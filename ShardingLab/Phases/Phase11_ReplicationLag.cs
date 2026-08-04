using System.Diagnostics;
using ShardingLab.Db;

namespace ShardingLab.Phases;

/// <summary>
/// Phase 11: how long is the window where a replica read can return stale
/// (or missing) data? We measure it directly instead of asserting it exists.
/// </summary>
public class Phase11_ReplicationLag
{
    private readonly ReplicatedShardStore _store = new(new ModuloShardRouter(shardCount: 4), replicationDelay: TimeSpan.FromMilliseconds(1500));

    public void Run()
    {
        Console.WriteLine("=== Phase 11: Replication Lag ===\n");

        _store.EnsureSchema();
        _store.ResetAll();

        long id = _store.Write(77, userId: 1, body: "the meeting is cancelled");
        Console.WriteLine($"Wrote message {id} to conversation 77's PRIMARY. Polling its REPLICA until the row appears:\n");

        var sw = Stopwatch.StartNew();
        int pollCount = 0;
        while (true)
        {
            pollCount++;
            var rows = _store.ReadFromReplica(77);
            Console.WriteLine($"  [{sw.Elapsed.TotalMilliseconds,7:F0} ms] poll #{pollCount}: replica has {rows.Count} row(s)");
            if (rows.Count > 0) break;
            Thread.Sleep(200);
        }
        sw.Stop();

        Console.WriteLine($"\nThe replica became consistent with the primary after {sw.Elapsed.TotalMilliseconds:F0} ms — that gap IS replication lag.\n");

        Console.WriteLine("What eventual consistency means here:");
        Console.WriteLine(" - The PRIMARY read was correct the entire time — the row existed there");
        Console.WriteLine("   instantly. Only a REPLICA read during the window above could observe a");
        Console.WriteLine("   stale (missing) result.");
        Console.WriteLine(" - This is CAP theorem in miniature: during the lag window, that replica");
        Console.WriteLine("   can answer immediately (available) or wait for the real data (consistent),");
        Console.WriteLine("   but not both at the same instant.");
        Console.WriteLine(" - Instagram and Facebook's MySQL fleets run exactly this pattern: a user");
        Console.WriteLine("   who just posted is routed to read their OWN write from the primary (or a");
        Console.WriteLine("   short-lived cache) for a few seconds, specifically to paper over this lag");
        Console.WriteLine("   window before falling back to cheaper replica reads for everyone else.");
    }
}
