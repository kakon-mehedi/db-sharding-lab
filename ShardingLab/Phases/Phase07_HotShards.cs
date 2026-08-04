using System.Diagnostics;
using Dapper;
using Npgsql;
using NpgsqlTypes;
using ShardingLab.Db;

namespace ShardingLab.Phases;

/// <summary>
/// Phase 7: sharding by conversation_id keeps a conversation's reads cheap
/// (Phase 4/5), but it also means one viral conversation piles ALL of its
/// messages onto ONE shard. Nothing balances load across shards automatically.
/// </summary>
public class Phase07_HotShards
{
    private const long HotConversationId = 4;
    private const int HotMessageCount = 1_000_000;
    private const int NormalConversations = 2_000;
    private const int MessagesPerNormalConversation = 10;
    private const long NormalConversationId = 2;

    private readonly MessageStore _store = new(new ModuloShardRouter(shardCount: 4));

    public void Run()
    {
        Console.WriteLine("=== Phase 7: Hot Shards ===\n");

        _store.EnsureSchema();
        _store.ResetAllShards();

        var random = new Random(3);
        for (long c = 1; c <= NormalConversations; c++)
        {
            if (c == HotConversationId) continue;
            for (int i = 0; i < MessagesPerNormalConversation; i++)
                _store.InsertMessage(c, random.Next(1, 500), Phrase(random));
        }
        Console.WriteLine($"Seeded baseline: {NormalConversations - 1:N0} normal conversations x {MessagesPerNormalConversation} messages.\n");

        int hotShard = _store.ShardNumberFor(HotConversationId);
        int normalShard = _store.ShardNumberFor(NormalConversationId);
        Console.WriteLine($"Conversation {HotConversationId} routes to shard{hotShard}. Simulating a viral group chat:");
        BulkInsertHotConversation(hotShard, HotConversationId, HotMessageCount);

        Console.WriteLine("Per-shard row counts and on-disk size after the spike:");
        foreach (var shardNumber in _store.AllShardNumbers)
        {
            using var conn = _store.OpenShardByNumber(shardNumber);
            long rows = conn.ExecuteScalar<long>("SELECT count(*) FROM messages;");
            string size = conn.ExecuteScalar<string>("SELECT pg_size_pretty(pg_total_relation_size('messages'));")!;
            string marker = shardNumber == hotShard ? "   <-- HOT" : "";
            Console.WriteLine($"  shard{shardNumber}: {rows,10:N0} rows, {size,10}{marker}");
        }

        var sw = Stopwatch.StartNew();
        using (var conn = _store.OpenShardByNumber(hotShard))
            conn.ExecuteScalar<long>("SELECT count(*) FROM messages;");
        sw.Stop();
        double hotCountMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        using (var conn = _store.OpenShardByNumber(normalShard))
            conn.ExecuteScalar<long>("SELECT count(*) FROM messages;");
        sw.Stop();
        double normalCountMs = sw.Elapsed.TotalMilliseconds;

        Console.WriteLine($"\nCOUNT(*) on hot shard{hotShard}:    {hotCountMs,7:F1} ms");
        Console.WriteLine($"COUNT(*) on normal shard{normalShard}: {normalCountMs,7:F1} ms");

        Console.WriteLine();
        Console.WriteLine("What this shows:");
        Console.WriteLine(" - Every OTHER conversation on shard{0} — none of them viral — now pays", hotShard);
        Console.WriteLine("   the tax of sharing a machine with a conversation 100,000x their size.");
        Console.WriteLine("   Any maintenance operation on that shard (VACUUM, backup, index rebuild)");
        Console.WriteLine("   takes as long as the hot data demands, not the average conversation.");
        Console.WriteLine(" - conversation_id sharding assumed roughly even conversation sizes. It");
        Console.WriteLine("   breaks the moment that assumption is wrong — and it's often wrong:");
        Console.WriteLine("   celebrity accounts, viral group chats, #general in a huge Discord server.");
        Console.WriteLine(" - Discord's real-world fix for exactly this: #general-style hot channels");
        Console.WriteLine("   get bucketed by TIME (message buckets per day/week) in addition to");
        Console.WriteLine("   channel_id, so a single busy channel still spreads across the cluster.");
        Console.WriteLine("   Twitter/X deals with celebrity hot keys by fanning writes out to follower");
        Console.WriteLine("   timelines asynchronously instead of reading a hot row on every view.");
    }

    private void BulkInsertHotConversation(int shardNumber, long conversationId, int count)
    {
        using var conn = _store.OpenShardByNumber(shardNumber);
        var random = new Random(99);
        var sw = Stopwatch.StartNew();

        using (var writer = conn.BeginBinaryImport(
            "COPY messages (conversation_id, user_id, body, created_at) FROM STDIN (FORMAT BINARY)"))
        {
            var now = DateTime.UtcNow;
            for (int i = 0; i < count; i++)
            {
                writer.StartRow();
                writer.Write(conversationId, NpgsqlDbType.Bigint);
                writer.Write((long)random.Next(1, 5000), NpgsqlDbType.Bigint);
                writer.Write("this group chat will not stop", NpgsqlDbType.Text);
                writer.Write(now, NpgsqlDbType.TimestampTz);
            }
            writer.Complete();
        }
        Console.WriteLine($"  Bulk-loaded {count:N0} messages into conversation {conversationId} in {sw.Elapsed.TotalSeconds:F1}s.\n");
    }

    private static string Phrase(Random random)
    {
        string[] bank = ["hey", "on my way", "sounds good", "thanks!", "call me", "brb", "lol", "same"];
        return bank[random.Next(bank.Length)];
    }
}
