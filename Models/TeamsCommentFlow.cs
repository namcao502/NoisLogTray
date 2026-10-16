namespace NoisLogTray;

internal sealed record TeamsCommentFlow(string TicketKey, string ConversationKey, string? ChatId, string AskMessageId, string AskSenderName,
    TeamsCommentStage Stage, bool PromptSubmitted = false, int OpenThreadCount = 0);
