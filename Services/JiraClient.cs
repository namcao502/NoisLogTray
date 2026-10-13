using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace NoisLogTray;

// Jira Cloud REST client (port of lib/jira.ts). Basic auth (email + API token).
internal sealed class JiraClient : IJiraClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private const int AssignedTicketsMaxResults = 50;
    private const string EtaFieldId = "customfield_10735"; // MDP's "ETA Date" (10880 belongs to another project)
    private const string DevPriorityFieldId = "customfield_11111";
    private static readonly string[] TicketFields = { "summary", "duedate", "status", "timespent", EtaFieldId, DevPriorityFieldId };

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    internal const string AssignedTicketsJql = "assignee = currentUser() AND statusCategory != Done ORDER BY updated DESC";

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

    // Single page of the user's active assigned tickets, keyed by ticket key. Throws on failure.
    public async Task<Dictionary<string, JiraTicket>> GetAssignedTicketsAsync(CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            jql = AssignedTicketsJql,
            maxResults = AssignedTicketsMaxResults,
            fields = TicketFields,
        });

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

        return ParseSearchResponse(json);
    }

    // Null when the key does not exist or is not visible; any other failure throws.
    public Task<JiraTicket?> GetTicketAsync(string ticketKey, CancellationToken ct = default)
        => Retry.OnTransientAsync(async c =>
        {
            var uri = $"{_baseUrl}/rest/api/3/issue/{Uri.EscapeDataString(ticketKey)}?fields={string.Join(",", TicketFields)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            AddHeaders(req);
            using var res = await Http.SendAsync(req, c);

            if (res.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"Jira API error: {(int)res.StatusCode} {res.ReasonPhrase}");

            return ParseIssueResponse(await res.Content.ReadAsStringAsync(c));
        }, ct: ct);

    // False when no transition leads to targetStatus; an HTTP failure throws.
    // Only the GET is retried: a POST retried after a lost response could apply twice.
    public async Task<bool> TransitionAsync(string ticketKey, string targetStatus, CancellationToken ct = default)
    {
        var uri = $"{_baseUrl}/rest/api/3/issue/{Uri.EscapeDataString(ticketKey)}/transitions";
        var transitionsJson = await Retry.OnTransientAsync(async c =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            AddHeaders(req);
            using var res = await Http.SendAsync(req, c);

            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"Jira API error: {(int)res.StatusCode} {res.ReasonPhrase}");

            return await res.Content.ReadAsStringAsync(c);
        }, ct: ct);

        var transitionId = FindTransitionId(transitionsJson, targetStatus);
        if (transitionId == null) return false;

        var body = JsonSerializer.Serialize(new { transition = new { id = transitionId } });
        using var applyReq = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        AddHeaders(applyReq);
        using var applyRes = await Http.SendAsync(applyReq, ct);

        if (!applyRes.IsSuccessStatusCode)
            throw new InvalidOperationException($"Jira API error: {(int)applyRes.StatusCode} {applyRes.ReasonPhrase}");

        return true;
    }

    internal static string? FindTransitionId(string transitionsJson, string targetStatus)
    {
        using var doc = JsonDocument.Parse(transitionsJson);
        if (!doc.RootElement.TryGetProperty("transitions", out var transitions) || transitions.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var transition in transitions.EnumerateArray())
        {
            if (!transition.TryGetProperty("to", out var to) || to.ValueKind != JsonValueKind.Object) continue;
            if (string.Equals(GetOptionalString(to, "name"), targetStatus, StringComparison.OrdinalIgnoreCase))
                return GetOptionalString(transition, "id");
        }
        return null;
    }

    internal static Dictionary<string, JiraTicket> ParseSearchResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);

        var result = new Dictionary<string, JiraTicket>();
        if (!doc.RootElement.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var issue in issues.EnumerateArray())
        {
            var ticket = ParseIssue(issue);
            if (ticket.Key.Length == 0) continue;
            result[ticket.Key] = ticket;
        }
        return result;
    }

    internal static JiraTicket ParseIssueResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseIssue(doc.RootElement);
    }

    private static JiraTicket ParseIssue(JsonElement issue)
    {
        // One malformed issue must not abort the whole poll: bad shapes degrade to empty/null.
        var key = GetOptionalString(issue, "key") ?? string.Empty;
        var issueFields = issue.TryGetProperty("fields", out var fieldsValue) && fieldsValue.ValueKind == JsonValueKind.Object
            ? fieldsValue
            : EmptyObject;
        var summary = GetOptionalString(issueFields, "summary") ?? string.Empty;
        var timeSpent = GetOptionalNumber(issueFields, "timespent");

        string? status = null;
        string? statusCategory = null;
        if (issueFields.TryGetProperty("status", out var statusValue) && statusValue.ValueKind == JsonValueKind.Object)
        {
            status = GetOptionalString(statusValue, "name");
            if (statusValue.TryGetProperty("statusCategory", out var categoryValue) && categoryValue.ValueKind == JsonValueKind.Object)
                statusCategory = GetOptionalString(categoryValue, "key");
        }

        return new JiraTicket(
            key,
            summary,
            GetOptionalString(issueFields, "duedate"),
            GetOptionalString(issueFields, EtaFieldId),
            GetOptionalNumber(issueFields, DevPriorityFieldId),
            status,
            timeSpent.HasValue ? (long)timeSpent.Value : null,
            statusCategory);
    }

    private static string? GetOptionalString(JsonElement fields, string propertyName)
        => fields.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? GetOptionalNumber(JsonElement fields, string propertyName)
        => fields.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private void AddHeaders(HttpRequestMessage req)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", _authHeader);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
}
