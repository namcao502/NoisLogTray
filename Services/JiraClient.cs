using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace NoisLogTray;

// Jira Cloud REST client (port of lib/jira.ts). Basic auth (email + API token).
internal sealed class JiraClient : IJiraClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // Default "My tickets" query when the user has not set a custom JIRA_MY_TICKETS_JQL:
    // assigned to AND contributing on, excluding done-ish statuses, ordered by due then
    // backlog rank (cf[10012]).
    internal const string DefaultMyTicketsJql =
        "project = MDP AND assignee = currentUser() AND " +
        "\"contributors[user picker (multiple users)]\" = currentUser() AND " +
        "status NOT IN (\"Deployed to Production\", \"QA Confirmed\", Done) " +
        "ORDER BY due ASC, cf[10012] ASC";

    private readonly string _baseUrl;
    private readonly string _authHeader;

    internal JiraClient(string baseUrl, string email, string token)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _authHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{token}"));
    }

    // Verify the email + token by calling the authenticated /myself endpoint.
    // 200 = valid; 401/403 = rejected; anything else (bad host, timeout, 404) =
    // unreachable, so the caller can let the user save without being locked out.
    public async Task<CredentialCheck> ValidateAsync(CancellationToken ct = default)
    {
        try
        {
            return await Retry.OnTransientAsync(async c =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/rest/api/3/myself");
                AddHeaders(req);
                using var res = await Http.SendAsync(req, c);
                if (res.StatusCode == HttpStatusCode.OK) return CredentialCheck.Valid;
                if (res.StatusCode == HttpStatusCode.Unauthorized || res.StatusCode == HttpStatusCode.Forbidden)
                    return CredentialCheck.Rejected;
                return CredentialCheck.Unreachable;
            }, ct: ct);
        }
        catch
        {
            return CredentialCheck.Unreachable;
        }
    }

    // GET is idempotent, so a transient transport failure is safe to retry.
    public Task<JiraVerifyResult> VerifyTicketAsync(string ticketId, CancellationToken ct = default)
        => Retry.OnTransientAsync(async c =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/rest/api/3/issue/{ticketId}?fields=summary");
            AddHeaders(req);
            using var res = await Http.SendAsync(req, c);

            if (res.StatusCode == HttpStatusCode.OK)
            {
                var json = await res.Content.ReadAsStringAsync(c);
                using var doc = JsonDocument.Parse(json);
                var summary = doc.RootElement.GetProperty("fields").GetProperty("summary").GetString();
                return new JiraVerifyResult(true, summary);
            }
            if (res.StatusCode == HttpStatusCode.NotFound)
                return new JiraVerifyResult(false, null);

            throw new InvalidOperationException($"Jira API error: {(int)res.StatusCode} {res.ReasonPhrase}");
        }, ct: ct);

    public async Task<IReadOnlyList<JiraSuggestion>> GetMyTicketsAsync(int limit = 5, string? jql = null, CancellationToken ct = default)
    {
        var effectiveJql = string.IsNullOrWhiteSpace(jql) ? DefaultMyTicketsJql : jql;
        var body = JsonSerializer.Serialize(new { jql = effectiveJql, maxResults = limit, fields = new[] { "summary", "duedate" } });

        // A read-only search; retry transient transport failures.
        var json = await Retry.OnTransientAsync(async c =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/rest/api/3/search/jql")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            AddHeaders(req);
            using var res = await Http.SendAsync(req, c);

            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"Jira API error: {(int)res.StatusCode} {res.ReasonPhrase}");

            return await res.Content.ReadAsStringAsync(c);
        }, ct: ct);

        using var doc = JsonDocument.Parse(json);
        var list = new List<JiraSuggestion>();
        if (doc.RootElement.TryGetProperty("issues", out var issues) && issues.ValueKind == JsonValueKind.Array)
        {
            foreach (var issue in issues.EnumerateArray())
            {
                var key = issue.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
                var hasFields = issue.TryGetProperty("fields", out var f);
                var summary = hasFields && f.TryGetProperty("summary", out var s)
                    ? s.GetString() ?? ""
                    : "";
                var due = hasFields && f.TryGetProperty("duedate", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString()
                    : null;
                list.Add(new JiraSuggestion(key, summary, due));
            }
        }
        return list;
    }

    // Check a custom "My tickets" JQL by running it (maxResults = 1) before it is saved.
    // 200 = Valid; a non-success (Jira returns 400 for bad JQL) = Invalid with Jira's
    // own error text; a network/timeout error = Unreachable so the caller can offer to
    // save anyway rather than block an offline user.
    public async Task<JqlCheckResult> ValidateJqlAsync(string jql, CancellationToken ct = default)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { jql, maxResults = 1, fields = new[] { "summary" } });
            return await Retry.OnTransientAsync(async c =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/rest/api/3/search/jql")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                AddHeaders(req);
                using var res = await Http.SendAsync(req, c);
                if (res.IsSuccessStatusCode) return new JqlCheckResult(JqlCheck.Valid, null);

                var json = await res.Content.ReadAsStringAsync(c);
                var reason = TryExtractJiraError(json) ?? $"Jira rejected the query ({(int)res.StatusCode}).";
                return new JqlCheckResult(JqlCheck.Invalid, reason);
            }, ct: ct);
        }
        catch
        {
            return new JqlCheckResult(JqlCheck.Unreachable, null);
        }
    }

    // Pull the first message out of a Jira error body ({ "errorMessages": [...] }); null
    // if the body is not the expected shape.
    private static string? TryExtractJiraError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("errorMessages", out var msgs)
                && msgs.ValueKind == JsonValueKind.Array && msgs.GetArrayLength() != 0)
                return msgs[0].GetString();
        }
        catch
        {
            // Not JSON, or not the shape we expect: fall through to the caller's default.
        }
        return null;
    }

    private void AddHeaders(HttpRequestMessage req)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", _authHeader);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
}
