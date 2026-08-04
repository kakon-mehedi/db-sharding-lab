namespace ShardingLab.Db;

/// <summary>
/// The simplest possible router: shard index = key % shard count.
/// Deterministic, stateless, no lookup table needed — but as Phase 8 shows,
/// changing the shard count reshuffles almost every key.
/// </summary>
public class ModuloShardRouter(int shardCount)
{
    public int ShardCount { get; } = shardCount;

    public int ShardNumberFor(long key) => (int)(key % ShardCount) + 1; // shards are numbered 1..N

    public string ConnectionStringFor(long key) => ConnectionStrings.Shard(ShardNumberFor(key));

    public IEnumerable<int> AllShardNumbers => Enumerable.Range(1, ShardCount);
}
