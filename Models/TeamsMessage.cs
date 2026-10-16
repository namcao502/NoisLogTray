namespace NoisLogTray;

internal sealed record TeamsMessage(string Id, string SenderId, string SenderName, DateTimeOffset SentAt, string Content, bool IsPlainText,
    string? Subject, DateTimeOffset? EditedAt = null, IReadOnlyDictionary<string, string>? MentionMris = null, bool HasFiles = false,
    IReadOnlyList<TeamsReaction>? Reactions = null, string? RootMessageId = null);
