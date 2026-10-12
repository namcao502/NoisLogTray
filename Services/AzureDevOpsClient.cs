using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace NoisLogTray;

internal sealed class AzureDevOpsClient : IAzureDevOpsClient
{
    private const string Org = "TeeShirtCentral";
    private const string Project = "MDP";
    internal const string BackendRepository = "Backend-DotNet";

    // Project-wide, so the same branch name finds the BE PR and any FE (Angular repo) PR.
    private static readonly string ApiBase = $"https://dev.azure.com/{Org}/{Project}/_apis/git";

    // Static so recreating the client on Settings save does not leak sockets; the short connect timeout
    // stops a down network from stalling every call for the OS default (~21-42s).
    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private readonly string _pat;
    private string? _creatorId;

    internal AzureDevOpsClient(string pat)
    {
        _pat = pat;
    }

    // Every branch is tried; a failed one is flagged via IsComplete so the healthy branch is still reported
    // but the caller never baselines a partial read. A network failure propagates at once to stop the pass.
    public async Task<PrLookupResult> GetTicketPrsAsync(string ticketKey)
    {
        if (string.IsNullOrEmpty(_pat))
        {
            return new PrLookupResult(new List<PrStatus>(), true);
        }

        // Propagates on failure: running unfiltered would track a teammate's same-named branch PR as ours,
        // and an empty result would be indistinguishable from "no PRs" to the checker.
        var creatorId = await GetCreatorIdAsync();

        var prStatuses = new List<PrStatus>();
        var failedBranches = new List<string>();
        var branches = new (string Label, string BranchName)[]
        {
            ("main", ticketKey),
            ("dev", $"{ticketKey}-dev"),
        };

        foreach (var (label, branchName) in branches)
        {
            try
            {
                var prs = await FindPrsForBranchAsync(branchName, creatorId);
                foreach (var pr in prs)
                {
                    var commentCount = await CountRealCommentsAsync(pr.Repository, pr.PullRequestId);
                    prStatuses.Add(pr with { Branch = label, CommentCount = commentCount });
                }
            }
            catch (Exception ex) when (!IsNetworkFailure(ex))
            {
                AppLogger.Error($"[ado] {ticketKey}, {label}: {ex.Message}");
                failedBranches.Add(label);
            }
        }

        return new PrLookupResult(prStatuses, failedBranches.Count == 0);
    }

    // No HTTP status means no response (DNS, connect, SSL); a timeout surfaces as TaskCanceledException.
    internal static bool IsNetworkFailure(Exception ex)
    {
        var httpRequestException = ex as HttpRequestException;
        if (httpRequestException != null)
        {
            return httpRequestException.StatusCode == null;
        }

        return ex is TaskCanceledException;
    }

    // The PAT owner's identity id, which is also PR.createdBy.id. Cached after the first success.
    private async Task<string> GetCreatorIdAsync()
    {
        if (_creatorId != null)
        {
            return _creatorId;
        }

        var uri = $"https://dev.azure.com/{Org}/_apis/connectionData?api-version=7.1-preview.1";
        using var doc = await GetJsonAsync(uri);

        _creatorId = doc.RootElement.GetProperty("authenticatedUser").GetProperty("id").GetString()
            ?? throw new InvalidOperationException("connectionData returned no authenticatedUser.id");
        return _creatorId;
    }

    private async Task<List<PrStatus>> FindPrsForBranchAsync(string branchName, string creatorId)
    {
        var sourceRef = Uri.EscapeDataString($"refs/heads/{branchName}");
        var uri = $"{ApiBase}/pullrequests?searchCriteria.sourceRefName={sourceRef}"
            + $"&searchCriteria.creatorId={creatorId}&searchCriteria.status=all&api-version=7.1";
        using var doc = await GetJsonAsync(uri);

        var result = new List<PrStatus>();
        foreach (var pr in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var pullRequestId = pr.GetProperty("pullRequestId").GetInt32();
            var status = pr.GetProperty("status").GetString() ?? string.Empty;
            var repository = pr.GetProperty("repository").GetProperty("name").GetString() ?? BackendRepository;
            result.Add(new PrStatus(pullRequestId, string.Empty, status, 0, PullRequestUrl(repository, pullRequestId), repository));
        }

        return result;
    }

    internal static string PullRequestUrl(string repository, int pullRequestId) =>
        $"https://dev.azure.com/{Org}/{Project}/_git/{repository}/pullrequest/{pullRequestId}";

    // commentType "system" covers auto-generated entries (policy updates, ref updates) no reviewer wrote.
    private async Task<int> CountRealCommentsAsync(string repository, int pullRequestId)
    {
        var uri = $"{ApiBase}/repositories/{Uri.EscapeDataString(repository)}/pullrequests/{pullRequestId}/threads?api-version=7.1";
        using var doc = await GetJsonAsync(uri);

        var realCommentCount = 0;
        foreach (var thread in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            if (!thread.TryGetProperty("comments", out var comments))
            {
                continue;
            }

            foreach (var comment in comments.EnumerateArray())
            {
                var commentType = comment.TryGetProperty("commentType", out var commentTypeValue)
                    ? commentTypeValue.GetString()
                    : null;
                var isDeleted = comment.TryGetProperty("isDeleted", out var isDeletedValue)
                    && isDeletedValue.GetBoolean();

                if (commentType != "system" && !isDeleted)
                {
                    realCommentCount++;
                }
            }
        }

        return realCommentCount;
    }

    private async Task<JsonDocument> GetJsonAsync(string uri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($":{_pat}")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await HttpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }
}
