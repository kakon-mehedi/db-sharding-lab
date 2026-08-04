namespace ShardingLab.Db;

/// <summary>
/// Every database in this lab lives on the same local Postgres server.
/// "Sharding" is nothing more than picking a different connection string —
/// there is no cluster, no network topology, no container orchestration.
/// Keeping that visible in one place is the point.
/// </summary>
public static class ConnectionStrings
{
    private const string Host = "127.0.0.1";
    private const string User = "shardlab";
    private const string Password = "shardlab";

    public static string For(string database) =>
        $"Host={Host};Username={User};Password={Password};Database={database}";

    // Phase 1: one database holds everything.
    public static string SingleDb => For("shardlab_single");

    // Phase 3+: four (later five) independent shard databases.
    public static string Shard(int number) => For($"shard{number}");

    // Phase 10+: one replica database per shard.
    public static string ShardReplica(int number) => For($"shard{number}_replica");
}
