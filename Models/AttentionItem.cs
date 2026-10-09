namespace NoisLogTray;

internal sealed record AttentionItem(AttentionKind Kind, JiraTicket Ticket, string? ActionLabel, int CommentCount, string? PrUrl);
