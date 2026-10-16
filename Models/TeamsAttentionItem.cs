namespace NoisLogTray;

// The newest message of a conversation that waits for the user's answer.
internal sealed record TeamsAttentionItem(string TicketKey, string ConversationKey, string? ChatId, string Title, string SenderName,
    DateTimeOffset SentAt, bool MentionsMe);
