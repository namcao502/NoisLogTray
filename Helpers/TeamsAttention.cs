namespace NoisLogTray;

// Pure rules for reminding the user of Teams messages that wait for an answer.
internal static class TeamsAttention
{
    private static readonly TimeSpan MarkLifetime = TimeSpan.FromDays(60);

    internal static TeamsAttentionItem? Pending(TeamsConversation conversation, string myId, DateTimeOffset? seenAt)
    {
        var messages = conversation.Thread.Messages;
        var lastMineIndex = -1;
        for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
        {
            if (string.Equals(messages[messageIndex].SenderId, myId, StringComparison.OrdinalIgnoreCase))
            {
                lastMineIndex = messageIndex;
            }
        }

        var pendingMessages = messages
            .Skip(lastMineIndex + 1)
            .Where(tm => seenAt == null || tm.SentAt > seenAt)
            .ToList();
        if (pendingMessages.Count == 0)
        {
            return null;
        }

        var newest = pendingMessages.MaxBy(tm => tm.SentAt)!;
        return new TeamsAttentionItem(
            conversation.TicketKey,
            TeamsChat.ConversationKey(conversation.Thread),
            conversation.ChatId,
            conversation.Title,
            newest.SenderName,
            newest.SentAt,
            pendingMessages.Exists(tm => TeamsChat.MentionsMe(tm, myId)));
    }

    internal static bool ShouldNotify(TeamsAttentionItem item, DateTimeOffset? notifiedAt, bool baselineDone)
    {
        return baselineDone && (notifiedAt == null || item.SentAt > notifiedAt);
    }

    internal static string NoticeText(TeamsAttentionItem item)
    {
        var place = item.ChatId == null ? "post" : "chat";
        if (item.MentionsMe)
        {
            return Lang.T($"{item.SenderName} mentioned you in the {item.TicketKey} {place}.",
                $"{item.SenderName} nhắc đến bạn trong {place} {item.TicketKey}.");
        }

        return Lang.T($"{item.SenderName} wrote in the {item.TicketKey} {place}.", $"{item.SenderName} nhắn trong {place} {item.TicketKey}.");
    }

    internal static Dictionary<string, DateTimeOffset> Pruned(Dictionary<string, DateTimeOffset> marks, DateTimeOffset now,
        IReadOnlySet<string> readKeys)
    {
        // A mark holds a message's time, which can be months old: dropping it while its conversation is still read would notify again.
        return marks
            .Where(kvp => readKeys.Contains(kvp.Key) || kvp.Value >= now - MarkLifetime)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }
}
