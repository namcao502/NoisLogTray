namespace NoisLogTray;

// Jira Cloud operations: ticket reads for logging and the Tasks board, plus status transitions.
internal interface IJiraClient
{
    Task<JiraVerifyResult> VerifyTicketAsync(string ticketId, CancellationToken ct = default);
    Task<IReadOnlyList<JiraSuggestion>> GetMyTicketsAsync(string? jql = null, CancellationToken ct = default);
    Task<JqlCheckResult> ValidateJqlAsync(string jql, CancellationToken ct = default);
    Task<Dictionary<string, JiraTicket>> GetAssignedTicketsAsync(CancellationToken ct = default);
    Task<JiraTicket?> GetTicketAsync(string ticketKey, CancellationToken ct = default);
    Task<bool> TransitionAsync(string ticketKey, string targetStatus, CancellationToken ct = default);
}
