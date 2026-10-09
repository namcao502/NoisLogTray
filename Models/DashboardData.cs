namespace NoisLogTray;

internal sealed record DashboardData(
    int QueueCount,
    int TodayQueuedCount,
    IReadOnlySet<string> TodayQueuedKeys,
    TimeSpan? UntilLogTime,
    IReadOnlyList<(JiraTicket Ticket, string PrSummary)> InProgress,
    IReadOnlyList<AttentionItem> Attention,
    IReadOnlyList<JiraTicket> NewTickets);
