using ShardingLab.Db;

namespace ShardingLab.Phases;

/// <summary>
/// Phase 9: same "add a 5th shard" scenario as Phase 8, but routed by a hash
/// ring instead of modulo. Pure computation — the point is the math, not the data.
/// </summary>
public class Phase09_ConsistentHashing
{
    private const int ConversationCount = 2_000;

    public void Run()
    {
        Console.WriteLine("=== Phase 9: Consistent Hashing ===\n");

        var oldModulo = new ModuloShardRouter(shardCount: 4);
        var newModulo = new ModuloShardRouter(shardCount: 5);
        int moduloMoved = CountMoved(ConversationCount, oldModulo.ShardNumberFor, newModulo.ShardNumberFor);

        var oldRing = new ConsistentHashRouter(Enumerable.Range(1, 4));
        var newRing = new ConsistentHashRouter(Enumerable.Range(1, 4));
        newRing.AddShard(5);
        int ringMoved = CountMoved(ConversationCount, oldRing.ShardNumberFor, newRing.ShardNumberFor);

        Console.WriteLine($"Adding a 5th shard to {ConversationCount:N0} conversations:\n");
        Console.WriteLine($"  Modulo routing (mod 4 -> mod 5):    {moduloMoved,6:N0} / {ConversationCount:N0} moved ({100.0 * moduloMoved / ConversationCount,5:F1}%)");
        Console.WriteLine($"  Consistent hashing (ring +1 node):  {ringMoved,6:N0} / {ConversationCount:N0} moved ({100.0 * ringMoved / ConversationCount,5:F1}%)");
        Console.WriteLine($"  Theoretical ideal (1/5 of keyspace): {ConversationCount / 5,6:N0} / {ConversationCount:N0} moved ( 20.0%)\n");

        VisualizeRing();

        Console.WriteLine();
        Console.WriteLine("Why so little data moves:");
        Console.WriteLine(" - Modulo routing ties EVERY key's shard number to the total shard count.");
        Console.WriteLine("   Change the count and the formula (key % N) changes output for almost");
        Console.WriteLine("   every key — there's no way to add capacity without a near-total reshuffle.");
        Console.WriteLine(" - A hash ring only asks 'what's the next virtual node clockwise from this");
        Console.WriteLine("   key?' Adding shard5's virtual nodes only intercepts the keys whose");
        Console.WriteLine("   nearest clockwise node USED to be further away — everyone else's answer");
        Console.WriteLine("   doesn't change, because their nearest node was never affected.");
        Console.WriteLine(" - This is exactly how DynamoDB, Cassandra, and Discord's own message store");
        Console.WriteLine("   distribute data across nodes — consistent hashing was literally invented");
        Console.WriteLine("   (Karger et al., 1997) to solve this exact 'adding a cache server reshuffles");
        Console.WriteLine("   everything' problem for web caches, and it generalizes directly to shards.");
    }

    private static int CountMoved(int conversationCount, Func<long, int> oldRoute, Func<long, int> newRoute)
    {
        int moved = 0;
        for (long c = 1; c <= conversationCount; c++)
            if (oldRoute(c) != newRoute(c)) moved++;
        return moved;
    }

    private static void VisualizeRing()
    {
        var ring = new ConsistentHashRouter(Enumerable.Range(1, 4));
        const int samples = 72;

        var before = new int[samples];
        for (int i = 0; i < samples; i++)
            before[i] = ring.ShardForPosition((uint)((ulong)i * uint.MaxValue / samples));

        ring.AddShard(5);
        var after = new int[samples];
        for (int i = 0; i < samples; i++)
            after[i] = ring.ShardForPosition((uint)((ulong)i * uint.MaxValue / samples));

        Console.WriteLine($"Ring ownership sampled at {samples} evenly spaced points around the ring:");
        Console.WriteLine("  before shard5: " + string.Concat(before.Select(s => s.ToString())));
        Console.WriteLine("  after  shard5: " + string.Concat(after.Select(s => s.ToString())));
        Console.WriteLine("                 " + string.Concat(Enumerable.Range(0, samples).Select(i => before[i] != after[i] ? "^" : " ")));

        int changed = before.Zip(after, (b, a) => b != a).Count(x => x);
        Console.WriteLine($"  {changed} / {samples} sample points changed owner ({100.0 * changed / samples:F1}%) — every one of them now points to shard5.");
    }
}
