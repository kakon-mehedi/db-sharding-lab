namespace ShardingLab.Db;

/// <summary>
/// A deterministic hash (FNV-1a) over a long key. Built-in GetHashCode() is
/// deterministic for integers too, but spelling out the algorithm makes
/// "hash the key" a concrete, inspectable operation instead of a black box —
/// important once Phase 9 places these hashes onto a ring.
/// </summary>
public static class StableHash
{
    public static uint Of(long key)
    {
        const uint fnvOffsetBasis = 2166136261;
        const uint fnvPrime = 16777619;

        uint hash = fnvOffsetBasis;
        for (int shift = 0; shift < 64; shift += 8)
        {
            byte b = (byte)((key >> shift) & 0xFF);
            hash ^= b;
            hash *= fnvPrime;
        }
        return hash;
    }
}
