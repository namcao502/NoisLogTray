namespace NoisLogTray;

// Jira Cloud read operations used by LoggingService: verify a ticket, and list the
// user's open tickets for the "My tickets" suggestions.
internal interface IJiraClient
{
    Task<JiraVerifyResult> VerifyTicketAsync(string ticketId, CancellationToken ct = default);
    Task<IReadOnlyList<JiraSuggestion>> GetMyTicketsAsync(int limit = 5, string? jql = null, CancellationToken ct = default);
    Task<JqlCheckResult> ValidateJqlAsync(string jql, CancellationToken ct = default);
}
