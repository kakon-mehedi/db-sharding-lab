using Npgsql;
using ShardingLab.Db;

namespace ShardingLab.Phases;

/// <summary>
/// Phase 12: a shard's primary goes down (simulated — this lab has no
/// spare hardware to actually kill, and this Postgres server hosts other
/// real databases we shouldn't touch). We retry, fall back to reading the
/// replica, then promote it so writes can resume.
/// </summary>
public class Phase12_ShardFailure
{
    private readonly ModuloShardRouter _router = new(shardCount: 4);
    private readonly ReplicatedShardStore _store;

    public Phase12_ShardFailure() => _store = new ReplicatedShardStore(_router, replicationDelay: TimeSpan.FromMilliseconds(200));

    public void Run()
    {
        Console.WriteLine("=== Phase 12: Shard Failure ===\n");

        _store.EnsureSchema();
        _store.ResetAll();

        const long conversationId = 55;
        int shardNumber = _router.ShardNumberFor(conversationId);

        _store.Write(conversationId, 1, "before the outage");
        _store.WaitForReplicationAsync().GetAwaiter().GetResult();
        Console.WriteLine($"Conversation {conversationId} lives on shard{shardNumber}. Wrote one message and let it replicate normally.\n");

        Console.WriteLine($"Simulating shard{shardNumber}'s primary going down...\n");
        _store.SimulateOutage(shardNumber);

        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Console.WriteLine($"  attempt {attempt}: writing to primary...");
                _store.Write(conversationId, 2, "trying to write during the outage");
                Console.WriteLine("  write succeeded (unexpected).");
                break;
            }
            catch (NpgsqlException ex)
            {
                Console.WriteLine($"  attempt {attempt} failed: {ex.Message}");
                if (attempt < maxAttempts) Thread.Sleep(200 * attempt);
            }
        }

        Console.WriteLine("\nWrites are failing, but reads can still be served — falling back to the replica:");
        foreach (var m in _store.ReadFromReplica(conversationId))
            Console.WriteLine($"  [{m.Id}] {m.Body}");

        Console.WriteLine($"\nPromoting shard{shardNumber}'s replica to primary so writes can resume...");
        _store.PromoteReplicaToPrimary(shardNumber);
        long newId = _store.Write(conversationId, 3, "writing again after promotion");
        Console.WriteLine($"  write succeeded, new message id {newId}, now served by the promoted node.\n");

        var afterPromotion = _store.ReadFromPrimary(conversationId);
        Console.WriteLine($"ReadFromPrimary({conversationId}) after promotion: {afterPromotion.Count} row(s) (served by the former replica).\n");

        Console.WriteLine("What real systems do here:");
        Console.WriteLine(" - Retries with backoff absorb brief blips (a deploy, a network hiccup)");
        Console.WriteLine("   without paging anyone — but they don't help when the primary is really gone.");
        Console.WriteLine(" - Falling back to a replica for READS keeps the app partially working;");
        Console.WriteLine("   serving stale data is a smaller problem than a hard outage.");
        Console.WriteLine(" - Promoting a replica to primary is how Postgres/MySQL failover actually");
        Console.WriteLine("   works — Patroni, pg_auto_failover, and AWS RDS Multi-AZ all do this");
        Console.WriteLine("   automatically. It's a routing/config change, not a data migration, which");
        Console.WriteLine("   is why failover completes in seconds instead of the minutes resharding takes.");
        Console.WriteLine(" - Promotion also had to resync the promoted node's id sequence — rows that");
        Console.WriteLine("   arrived via replication carry an explicit id, so they never advance the");
        Console.WriteLine("   local sequence, and the next write would otherwise collide with one of them.");
        Console.WriteLine(" - The promoted node only has whatever it had already replicated. Any write");
        Console.WriteLine("   still in flight when the primary died can be lost — the real, uncomfortable");
        Console.WriteLine("   tradeoff async replication makes in exchange for low write latency.");
    }
}
