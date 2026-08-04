using System.Diagnostics;
using Dapper;
using ShardingLab.Db;

namespace ShardingLab.Phases;

/// <summary>
/// Phase 6: not every query fits the conversation_id shard key. FindMessage,
/// SearchMessages and UnreadMessages don't know which shard to ask, so they
/// have to ask ALL of them — scatter-gather — and merge the results.
/// </summary>
public class Phase06_CrossShardQueries
{
    private readonly MessageStore _store = new(new ModuloShardRouter(shardCount: 4));

    public void Run()
    {
        Console.WriteLine("=== Phase 6: Cross-Shard Queries ===\n");

        _store.EnsureSchema();
        _store.ResetAllShards();
        AddReadReceiptColumn();

        long targetMessageId = SeedData();
        Console.WriteLine("Seeded 400 conversations x 25 messages = 10,000 messages across 4 shards.\n");

        var sw = Stopwatch.StartNew();
        var single = _store.GetConversationMessages(217);
        sw.Stop();
        Console.WriteLine($"{"GetConversationMessages(217)",-45} single shard   {single.Count,6:N0} rows  {sw.Elapsed.TotalMilliseconds,8:F2} ms");

        sw.Restart();
        var found = FindMessage(targetMessageId);
        sw.Stop();
        Console.WriteLine($"{$"FindMessage({targetMessageId})",-45} scatter/gather {1,6:N0} rows  {sw.Elapsed.TotalMilliseconds,8:F2} ms  (found on shard {_store.ShardNumberFor(found!.ConversationId)})");

        sw.Restart();
        var searchResults = SearchMessages("launch codes");
        sw.Stop();
        Console.WriteLine($"{"SearchMessages(\"launch codes\")",-45} scatter/gather {searchResults.Count,6:N0} rows  {sw.Elapsed.TotalMilliseconds,8:F2} ms");

        sw.Restart();
        var unread = UnreadMessages(userId: 7);
        sw.Stop();
        Console.WriteLine($"{"UnreadMessages(userId: 7)",-45} scatter/gather {unread.Count,6:N0} rows  {sw.Elapsed.TotalMilliseconds,8:F2} ms");

        Console.WriteLine();
        Console.WriteLine("What the cost comparison shows:");
        Console.WriteLine(" - GetConversationMessages hit exactly 1 connection because conversation_id");
        Console.WriteLine("   IS the shard key — Phase 4's payoff, realized.");
        Console.WriteLine(" - The other three don't have a conversation_id to route on, so every one");
        Console.WriteLine("   of them opens a connection to all 4 shards and merges results in memory.");
        Console.WriteLine("   That's 4x the connections and 4x the query planning, no matter how cheap");
        Console.WriteLine("   each individual shard's share of the work is.");
        Console.WriteLine(" - This is exactly why Instagram keeps a separate global secondary index");
        Console.WriteLine("   for lookups that don't align with their shard key, and why Elasticsearch");
        Console.WriteLine("   sits next to sharded primary stores at companies like Slack and Discord —");
        Console.WriteLine("   scatter-gather is fine occasionally, but you don't want it on your hot path.");
    }

    private long SeedData()
    {
        // Each shard's BIGSERIAL starts at 1, so plain auto-increment ids collide
        // across shards (shard1's id=42 and shard3's id=42 are different messages).
        // We embed the shard number in the id instead — the same trick Instagram
        // and Twitter's Snowflake use to keep ids globally unique without a
        // central counter. FindMessage's scatter-gather below depends on this.
        var random = new Random(11);
        long targetMessageId = -1;
        var localSequence = new Dictionary<int, long>();

        for (long conversationId = 1; conversationId <= 400; conversationId++)
        {
            int shardNumber = _store.ShardNumberFor(conversationId);
            using var conn = _store.OpenShardByNumber(shardNumber);

            for (int i = 0; i < 25; i++)
            {
                long userId = random.Next(1, 50);
                bool isTarget = conversationId == 217 && i == 12;
                string body = isTarget ? "the launch codes are hidden in the closet" : Phrase(random);

                long seq = localSequence.GetValueOrDefault(shardNumber) + 1;
                localSequence[shardNumber] = seq;
                long id = shardNumber * 10_000_000L + seq;

                conn.Execute(
                    "INSERT INTO messages (id, conversation_id, user_id, body, created_at) VALUES (@Id, @ConversationId, @UserId, @Body, now());",
                    new { Id = id, ConversationId = conversationId, UserId = userId, Body = body });

                if (isTarget) targetMessageId = id;
            }
        }
        return targetMessageId;
    }

    private static string Phrase(Random random)
    {
        string[] bank = ["hey", "on my way", "sounds good", "thanks!", "call me", "brb", "lol", "same", "no worries", "see you then"];
        return bank[random.Next(bank.Length)];
    }

    private Message? FindMessage(long messageId)
    {
        const string sql = """
            SELECT id AS Id, conversation_id AS ConversationId, user_id AS UserId,
                   body AS Body, created_at AS CreatedAt
            FROM messages WHERE id = @Id;
            """;
        foreach (var shardNumber in _store.AllShardNumbers)
        {
            using var conn = _store.OpenShardByNumber(shardNumber);
            var match = conn.QuerySingleOrDefault<Message>(sql, new { Id = messageId });
            if (match != null) return match;
        }
        return null;
    }

    private List<Message> SearchMessages(string keyword)
    {
        const string sql = """
            SELECT id AS Id, conversation_id AS ConversationId, user_id AS UserId,
                   body AS Body, created_at AS CreatedAt
            FROM messages WHERE body ILIKE @Pattern;
            """;
        var results = new List<Message>();
        foreach (var shardNumber in _store.AllShardNumbers)
        {
            using var conn = _store.OpenShardByNumber(shardNumber);
            results.AddRange(conn.Query<Message>(sql, new { Pattern = $"%{keyword}%" }));
        }
        return results;
    }

    private List<Message> UnreadMessages(long userId)
    {
        // "Unread" here = not sent by this user and never marked read. Every message
        // starts unread (read_at IS NULL) since there's no read-marking feature in this
        // lab — the point is the fan-out, not a full inbox implementation.
        const string sql = """
            SELECT id AS Id, conversation_id AS ConversationId, user_id AS UserId,
                   body AS Body, created_at AS CreatedAt
            FROM messages WHERE user_id != @UserId AND read_at IS NULL;
            """;
        var results = new List<Message>();
        foreach (var shardNumber in _store.AllShardNumbers)
        {
            using var conn = _store.OpenShardByNumber(shardNumber);
            results.AddRange(conn.Query<Message>(sql, new { UserId = userId }));
        }
        return results;
    }

    private void AddReadReceiptColumn()
    {
        foreach (var shardNumber in _store.AllShardNumbers)
        {
            using var conn = _store.OpenShardByNumber(shardNumber);
            conn.Execute("ALTER TABLE messages ADD COLUMN IF NOT EXISTS read_at TIMESTAMPTZ;");
        }
    }
}
