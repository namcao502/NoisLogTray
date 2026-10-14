namespace NoisLogTray;

internal sealed record TicketPollResult(
    IReadOnlyList<JiraTicket> NewTickets,
    IReadOnlyList<JiraTicket> FinishedTickets,
    IReadOnlyList<JiraTicket> ReopenedTickets);
