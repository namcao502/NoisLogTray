namespace NoisLogTray;

internal sealed record JiraVerifyResult(bool Valid, string? Summary);

// DueDate is the raw Jira `duedate` (ISO "yyyy-MM-dd"), or null when unset.
internal sealed record JiraSuggestion(string Key, string Summary, string? DueDate = null);

// Outcome of validating a custom "My tickets" JQL against Jira before saving it:
// Valid (query runs), Invalid (Jira rejected it - Error holds the reason), or
// Unreachable (couldn't reach Jira to check). Mirrors the CredentialCheck tri-state.
internal enum JqlCheck { Valid, Invalid, Unreachable }

internal sealed record JqlCheckResult(JqlCheck Status, string? Error);
