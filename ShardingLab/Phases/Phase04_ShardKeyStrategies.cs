using ShardingLab.Db;

namespace ShardingLab.Phases;

/// <summary>
/// Phase 4: three candidate shard keys, compared on synthetic data.
/// This is pure computation — no database needed to see which key wins.
/// </summary>
public class Phase04_ShardKeyStrategies
{
    private const int ShardCount = 4;
    private const int ConversationCount = 5_000;
    private const int UserPoolSize = 2_000;
    private const int MessagesPerConversation = 20;

    private record SyntheticMessage(long MessageId, long ConversationId, long UserId);

    public void Run()
    {
        Console.WriteLine("=== Phase 4: Shard Key Strategies ===\n");

        var messages = GenerateSyntheticMessages();
        Console.WriteLine($"Simulated {ConversationCount:N0} conversations, {messages.Count:N0} messages, " +
                           $"{UserPoolSize:N0} distinct users, routed onto {ShardCount} shards.\n");

        Evaluate("Hash(MessageId)", messages, m => m.MessageId);
        Evaluate("Hash(UserId)", messages, m => m.UserId);
        Evaluate("Hash(ConversationId)", messages, m => m.ConversationId);

        Console.WriteLine();
        Console.WriteLine("Why ConversationId wins for this data model:");
        Console.WriteLine(" - The dominant query in a chat app is GetConversationMessages — run on");
        Console.WriteLine("   every open thread, constantly.");
        Console.WriteLine(" - Hash(MessageId) scatters every message independently, so a single");
        Console.WriteLine("   conversation's messages land all over — nearly every conversation is split.");
        Console.WriteLine(" - Hash(UserId) keeps one user's own messages together, but a conversation");
        Console.WriteLine("   has two participants who usually hash to different shards — most");
        Console.WriteLine("   conversations are still split.");
        Console.WriteLine(" - Hash(ConversationId) is the only strategy where every message in a");
        Console.WriteLine("   conversation shares one key by definition, so 0% end up split.");
        Console.WriteLine(" - This mirrors Discord's real design: they shard messages by channel_id");
        Console.WriteLine("   for exactly this reason — reading a channel's history must never fan");
        Console.WriteLine("   out across the cluster. Instagram makes the same call sharding by");
        Console.WriteLine("   user_id, because their dominant query is 'this user's own data.'");
    }

    private static void Evaluate(string strategyName, List<SyntheticMessage> messages, Func<SyntheticMessage, long> keySelector)
    {
        var shardSizes = new int[ShardCount];
        var shardsPerConversation = new Dictionary<long, HashSet<int>>();

        foreach (var m in messages)
        {
            int shard = (int)(StableHash.Of(keySelector(m)) % ShardCount);
            shardSizes[shard]++;

            if (!shardsPerConversation.TryGetValue(m.ConversationId, out var shardsSeen))
                shardsPerConversation[m.ConversationId] = shardsSeen = [];
            shardsSeen.Add(shard);
        }

        int splitConversations = shardsPerConversation.Values.Count(s => s.Count > 1);
        double splitPct = 100.0 * splitConversations / shardsPerConversation.Count;

        Console.WriteLine($"{strategyName,-22} shard sizes {string.Join("/", shardSizes),-16} " +
                           $"conversations split across shards: {splitConversations,5:N0} / {shardsPerConversation.Count:N0} ({splitPct,5:F1}%)");
    }

    private static List<SyntheticMessage> GenerateSyntheticMessages()
    {
        var random = new Random(7);
        var messages = new List<SyntheticMessage>();
        long nextMessageId = 1;

        for (long conversationId = 1; conversationId <= ConversationCount; conversationId++)
        {
            long userA = random.Next(1, UserPoolSize + 1);
            long userB = random.Next(1, UserPoolSize + 1);
            for (int i = 0; i < MessagesPerConversation; i++)
            {
                long userId = i % 2 == 0 ? userA : userB;
                messages.Add(new SyntheticMessage(nextMessageId++, conversationId, userId));
            }
        }
        return messages;
    }
}
