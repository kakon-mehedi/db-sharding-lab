using System.Diagnostics;
using ShardingLab.Db;

namespace ShardingLab.Phases;

/// <summary>
/// Phase 10: every shard becomes a primary + replica pair. Writes always
/// target the primary; reads can be offloaded to the replica.
/// </summary>
public class Phase10_Replication
{
    private readonly ReplicatedShardStore _store = new(new ModuloShardRouter(shardCount: 4), replicationDelay: TimeSpan.FromMilliseconds(300));

    public void Run()
    {
        Console.WriteLine("=== Phase 10: Replication ===\n");

        _store.EnsureSchema();
        _store.ResetAll();

        Console.WriteLine("Writing 5 messages to conversation 42 (writes always go to the PRIMARY):");
        for (int i = 0; i < 5; i++)
            _store.Write(42, userId: 1, body: $"message #{i + 1}");
        Console.WriteLine("  done.\n");

        var immediateReplicaRead = _store.ReadFromReplica(42);
        Console.WriteLine($"Reading conversation 42 from its REPLICA immediately after writing: {immediateReplicaRead.Count} row(s) found.");

        _store.WaitForReplicationAsync().GetAwaiter().GetResult();
        var settledReplicaRead = _store.ReadFromReplica(42);
        Console.WriteLine($"Reading again after waiting for replication to catch up:            {settledReplicaRead.Count} row(s) found.\n");

        const int readCount = 20;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < readCount; i++) _store.ReadFromReplica(42);
        sw.Stop();
        Console.WriteLine($"{readCount} reads served entirely by the REPLICA in {sw.Elapsed.TotalMilliseconds:F1} ms — the primary did zero work for any of them.\n");

        Console.WriteLine("What changed:");
        Console.WriteLine(" - Every shard is now two databases: a primary that accepts writes, and a");
        Console.WriteLine("   replica that receives a copy shortly after.");
        Console.WriteLine(" - The immediate replica read came back with fewer rows than the settled");
        Console.WriteLine("   one — the write had happened but hadn't propagated yet. That gap is real");
        Console.WriteLine("   replication lag; Phase 11 measures it precisely.");
        Console.WriteLine(" - Splitting reads onto replicas is exactly how Postgres/MySQL read replicas");
        Console.WriteLine("   are used at most companies: writes are comparatively rare and must be");
        Console.WriteLine("   correct immediately, reads are frequent and can tolerate some staleness");
        Console.WriteLine("   in exchange for taking zero load off the primary.");
    }
}
