using System.Diagnostics;
using Dapper;
using Npgsql;
using NpgsqlTypes;
using ShardingLab.Db;

namespace ShardingLab.Phases;

/// <summary>
/// Phase 2: the same single database, but with millions of rows instead of four.
/// Same queries as Phase 1 — the code doesn't change, only the data volume does.
/// </summary>
public class Phase02_ScaleLimits
{
    private const long TargetRowCount = 3_000_000;
    private const int ConversationCount = 200_000; // ~15 messages/conversation on average
    private const int UserCount = 50_000;

    private static readonly string[] WordBank =
    [
        "hey", "meeting moved to 3pm", "thanks!", "lunch?", "calling you now",
        "running late, sorry", "sounds good", "on my way", "see you soon",
        "got it", "sure thing", "no problem", "let's sync tomorrow",
        "quick question", "ok", "sounds great", "can't talk right now", "brb"
    ];

    private const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS messages_huge (
            id              BIGSERIAL PRIMARY KEY,
            conversation_id BIGINT NOT NULL,
            user_id         BIGINT NOT NULL,
            body            TEXT NOT NULL,
            created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """;

    public void Run()
    {
        Console.WriteLine("=== Phase 2: The Database Gets Huge ===\n");

        using var conn = new NpgsqlConnection(ConnectionStrings.SingleDb);
        conn.Open();
        conn.Execute(CreateTableSql);
        conn.Execute("DROP INDEX IF EXISTS idx_messages_huge_conversation;"); // start each run unindexed

        EnsureRowsGenerated(conn);

        Console.WriteLine();
        var timings = new List<(string Label, double Ms)>();

        timings.Add(Time("COUNT(*) over the whole table", () =>
            conn.ExecuteScalar<long>("SELECT count(*) FROM messages_huge;")));

        timings.Add(Time("GetMessage(id=1) — primary key lookup", () =>
            conn.QuerySingleOrDefault<Message>(
                """
                SELECT id AS Id, conversation_id AS ConversationId, user_id AS UserId,
                       body AS Body, created_at AS CreatedAt
                FROM messages_huge WHERE id = 1;
                """)));

        const long sampleConversationId = 4242; // known to exist: generated in range [0, ConversationCount)

        timings.Add(Time($"GetConversationMessages({sampleConversationId}) — NO index yet", () =>
            conn.Query<Message>(
                """
                SELECT id AS Id, conversation_id AS ConversationId, user_id AS UserId,
                       body AS Body, created_at AS CreatedAt
                FROM messages_huge WHERE conversation_id = @ConversationId ORDER BY id;
                """, new { ConversationId = sampleConversationId }).ToList()));

        timings.Add(Time("CREATE INDEX ON messages_huge(conversation_id)", () =>
            conn.Execute("CREATE INDEX idx_messages_huge_conversation ON messages_huge(conversation_id);")));

        timings.Add(Time($"GetConversationMessages({sampleConversationId}) — WITH index", () =>
            conn.Query<Message>(
                """
                SELECT id AS Id, conversation_id AS ConversationId, user_id AS UserId,
                       body AS Body, created_at AS CreatedAt
                FROM messages_huge WHERE conversation_id = @ConversationId ORDER BY id;
                """, new { ConversationId = sampleConversationId }).ToList()));

        timings.Add(Time("Keyword search: body ILIKE '%zzzsearchterm%' (index can't help)", () =>
            conn.Query<Message>(
                "SELECT id AS Id, conversation_id AS ConversationId, user_id AS UserId, body AS Body, created_at AS CreatedAt " +
                "FROM messages_huge WHERE body ILIKE '%zzzsearchterm%';").ToList()));

        Console.WriteLine("\n--- Timings ---");
        foreach (var (label, ms) in timings)
            Console.WriteLine($"{ms,9:F1} ms  {label}");

        Console.WriteLine();
        Console.WriteLine("Why vertical scaling eventually fails:");
        Console.WriteLine(" - The PK lookup stayed fast: an index turns 3,000,000 rows into a");
        Console.WriteLine("   handful of page reads, same as it was with 4 rows.");
        Console.WriteLine(" - The conversation lookup was slow with no index — Postgres had no");
        Console.WriteLine("   choice but to scan every row. Adding the index fixed THIS query.");
        Console.WriteLine(" - But the index didn't fix the keyword search — no index (short of a");
        Console.WriteLine("   dedicated full-text index) can skip rows when the predicate is a");
        Console.WriteLine("   substring match. That query scans all 3,000,000 rows no matter how");
        Console.WriteLine("   fast or expensive the single machine underneath it is.");
        Console.WriteLine(" - Every fix so far is bound to ONE machine's CPU, RAM and disk I/O.");
        Console.WriteLine("   A bigger box buys headroom, not a ceiling removal — and cloud");
        Console.WriteLine("   instance pricing gets steep well before you approach that ceiling.");
    }

    private static (string, double) Time(string label, Action work)
    {
        var sw = Stopwatch.StartNew();
        work();
        sw.Stop();
        return (label, sw.Elapsed.TotalMilliseconds);
    }

    private static (string, double) Time<T>(string label, Func<T> work) => Time(label, () => { work(); });

    private static void EnsureRowsGenerated(NpgsqlConnection conn)
    {
        long existing = conn.ExecuteScalar<long>("SELECT count(*) FROM messages_huge;");
        if (existing >= TargetRowCount)
        {
            Console.WriteLine($"messages_huge already has {existing:N0} rows. Skipping generation.");
            return;
        }

        long toGenerate = TargetRowCount - existing;
        Console.WriteLine($"messages_huge has {existing:N0} rows. Generating {toGenerate:N0} more via COPY " +
                           "(row-by-row INSERTs would take far too long at this volume)...");

        var sw = Stopwatch.StartNew();
        var random = new Random(42);

        using (var writer = conn.BeginBinaryImport(
            "COPY messages_huge (conversation_id, user_id, body, created_at) FROM STDIN (FORMAT BINARY)"))
        {
            var now = DateTime.UtcNow;
            for (long i = 0; i < toGenerate; i++)
            {
                writer.StartRow();
                writer.Write((long)random.Next(0, ConversationCount), NpgsqlDbType.Bigint);
                writer.Write((long)random.Next(0, UserCount), NpgsqlDbType.Bigint);
                writer.Write(WordBank[random.Next(WordBank.Length)], NpgsqlDbType.Text);
                writer.Write(now, NpgsqlDbType.TimestampTz);

                if (i > 0 && i % 1_000_000 == 0)
                    Console.WriteLine($"  ...{i:N0} rows written ({sw.Elapsed.TotalSeconds:F1}s elapsed)");
            }
            writer.Complete();
        }

        Console.WriteLine($"Generated {toGenerate:N0} rows in {sw.Elapsed.TotalSeconds:F1}s.");
    }
}
