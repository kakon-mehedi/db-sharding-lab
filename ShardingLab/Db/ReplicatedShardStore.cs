using Dapper;
using Npgsql;

namespace ShardingLab.Db;

/// <summary>
/// Each shard gets a primary and a replica database. Writes go to the
/// primary; a background task copies the row to the replica after a
/// configurable delay. This is a deliberately simplified stand-in for real
/// Postgres streaming (WAL-shipping) replication — this lab's single local
/// server can't host multiple physical clusters — but the lag it produces
/// and the failure/promotion mechanics built on top of it are real.
/// </summary>
public class ReplicatedShardStore(ModuloShardRouter router, TimeSpan replicationDelay)
{
    private const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS messages (
            id              BIGSERIAL PRIMARY KEY,
            conversation_id BIGINT NOT NULL,
            user_id         BIGINT NOT NULL,
            body            TEXT NOT NULL,
            created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """;

    private readonly List<Task> _pendingReplication = [];
    private readonly Dictionary<int, string> _primaryOverride = new();
    private readonly HashSet<int> _simulatedOutage = [];

    public IEnumerable<int> AllShardNumbers => router.AllShardNumbers;

    public void EnsureSchema()
    {
        foreach (var shardNumber in router.AllShardNumbers)
        {
            using (var conn = new NpgsqlConnection(ConnectionStrings.Shard(shardNumber))) { conn.Open(); conn.Execute(CreateTableSql); }
            using (var conn = new NpgsqlConnection(ConnectionStrings.ShardReplica(shardNumber))) { conn.Open(); conn.Execute(CreateTableSql); }
        }
    }

    public void ResetAll()
    {
        foreach (var shardNumber in router.AllShardNumbers)
        {
            using (var conn = new NpgsqlConnection(ConnectionStrings.Shard(shardNumber))) { conn.Open(); conn.Execute("TRUNCATE messages RESTART IDENTITY;"); }
            using (var conn = new NpgsqlConnection(ConnectionStrings.ShardReplica(shardNumber))) { conn.Open(); conn.Execute("TRUNCATE messages RESTART IDENTITY;"); }
        }
        _pendingReplication.Clear();
        _primaryOverride.Clear();
        _simulatedOutage.Clear();
    }

    public long Write(long conversationId, long userId, string body)
    {
        int shardNumber = router.ShardNumberFor(conversationId);
        using var primary = OpenPrimary(shardNumber);
        long id = primary.ExecuteScalar<long>(
            "INSERT INTO messages (conversation_id, user_id, body) VALUES (@ConversationId, @UserId, @Body) RETURNING id;",
            new { ConversationId = conversationId, UserId = userId, Body = body });
        var createdAt = primary.ExecuteScalar<DateTime>("SELECT created_at FROM messages WHERE id = @Id;", new { Id = id });

        _pendingReplication.Add(ReplicateAsync(shardNumber, id, conversationId, userId, body, createdAt));
        return id;
    }

    private async Task ReplicateAsync(int shardNumber, long id, long conversationId, long userId, string body, DateTime createdAt)
    {
        await Task.Delay(replicationDelay);
        using var replica = new NpgsqlConnection(ConnectionStrings.ShardReplica(shardNumber));
        replica.Open();
        replica.Execute(
            "INSERT INTO messages (id, conversation_id, user_id, body, created_at) VALUES (@Id, @ConversationId, @UserId, @Body, @CreatedAt) " +
            "ON CONFLICT (id) DO NOTHING;",
            new { Id = id, ConversationId = conversationId, UserId = userId, Body = body, CreatedAt = createdAt });
    }

    public Task WaitForReplicationAsync() => Task.WhenAll(_pendingReplication);

    public List<Message> ReadFromPrimary(long conversationId)
    {
        int shardNumber = router.ShardNumberFor(conversationId);
        using var conn = OpenPrimary(shardNumber);
        return QueryConversation(conn, conversationId);
    }

    public List<Message> ReadFromReplica(long conversationId)
    {
        int shardNumber = router.ShardNumberFor(conversationId);
        using var conn = new NpgsqlConnection(ConnectionStrings.ShardReplica(shardNumber));
        conn.Open();
        return QueryConversation(conn, conversationId);
    }

    /// <summary>Simulates this shard's primary being unreachable, without touching real network state.</summary>
    public void SimulateOutage(int shardNumber) => _simulatedOutage.Add(shardNumber);
    public void ClearOutage(int shardNumber) => _simulatedOutage.Remove(shardNumber);

    /// <summary>Points this shard's "primary" at what used to be its replica — a routing/config change, not a data move.</summary>
    public void PromoteReplicaToPrimary(int shardNumber)
    {
        // Replicated rows are inserted with an explicit id (see ReplicateAsync), which never
        // advances the replica's own id sequence. Left alone, the first write after promotion
        // would collide with an id that already arrived via replication — a real gotcha in
        // actual failover too, not just this simulation. Resync the sequence before handing out writes.
        using (var conn = new NpgsqlConnection(ConnectionStrings.ShardReplica(shardNumber)))
        {
            conn.Open();
            conn.Execute("SELECT setval(pg_get_serial_sequence('messages', 'id'), COALESCE((SELECT MAX(id) FROM messages), 1));");
        }

        _primaryOverride[shardNumber] = ConnectionStrings.ShardReplica(shardNumber);
        _simulatedOutage.Remove(shardNumber);
    }

    public NpgsqlConnection OpenPrimary(int shardNumber)
    {
        if (_simulatedOutage.Contains(shardNumber))
            throw new NpgsqlException($"simulated outage: shard{shardNumber} primary is unreachable");

        string connectionString = _primaryOverride.GetValueOrDefault(shardNumber) ?? ConnectionStrings.Shard(shardNumber);
        var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        return conn;
    }

    private static List<Message> QueryConversation(NpgsqlConnection conn, long conversationId) =>
        conn.Query<Message>(
            """
            SELECT id AS Id, conversation_id AS ConversationId, user_id AS UserId,
                   body AS Body, created_at AS CreatedAt
            FROM messages WHERE conversation_id = @ConversationId ORDER BY id;
            """, new { ConversationId = conversationId }).ToList();
}
