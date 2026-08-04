using Dapper;
using Npgsql;
using ShardingLab.Db;

namespace ShardingLab.Phases;

/// <summary>
/// Phase 3: the messages table is split across 4 independent databases
/// (shard1..shard4). The application decides which shard to talk to by
/// hashing conversation_id — there is no abstraction yet, routing is done
/// by hand right here, deliberately, so Phase 5 has something to fix.
/// </summary>
public class Phase03_Sharding
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

    private readonly ModuloShardRouter _router = new(shardCount: 4);

    public void Run()
    {
        Console.WriteLine("=== Phase 3: Introduce Sharding ===\n");

        foreach (var shardNumber in _router.AllShardNumbers)
        {
            using var conn = new NpgsqlConnection(ConnectionStrings.Shard(shardNumber));
            conn.Open();
            conn.Execute(CreateTableSql);
            conn.Execute("TRUNCATE messages RESTART IDENTITY;");
        }
        Console.WriteLine("shard1..shard4 ready, each with an empty 'messages' table.\n");

        long[] conversationIds = [1001, 1002, 1003, 1004, 1005, 1006, 2002, 3003];

        Console.WriteLine("Routing writes by hand (conversation_id % 4):");
        foreach (var conversationId in conversationIds)
        {
            int shardNumber = _router.ShardNumberFor(conversationId);
            using var conn = new NpgsqlConnection(ConnectionStrings.Shard(shardNumber));
            conn.Open();
            conn.Execute(
                "INSERT INTO messages (conversation_id, user_id, body) VALUES (@ConversationId, 1, @Body);",
                new { ConversationId = conversationId, Body = $"message for conversation {conversationId}" });
            Console.WriteLine($"  conversation {conversationId,5} -> shard{shardNumber}");
        }

        Console.WriteLine("\nReading conversation 1005 back — the app must know it lives on shard{0}:",
            _router.ShardNumberFor(1005));
        using (var conn = new NpgsqlConnection(ConnectionStrings.Shard(_router.ShardNumberFor(1005))))
        {
            conn.Open();
            var rows = conn.Query<Message>(
                """
                SELECT id AS Id, conversation_id AS ConversationId, user_id AS UserId,
                       body AS Body, created_at AS CreatedAt
                FROM messages WHERE conversation_id = @ConversationId;
                """, new { ConversationId = 1005L });
            foreach (var m in rows)
                Console.WriteLine($"  [{m.Id}] {m.Body}");
        }

        Console.WriteLine("\nPer-shard row counts:");
        foreach (var shardNumber in _router.AllShardNumbers)
        {
            using var conn = new NpgsqlConnection(ConnectionStrings.Shard(shardNumber));
            conn.Open();
            long count = conn.ExecuteScalar<long>("SELECT count(*) FROM messages;");
            Console.WriteLine($"  shard{shardNumber}: {count} row(s)");
        }

        Console.WriteLine();
        Console.WriteLine("What changed:");
        Console.WriteLine(" - Every write and read now needs TWO decisions: what shard, then what query.");
        Console.WriteLine(" - conversation_id % 4 is deterministic: the same conversation always lands");
        Console.WriteLine("   on the same shard, so a targeted read never has to guess or search.");
        Console.WriteLine(" - Notice every call site above computed the shard itself. That's a leak:");
        Console.WriteLine("   the application now has distributed-systems knowledge baked into it.");
    }
}
