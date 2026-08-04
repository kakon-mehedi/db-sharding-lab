using System.Diagnostics;
using Dapper;
using Npgsql;
using ShardingLab.Db;

namespace ShardingLab.Phases;

/// <summary>
/// Phase 14: the numbers an on-call engineer would actually look at —
/// size, row count, QPS, latency — per shard, with hot shards flagged.
/// </summary>
public class Phase14_Monitoring
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

    private record ShardMetrics(int ShardNumber, long Rows, string Size, double Qps, double AvgLatencyMs);

    public void Run()
    {
        Console.WriteLine("=== Phase 14: Monitoring ===\n");

        SeedUnevenLoad();

        var metrics = new List<ShardMetrics>();
        foreach (var n in Enumerable.Range(1, ShardCount))
            metrics.Add(MeasureShard(n));

        double avgRows = metrics.Average(m => m.Rows);

        Console.WriteLine($"{"Shard",-8}{"Rows",12}{"Size",12}{"QPS",10}{"Avg Latency",15}  Status");
        foreach (var m in metrics)
        {
            bool hot = m.Rows > avgRows * 1.5;
            Console.WriteLine($"{$"shard{m.ShardNumber}",-8}{m.Rows,12:N0}{m.Size,12}{m.Qps,10:F1}{m.AvgLatencyMs,12:F2} ms  {(hot ? "<-- HOT" : "ok")}");
        }

        Console.WriteLine();
        Console.WriteLine("What this dashboard would tell an on-call engineer:");
        Console.WriteLine(" - Row count and size answer 'is this shard growing unusually fast?' —");
        Console.WriteLine("   the same signal Phase 7 and Phase 13 detected by hand, now automated.");
        Console.WriteLine(" - QPS and latency answer 'is this shard under more query pressure?' — a");
        Console.WriteLine("   shard can be hot on traffic without being hot on data size, and vice versa.");
        Console.WriteLine(" - A fixed 1.5x-of-average threshold is a simplification; production systems");
        Console.WriteLine("   (Vitess's VTGate metrics, Citus's citus_stat_statements, DataDog's DB");
        Console.WriteLine("   Monitoring) track these per-shard time series and alert on trend, not just");
        Console.WriteLine("   a snapshot — but the signals being watched are exactly these four.");
    }

    private static ShardMetrics MeasureShard(int shardNumber)
    {
        using var conn = new NpgsqlConnection(ConnectionStrings.Shard(shardNumber));
        conn.Open();

        long rows = conn.ExecuteScalar<long>("SELECT count(*) FROM messages;");
        string size = conn.ExecuteScalar<string>("SELECT pg_size_pretty(pg_total_relation_size('messages'));")!;

        const int sampleQueries = 200;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < sampleQueries; i++)
            conn.ExecuteScalar<long>("SELECT count(*) FROM messages WHERE conversation_id = @Id;", new { Id = (long)(i % 50 + 1) });
        sw.Stop();

        double qps = sampleQueries / sw.Elapsed.TotalSeconds;
        double avgLatencyMs = sw.Elapsed.TotalMilliseconds / sampleQueries;

        return new ShardMetrics(shardNumber, rows, size, qps, avgLatencyMs);
    }

    private static void SeedUnevenLoad()
    {
        for (int n = 1; n <= ShardCount; n++)
        {
            using var conn = new NpgsqlConnection(ConnectionStrings.Shard(n));
            conn.Open();
            conn.Execute(CreateTableSql);
            conn.Execute("TRUNCATE messages RESTART IDENTITY;");
        }

        var random = new Random(9);
        SeedShard(1, conversationCount: 400, messagesPerConversation: 20, random);
        SeedShard(2, conversationCount: 100, messagesPerConversation: 20, random);
        SeedShard(3, conversationCount: 100, messagesPerConversation: 20, random);
        SeedShard(4, conversationCount: 100, messagesPerConversation: 20, random);
    }

    private static void SeedShard(int shardNumber, int conversationCount, int messagesPerConversation, Random random)
    {
        using var conn = new NpgsqlConnection(ConnectionStrings.Shard(shardNumber));
        conn.Open();
        for (int c = 0; c < conversationCount; c++)
        for (int m = 0; m < messagesPerConversation; m++)
            conn.Execute(
                "INSERT INTO messages (conversation_id, user_id, body) VALUES (@ConversationId, @UserId, @Body);",
                new { ConversationId = (long)c, UserId = (long)random.Next(1, 200), Body = "hey" });
    }
}
