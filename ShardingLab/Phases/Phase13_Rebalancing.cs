using Dapper;
using Npgsql;
using ShardingLab.Db;

namespace ShardingLab.Phases;

/// <summary>
/// Phase 13: shard1 has drifted overloaded (organic growth, not a bad hash
/// function). A rebalancer detects the imbalance and moves whole
/// conversations to a colder shard, recording each move in an override
/// directory so the formula doesn't just route them right back.
/// </summary>
public class Phase13_Rebalancing
{
    private const int ShardCount = 4;
    private const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS messages (
            id              BIGSERIAL PRIMARY KEY,
            conversation_id BIGINT NOT NULL,
            user_id         BIGINT NOT NULL,
            body            TEXT NOT NULL,
            created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """;

    private readonly OverrideRouter _router = new(new ModuloShardRouter(ShardCount));
    private readonly Dictionary<long, int> _conversationShard = new();

    public void Run()
    {
        Console.WriteLine("=== Phase 13: Rebalancing ===\n");

        for (int n = 1; n <= ShardCount; n++)
        {
            using var conn = new NpgsqlConnection(ConnectionStrings.Shard(n));
            conn.Open();
            conn.Execute(CreateTableSql);
            conn.Execute("TRUNCATE messages RESTART IDENTITY;");
        }

        var random = new Random(5);
        long nextConversationId = 1;
        SeedShard(1, conversationCount: 300, messagesPerConversation: 20, random, ref nextConversationId);
        SeedShard(2, conversationCount: 100, messagesPerConversation: 20, random, ref nextConversationId);
        SeedShard(3, conversationCount: 100, messagesPerConversation: 20, random, ref nextConversationId);
        SeedShard(4, conversationCount: 100, messagesPerConversation: 20, random, ref nextConversationId);

        foreach (var (conversationId, shardNumber) in _conversationShard)
            _router.Override(conversationId, shardNumber);

        Console.WriteLine("Row counts before rebalancing:");
        var before = PrintShardCounts();

        double average = before.Values.Average();
        int hotShard = before.MaxBy(kv => kv.Value).Key;
        int coldShard = before.MinBy(kv => kv.Value).Key;
        Console.WriteLine($"\nDetected imbalance: shard{hotShard} has {before[hotShard]:N0} rows vs an average of {average:N0}.");
        Console.WriteLine($"Moving whole conversations from shard{hotShard} to shard{coldShard} until balanced.\n");

        var candidates = _conversationShard.Where(kv => kv.Value == hotShard).Select(kv => kv.Key).OrderBy(x => x).ToList();
        long rowsMoved = 0;
        int movedCount = 0;
        long hotRemaining = before[hotShard];

        foreach (var conversationId in candidates)
        {
            if (hotRemaining <= average) break;
            long size = MoveConversation(conversationId, hotShard, coldShard);
            rowsMoved += size;
            hotRemaining -= size;
            movedCount++;
            _router.Override(conversationId, coldShard);
        }

        Console.WriteLine($"Moved {movedCount} conversations ({rowsMoved:N0} rows) from shard{hotShard} to shard{coldShard}.\n");

        Console.WriteLine("Row counts after rebalancing:");
        PrintShardCounts();

        Console.WriteLine();
        Console.WriteLine("Why this needs a directory, not just a formula:");
        Console.WriteLine($" - conversation_id % 4 would route these conversations right back to shard{hotShard}");
        Console.WriteLine("   the instant it was recomputed — the formula has no memory of the move.");
        Console.WriteLine("   An override directory (conversation_id -> actual shard) is what makes");
        Console.WriteLine("   selective movement possible at all.");
        Console.WriteLine(" - This is exactly how Vitess (VSchema routing rules) and Google Spanner");
        Console.WriteLine("   (directory-based placement) support rebalancing: a lookup layer sits in");
        Console.WriteLine("   front of, or replaces, the pure hash function so any individual key can");
        Console.WriteLine("   be relocated independently, without touching anyone else's key.");
        Console.WriteLine(" - We moved whole conversations, never split one — a partial split would");
        Console.WriteLine("   reintroduce Phase 6's scatter-gather tax for the one query type");
        Console.WriteLine("   (GetConversationMessages) sharding was supposed to keep cheap.");
    }

    private void SeedShard(int shardNumber, int conversationCount, int messagesPerConversation, Random random, ref long nextConversationId)
    {
        using var conn = new NpgsqlConnection(ConnectionStrings.Shard(shardNumber));
        conn.Open();

        for (int i = 0; i < conversationCount; i++)
        {
            long conversationId = nextConversationId++;
            _conversationShard[conversationId] = shardNumber;
            for (int m = 0; m < messagesPerConversation; m++)
            {
                conn.Execute(
                    "INSERT INTO messages (conversation_id, user_id, body) VALUES (@ConversationId, @UserId, @Body);",
                    new { ConversationId = conversationId, UserId = (long)random.Next(1, 200), Body = Phrase(random) });
            }
        }
    }

    private static long MoveConversation(long conversationId, int fromShard, int toShard)
    {
        using var fromConn = new NpgsqlConnection(ConnectionStrings.Shard(fromShard));
        fromConn.Open();
        var rows = fromConn.Query(
            "SELECT user_id, body, created_at FROM messages WHERE conversation_id = @ConversationId;",
            new { ConversationId = conversationId }).ToList();

        using var toConn = new NpgsqlConnection(ConnectionStrings.Shard(toShard));
        toConn.Open();
        foreach (var row in rows)
        {
            toConn.Execute(
                "INSERT INTO messages (conversation_id, user_id, body, created_at) VALUES (@ConversationId, @UserId, @Body, @CreatedAt);",
                new { ConversationId = conversationId, UserId = (long)row.user_id, Body = (string)row.body, CreatedAt = (DateTime)row.created_at });
        }

        fromConn.Execute("DELETE FROM messages WHERE conversation_id = @ConversationId;", new { ConversationId = conversationId });
        return rows.Count;
    }

    private static Dictionary<int, long> PrintShardCounts()
    {
        var counts = new Dictionary<int, long>();
        for (int n = 1; n <= ShardCount; n++)
        {
            using var conn = new NpgsqlConnection(ConnectionStrings.Shard(n));
            conn.Open();
            long rows = conn.ExecuteScalar<long>("SELECT count(*) FROM messages;");
            counts[n] = rows;
            Console.WriteLine($"  shard{n}: {rows,6:N0} rows");
        }
        return counts;
    }

    private static string Phrase(Random random)
    {
        string[] bank = ["hey", "on my way", "sounds good", "thanks!", "call me", "brb"];
        return bank[random.Next(bank.Length)];
    }
}
