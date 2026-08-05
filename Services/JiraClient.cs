using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace NoisLogTray;

// Jira Cloud REST client (port of lib/jira.ts). Basic auth (email + API token).
internal sealed class JiraClient : IJiraClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // Nam's open MDP work: assigned to AND contributing on, excluding done-ish
    // statuses, ordered by due then backlog rank (cf[10012]).
    private const string MyTicketsJql =
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

    public async Task<JiraVerifyResult> VerifyTicketAsync(string ticketId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/rest/api/3/issue/{ticketId}?fields=summary");
        AddHeaders(req);
        using var res = await Http.SendAsync(req, ct);

        if (res.StatusCode == HttpStatusCode.OK)
        {
            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var summary = doc.RootElement.GetProperty("fields").GetProperty("summary").GetString();
            return new JiraVerifyResult(true, summary);
        }
        if (res.StatusCode == HttpStatusCode.NotFound)
            return new JiraVerifyResult(false, null);

        throw new InvalidOperationException($"Jira API error: {(int)res.StatusCode} {res.ReasonPhrase}");
    }

    public async Task<IReadOnlyList<JiraSuggestion>> GetMyTicketsAsync(int limit = 5, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { jql = MyTicketsJql, maxResults = limit, fields = new[] { "summary" } });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/rest/api/3/search/jql")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        AddHeaders(req);
        using var res = await Http.SendAsync(req, ct);

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Jira API error: {(int)res.StatusCode} {res.ReasonPhrase}");

        var json = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var list = new List<JiraSuggestion>();
        if (doc.RootElement.TryGetProperty("issues", out var issues) && issues.ValueKind == JsonValueKind.Array)
        {
            foreach (var issue in issues.EnumerateArray())
            {
                var key = issue.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
                var summary = issue.TryGetProperty("fields", out var f) && f.TryGetProperty("summary", out var s)
                    ? s.GetString() ?? ""
                    : "";
                list.Add(new JiraSuggestion(key, summary));
            }
        }
        return list;
    }

    private void AddHeaders(HttpRequestMessage req)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", _authHeader);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
}
