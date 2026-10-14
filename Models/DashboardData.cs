namespace NoisLogTray;

internal sealed record DashboardData(
    int QueueCount,
    int TodayQueuedCount,
    IReadOnlySet<string> TodayQueuedKeys,
    TimeSpan? UntilLogTime,
    IReadOnlyList<(JiraTicket Ticket, IReadOnlyList<TicketPr> Prs)> InProgress,
    IReadOnlyList<JiraTicket> NewTickets,
    IReadOnlyList<TodoItem> TicketTodos,
    IReadOnlyList<string> ShippedKeys);
