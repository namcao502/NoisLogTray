namespace NoisLogTray;

// Origin is kept so Restore puts the ticket back where it was dismissed from, not always into New.
internal sealed record DismissedTicket(JiraTicket Ticket, TicketSection Origin);
