using ShardingLab.Db;

namespace ShardingLab.Phases;

/// <summary>
/// Phase 5: the same behavior as Phase 3, but every call site below only
/// ever supplies a conversation_id. Compare this file to Phase03_Sharding.cs —
/// no shard numbers, no connection strings, no routing math in application code.
/// </summary>
public class Phase05_RouterAbstraction
{
    private readonly MessageStore _store = new(new ModuloShardRouter(shardCount: 4));

    public void Run()
    {
        Console.WriteLine("=== Phase 5: Shard Router as an Abstraction ===\n");

        _store.EnsureSchema();
        _store.ResetAllShards();

        long[] conversationIds = [1001, 1002, 1003, 1004, 1005, 1006, 2002, 3003];

        Console.WriteLine("Application code — notice it never mentions a shard:");
        foreach (var conversationId in conversationIds)
        {
            _store.InsertMessage(conversationId, userId: 1, body: $"message for conversation {conversationId}");
            Console.WriteLine($"  _store.InsertMessage({conversationId}, ...)");
        }

        Console.WriteLine("\n_store.GetConversationMessages(1005):");
        foreach (var m in _store.GetConversationMessages(1005))
            Console.WriteLine($"  [{m.Id}] {m.Body}");

        Console.WriteLine("\n(Behind the scenes, for reference, MessageStore resolved these shards:)");
        foreach (var conversationId in conversationIds)
            Console.WriteLine($"  conversation {conversationId,5} -> shard{_store.ShardNumberFor(conversationId)}");

        Console.WriteLine();
        Console.WriteLine("What changed since Phase 3:");
        Console.WriteLine(" - The application layer's vocabulary is now just 'conversation_id'.");
        Console.WriteLine(" - MessageStore is the only class that knows shard1..shard4 exist at all.");
        Console.WriteLine(" - This is exactly what Vitess does for MySQL and what Citus does for");
        Console.WriteLine("   Postgres: the app speaks normal SQL/queries against a logical table,");
        Console.WriteLine("   and a routing layer underneath decides which physical shard serves it.");
        Console.WriteLine(" - The payoff shows up in Phase 8: when shard COUNT changes, only");
        Console.WriteLine("   MessageStore's router needs to change — not every call site.");
    }
}
