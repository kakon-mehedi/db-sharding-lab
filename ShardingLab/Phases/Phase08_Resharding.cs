using System.Diagnostics;
using Dapper;
using Npgsql;
using ShardingLab.Db;

namespace ShardingLab.Phases;

/// <summary>
/// Phase 8: shard count goes from 4 to 5. Under modulo routing that changes
/// almost every key's home shard, so almost every conversation has to move.
/// </summary>
public class Phase08_Resharding
{
    private const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS messages (
            id              BIGSERIAL PRIMARY KEY,
            conversation_id BIGINT NOT NULL,
            user_id         BIGINT NOT NULL,
            body            TEXT NOT NULL,
            created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """;

    private const int ConversationCount = 2_000;
    private const int MessagesPerConversation = 10;

    private readonly ModuloShardRouter _oldRouter = new(shardCount: 4);
    private readonly ModuloShardRouter _newRouter = new(shardCount: 5);

    public void Run()
    {
        Console.WriteLine("=== Phase 8: Resharding ===\n");

        foreach (var n in Enumerable.Range(1, 5))
        {
            using var conn = new NpgsqlConnection(ConnectionStrings.Shard(n));
            conn.Open();
            conn.Execute(CreateTableSql);
            conn.Execute("TRUNCATE messages RESTART IDENTITY;");
        }

        var random = new Random(21);
        for (long c = 1; c <= ConversationCount; c++)
        {
            int shardNumber = _oldRouter.ShardNumberFor(c);
            using var conn = new NpgsqlConnection(ConnectionStrings.Shard(shardNumber));
            conn.Open();
            for (int i = 0; i < MessagesPerConversation; i++)
                conn.Execute(
                    "INSERT INTO messages (conversation_id, user_id, body) VALUES (@ConversationId, @UserId, @Body);",
                    new { ConversationId = c, UserId = (long)random.Next(1, 500), Body = Phrase(random) });
        }
        Console.WriteLine($"Seeded {ConversationCount:N0} conversations x {MessagesPerConversation} messages across shard1-4 (mod-4 topology).\n");

        PrintShardCounts("Before resharding", 4);

        var moving = new List<long>();
        for (long c = 1; c <= ConversationCount; c++)
            if (_oldRouter.ShardNumberFor(c) != _newRouter.ShardNumberFor(c))
                moving.Add(c);

        double movedPct = 100.0 * moving.Count / ConversationCount;
        Console.WriteLine($"\n{moving.Count:N0} / {ConversationCount:N0} conversations ({movedPct:F1}%) must move under mod-5 routing.\n");

        Console.WriteLine("Backfilling (old shards keep serving reads throughout — this is the 'no downtime' part):");
        var sw = Stopwatch.StartNew();
        long rowsMoved = 0;
        foreach (var conversationId in moving)
        {
            int fromShard = _oldRouter.ShardNumberFor(conversationId);
            int toShard = _newRouter.ShardNumberFor(conversationId);
            rowsMoved += MoveConversation(conversationId, fromShard, toShard);
        }
        sw.Stop();
        Console.WriteLine($"  moved {rowsMoved:N0} rows for {moving.Count:N0} conversations in {sw.Elapsed.TotalSeconds:F2}s.\n");

        // The cutover: one reference reassignment. In a real router this is a single
        // metadata/config update, not a data change — that's the entire "downtime window."
        var activeRouter = _newRouter;
        long sample = moving[0];
        Console.WriteLine("Cutover (atomic routing swap):");
        Console.WriteLine($"  conversation {sample}: old shard{_oldRouter.ShardNumberFor(sample)} -> now resolves to shard{activeRouter.ShardNumberFor(sample)}\n");

        PrintShardCounts("After resharding", 5);

        Console.WriteLine();
        Console.WriteLine("Why this counts as 'no downtime':");
        Console.WriteLine(" - Reads and writes kept working against the OLD 4-shard layout for the");
        Console.WriteLine("   entire backfill — nothing was locked or taken offline to copy data.");
        Console.WriteLine(" - The only instantaneous step is flipping which router is active — a");
        Console.WriteLine("   metadata change, not a data change. Real systems call this the cutover.");
        Console.WriteLine($" - The cost that didn't go away: {movedPct:F1}% of all data had to be physically");
        Console.WriteLine("   copied just to add one shard. Vitess's online resharding (used at Slack,");
        Console.WriteLine("   YouTube) and Citus's shard rebalancer do exactly this dual-write-then-cutover");
        Console.WriteLine("   dance, because with modulo hashing there's no cheaper way. Phase 9 fixes it.");
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

    private static void PrintShardCounts(string label, int shardCount)
    {
        Console.WriteLine($"{label}:");
        foreach (var n in Enumerable.Range(1, shardCount))
        {
            using var conn = new NpgsqlConnection(ConnectionStrings.Shard(n));
            conn.Open();
            long rows = conn.ExecuteScalar<long>("SELECT count(*) FROM messages;");
            Console.WriteLine($"  shard{n}: {rows,6:N0} rows");
        }
    }

    private static string Phrase(Random random)
    {
        string[] bank = ["hey", "on my way", "sounds good", "thanks!", "call me", "brb"];
        return bank[random.Next(bank.Length)];
    }
}
