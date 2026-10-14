namespace NoisLogTray;

// One to-do suggestion; which fields are set depends on Kind (see DashboardTodos). Section tells an overdue New ticket
// (Start) from one in progress (Open); TicketKeys are the tickets to queue, or the worktrees to clean up.
internal sealed record TodoItem(
    TodoKind Kind,
    JiraTicket? Ticket = null,
    TicketSection Section = TicketSection.InProgress,
    string? ActionLabel = null,
    int CommentCount = 0,
    string? PrUrl = null,
    DayCoverage? Day = null,
    ClaudeWait? Wait = null,
    IReadOnlyList<string>? TicketKeys = null);
