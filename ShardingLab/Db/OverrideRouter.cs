namespace ShardingLab.Db;

/// <summary>
/// Wraps a formula-based router with a small directory of manual overrides —
/// the only way to move a single key off its formula-assigned shard without
/// changing the formula (and thus every other key) too.
/// </summary>
public class OverrideRouter(ModuloShardRouter baseRouter)
{
    private readonly Dictionary<long, int> _overrides = new();

    public int ShardNumberFor(long key) => _overrides.GetValueOrDefault(key, baseRouter.ShardNumberFor(key));

    public void Override(long key, int shardNumber) => _overrides[key] = shardNumber;

    public IEnumerable<int> AllShardNumbers => baseRouter.AllShardNumbers;
}
