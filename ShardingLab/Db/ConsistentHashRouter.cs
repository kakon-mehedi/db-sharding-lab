namespace ShardingLab.Db;

/// <summary>
/// Shards are placed at many points ("virtual nodes") around a ring of hash
/// values. A key belongs to whichever shard's virtual node is the next one
/// clockwise from the key's own hash. Adding a shard only steals the keys
/// that fall in the small arcs its new virtual nodes claim — everyone else's
/// nearest clockwise node is unchanged.
/// </summary>
public class ConsistentHashRouter
{
    private const int VirtualNodesPerShard = 100;
    private readonly SortedDictionary<uint, int> _ring = new();

    public ConsistentHashRouter(IEnumerable<int> shardNumbers)
    {
        foreach (var shardNumber in shardNumbers)
            AddShard(shardNumber);
    }

    public void AddShard(int shardNumber)
    {
        for (int v = 0; v < VirtualNodesPerShard; v++)
            _ring[StableHash.Of(shardNumber * 1_000_003L + v)] = shardNumber;
    }

    public int ShardNumberFor(long key) => ShardForPosition(StableHash.Of(key));

    public int ShardForPosition(uint position)
    {
        foreach (var (nodePosition, shardNumber) in _ring)
            if (nodePosition >= position) return shardNumber;
        return _ring.Values.First(); // wrapped past the last node back to the first
    }
}
