using Dapper;
using Npgsql;
using ShardingLab.Db;

namespace ShardingLab.Phases;

/// <summary>
/// Phase 1: everything lives in one Postgres database. This is the baseline
/// every later phase gets compared against.
/// </summary>
public class Phase01_SingleDb
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

    public void Run()
    {
        Console.WriteLine("=== Phase 1: Single Database ===\n");

        using var conn = new NpgsqlConnection(ConnectionStrings.SingleDb);
        conn.Open();
        conn.Execute(CreateTableSql);
        conn.Execute("TRUNCATE messages RESTART IDENTITY;"); // clean slate each run

        Console.WriteLine("Table 'messages' ready in database 'shardlab_single'.\n");

        // --- Insert a handful of messages across two conversations ---
        long conversationA = 1001;
        long conversationB = 2002;

        long m1 = InsertMessage(conn, conversationA, userId: 1, body: "hey, are you free tonight?");
        long m2 = InsertMessage(conn, conversationA, userId: 2, body: "yeah, what's up?");
        long m3 = InsertMessage(conn, conversationA, userId: 1, body: "want to grab dinner?");
        long m4 = InsertMessage(conn, conversationB, userId: 3, body: "standup moved to 10am");

        Console.WriteLine($"Inserted messages: {m1}, {m2}, {m3} (conversation {conversationA}), {m4} (conversation {conversationB})\n");

        // --- Get Message: point lookup by primary key ---
        var single = GetMessage(conn, m2);
        Console.WriteLine($"GetMessage({m2}) -> [{single!.UserId}] {single.Body}\n");

        // --- Get Conversation Messages: all messages for one conversation, in order ---
        var thread = GetConversationMessages(conn, conversationA);
        Console.WriteLine($"GetConversationMessages({conversationA}):");
        foreach (var m in thread)
            Console.WriteLine($"  [{m.Id}] user {m.UserId}: {m.Body}");

        Console.WriteLine();
        Console.WriteLine("Why this works fine right now:");
        Console.WriteLine(" - One table, a few rows: every query is a fast index/seq scan.");
        Console.WriteLine(" - GetMessage is a primary key lookup: O(log n) via the PK index.");
        Console.WriteLine(" - GetConversationMessages filters on conversation_id: fine without");
        Console.WriteLine("   an index at this size, and trivial to index when it isn't.");
        Console.WriteLine(" - There is exactly one place data can be: this connection string.");
        Console.WriteLine("   No routing, no coordination, no distributed anything.");
    }

    private static long InsertMessage(NpgsqlConnection conn, long conversationId, long userId, string body)
    {
        const string sql = """
            INSERT INTO messages (conversation_id, user_id, body)
            VALUES (@ConversationId, @UserId, @Body)
            RETURNING id;
            """;
        return conn.ExecuteScalar<long>(sql, new { ConversationId = conversationId, UserId = userId, Body = body });
    }

    private static Message? GetMessage(NpgsqlConnection conn, long id)
    {
        const string sql = """
            SELECT id AS Id, conversation_id AS ConversationId, user_id AS UserId,
                   body AS Body, created_at AS CreatedAt
            FROM messages WHERE id = @Id;
            """;
        return conn.QuerySingleOrDefault<Message>(sql, new { Id = id });
    }

    private static IEnumerable<Message> GetConversationMessages(NpgsqlConnection conn, long conversationId)
    {
        const string sql = """
            SELECT id AS Id, conversation_id AS ConversationId, user_id AS UserId,
                   body AS Body, created_at AS CreatedAt
            FROM messages
            WHERE conversation_id = @ConversationId
            ORDER BY id;
            """;
        return conn.Query<Message>(sql, new { ConversationId = conversationId });
    }
}
