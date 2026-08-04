namespace ShardingLab.Db;

public record Message(long Id, long ConversationId, long UserId, string Body, DateTime CreatedAt);
