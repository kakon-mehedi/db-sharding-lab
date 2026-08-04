using System.Diagnostics;
using Dapper;
using Npgsql;
using ShardingLab.Db;

namespace ShardingLab.Phases;

/// <summary>
/// Phase 15: write throughput, single database vs 4 shards, at increasing scale.
/// Batched multi-row INSERTs (not COPY) — this measures realistic app-level
/// write operations, the same kind every earlier phase actually issued.
/// </summary>
public class Phase15_Benchmark
{
    private static readonly int[] OperationCounts = [100, 1_000, 10_000, 100_000, 1_000_000];
    private const int BatchSize = 500;
    private const int ShardCount = 4;

    private const string CreateShardTableSql = """
        CREATE TABLE IF NOT EXISTS messages (
            id              BIGSERIAL PRIMARY KEY,
            conversation_id BIGINT NOT NULL,
            user_id         BIGINT NOT NULL,
            body            TEXT NOT NULL,
            created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """;

    private const string CreateSingleTableSql = """
        CREATE TABLE IF NOT EXISTS bench_messages (
            id              BIGSERIAL PRIMARY KEY,
            conversation_id BIGINT NOT NULL,
            user_id         BIGINT NOT NULL,
            body            TEXT NOT NULL,
            created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """;

    public void Run()
    {
        Console.WriteLine("=== Phase 15: Benchmark ===\n");
        Console.WriteLine("Write throughput: 1 database vs 4 shards written concurrently.\n");
        Console.WriteLine($"{"Operations",12}{"Single-DB",16}{"Sharded (x4)",16}{"Speedup",10}");

        foreach (var n in OperationCounts)
        {
            double singleSeconds = BenchmarkSingleDb(n);
            double shardedSeconds = BenchmarkSharded(n);
            double singleOpsPerSec = n / singleSeconds;
            double shardedOpsPerSec = n / shardedSeconds;

            Console.WriteLine($"{n,12:N0}{singleOpsPerSec,13:N0} op/s{shardedOpsPerSec,13:N0} op/s{shardedOpsPerSec / singleOpsPerSec,9:F1}x");
        }

        Console.WriteLine();
        Console.WriteLine("What the table shows:");
        Console.WriteLine(" - Sharded writes pull ahead as N grows: 4 independent connections (backed");
        Console.WriteLine("   by 4 independent Postgres backend processes) commit concurrently, while");
        Console.WriteLine("   the single-DB path serializes every batch through one connection.");
        Console.WriteLine(" - All 5 databases here still share ONE physical machine's disk and CPU —");
        Console.WriteLine("   so any speedup shown is a LOWER BOUND. In production each shard usually");
        Console.WriteLine("   lives on its own machine with its own disk and network link, so the real");
        Console.WriteLine("   gap is typically larger than a single laptop can demonstrate.");
        Console.WriteLine(" - This is the concrete payoff behind every earlier phase's cost: routing");
        Console.WriteLine("   (Phase 3/5), picking the right shard key (Phase 4), and the operational");
        Console.WriteLine("   overhead of resharding, rebalancing and monitoring (Phases 8, 13, 14) —");
        Console.WriteLine("   all in service of this number actually getting better as data grows,");
        Console.WriteLine("   instead of Phase 2's single database getting worse.");
    }

    private static double BenchmarkSingleDb(int n)
    {
        using var conn = new NpgsqlConnection(ConnectionStrings.For("shardlab_single"));
        conn.Open();
        conn.Execute(CreateSingleTableSql);
        conn.Execute("TRUNCATE bench_messages RESTART IDENTITY;");

        var sw = Stopwatch.StartNew();
        InsertBatched(conn, "bench_messages", n, conversationIdOffset: 0);
        sw.Stop();
        return sw.Elapsed.TotalSeconds;
    }

    private static double BenchmarkSharded(int n)
    {
        for (int s = 1; s <= ShardCount; s++)
        {
            using var conn = new NpgsqlConnection(ConnectionStrings.Shard(s));
            conn.Open();
            conn.Execute(CreateShardTableSql);
            conn.Execute("TRUNCATE messages RESTART IDENTITY;");
        }

        int perShard = n / ShardCount;
        int remainder = n % ShardCount;

        var sw = Stopwatch.StartNew();
        Parallel.For(0, ShardCount, s =>
        {
            using var conn = new NpgsqlConnection(ConnectionStrings.Shard(s + 1));
            conn.Open();
            int count = perShard + (s < remainder ? 1 : 0);
            InsertBatched(conn, "messages", count, conversationIdOffset: s * 1_000_000L);
        });
        sw.Stop();
        return sw.Elapsed.TotalSeconds;
    }

    private static void InsertBatched(NpgsqlConnection conn, string tableName, int count, long conversationIdOffset)
    {
        var random = new Random(1);
        int written = 0;

        while (written < count)
        {
            int batch = Math.Min(BatchSize, count - written);
            var values = new List<string>(batch);
            var parameters = new DynamicParameters();

            for (int i = 0; i < batch; i++)
            {
                values.Add($"(@c{i}, @u{i}, @b{i})");
                parameters.Add($"c{i}", conversationIdOffset + (written + i) % 500);
                parameters.Add($"u{i}", random.Next(1, 500));
                parameters.Add($"b{i}", "benchmark message");
            }

            conn.Execute(
                $"INSERT INTO {tableName} (conversation_id, user_id, body) VALUES {string.Join(",", values)};",
                parameters);
            written += batch;
        }
    }
}
