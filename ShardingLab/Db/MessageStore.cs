using Dapper;
using Npgsql;

namespace ShardingLab.Db;

/// <summary>
/// The abstraction Phase 3 was missing: callers supply only a conversation_id.
/// Where that conversation physically lives — which shard, which connection
/// string — is this class's problem, never the caller's.
/// </summary>
public class MessageStore(ModuloShardRouter router)
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

    public IEnumerable<int> AllShardNumbers => router.AllShardNumbers;

    public void EnsureSchema()
    {
        foreach (var shardNumber in router.AllShardNumbers)
        {
            using var conn = OpenShardByNumber(shardNumber);
            conn.Execute(CreateTableSql);
        }
    }

    public void ResetAllShards()
    {
        foreach (var shardNumber in router.AllShardNumbers)
        {
            using var conn = OpenShardByNumber(shardNumber);
            conn.Execute("TRUNCATE messages RESTART IDENTITY;");
        }
    }

    public long InsertMessage(long conversationId, long userId, string body)
    {
        using var conn = OpenShardFor(conversationId);
        return conn.ExecuteScalar<long>(
            "INSERT INTO messages (conversation_id, user_id, body) VALUES (@ConversationId, @UserId, @Body) RETURNING id;",
            new { ConversationId = conversationId, UserId = userId, Body = body });
    }

    public List<Message> GetConversationMessages(long conversationId)
    {
        using var conn = OpenShardFor(conversationId);
        return conn.Query<Message>(
            """
            SELECT id AS Id, conversation_id AS ConversationId, user_id AS UserId,
                   body AS Body, created_at AS CreatedAt
            FROM messages WHERE conversation_id = @ConversationId ORDER BY id;
            """, new { ConversationId = conversationId }).ToList();
    }

    public int ShardNumberFor(long conversationId) => router.ShardNumberFor(conversationId);

    public NpgsqlConnection OpenShardByNumber(int shardNumber)
    {
        var conn = new NpgsqlConnection(ConnectionStrings.Shard(shardNumber));
        conn.Open();
        return conn;
    }

    private NpgsqlConnection OpenShardFor(long conversationId)
    {
        var conn = new NpgsqlConnection(router.ConnectionStringFor(conversationId));
        conn.Open();
        return conn;
    }
}
