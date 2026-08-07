namespace NoisLogTray;

// Orchestrates the two logging destinations (TSC via Graph, HRM via MCP) on top
// of AppConfig. Mirrors the web app's API routes, minus the HTTP layer. All
// methods accept an onLog callback so the UI can stream progress.
internal sealed class LoggingService
{
    private readonly AppConfig _config;
    private readonly IJiraClient _jira;

    // Cached sniffed Graph token, reused until shortly before it expires so repeated
    // TSC operations do not each relaunch headless Chrome. Guarded by _tokenLock.
    private static readonly TimeSpan TokenSafetyBuffer = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _cachedTokenExpiry;

    internal LoggingService(AppConfig config)
    {
        _config = config;
        _jira = new JiraClient(config.JiraBaseUrl, config.JiraEmail, config.JiraToken);
    }

    internal Task<JiraVerifyResult> VerifyAsync(string ticketId, CancellationToken ct = default)
        => _jira.VerifyTicketAsync(ticketId, ct);

    internal Task<IReadOnlyList<JiraSuggestion>> GetMyTicketsAsync(int limit = 5, CancellationToken ct = default)
        => _jira.GetMyTicketsAsync(limit, ct);

    // Prefer MS_GRAPH_TOKEN override; otherwise return a cached sniffed token while
    // it is still valid, and only sniff a fresh one (launches headless Chrome once,
    // guarded by BrowserLock) when there is no usable cached token.
    internal async Task<string?> AcquireGraphTokenAsync(Action<string>? onLog = null)
    {
        if (!string.IsNullOrEmpty(_config.MsGraphToken))
        {
            onLog?.Invoke("[tsc] Using MS_GRAPH_TOKEN override.");
            return _config.MsGraphToken;
        }

        await _tokenLock.WaitAsync();
        try
        {
            if (_cachedToken != null && DateTimeOffset.UtcNow < _cachedTokenExpiry - TokenSafetyBuffer)
            {
                onLog?.Invoke("[tsc] Reusing cached Graph token.");
                return _cachedToken;
            }

            onLog?.Invoke("[tsc] Sniffing Graph token from the saved TSC session...");
            var sniff = await TscTokenSniffer.SniffGraphTokenAsync(onLog);
            if (sniff is null) return null;

            _cachedToken = sniff.Value.Token;
            // Fall back to a short window if the token is opaque (no decodable exp).
            _cachedTokenExpiry = GraphTscClient.DecodeJwtExpiry(sniff.Value.Token)
                ?? DateTimeOffset.UtcNow.AddMinutes(30);
            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    // Drop any cached Graph token so the next log re-sniffs. Call after a re-auth so
    // a stale token from a previous session is not reused.
    internal void InvalidateGraphToken()
    {
        _tokenLock.Wait();
        try
        {
            _cachedToken = null;
            _cachedTokenExpiry = default;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    internal async Task<(bool Success, string Cell, string? Error)> LogTscAsync(
        string ticketString, IReadOnlyList<DateOnly> dates, Action<string>? onLog = null,
        Action<int, int>? onProgress = null, CancellationToken ct = default)
    {
        var token = await AcquireGraphTokenAsync(onLog);
        if (string.IsNullOrEmpty(token))
            return (false, "", "No Graph token (session may be logged out; run Check TSC / Re-authenticate).");
        return await GraphTscClient.WriteTicketAsync(ticketString, dates, token, _config.Graph, onLog, onProgress, ct);
    }

    internal Task<(bool Success, string? Error)> LogHrmAsync(
        IReadOnlyList<string> tickets, DateOnly date, Action<string>? onLog = null,
        Action<int, int>? onProgress = null, CancellationToken ct = default)
        => HrmMcpClient.LogTicketsAsync(tickets, date, _config.HrmApiKey, _config.HrmProjectId, onLog, onProgress, ct);

    // Log one date's tickets to both destinations in parallel (HRM uses no
    // browser, so it cannot contend with the TSC sniff).
    internal async Task<EntryLogResult> LogEntryAsync(
        DateOnly date, IReadOnlyList<string> tickets, string? graphToken, Action<string>? onLog = null,
        Action<int, int>? onTscProgress = null, Action<int, int>? onHrmProgress = null, CancellationToken ct = default)
    {
        var ticketString = string.Join(", ", tickets);

        Task<(bool Success, string Cell, string? Error)> tscTask = string.IsNullOrEmpty(graphToken)
            ? Task.FromResult((false, "", (string?)"No Graph token (session may be logged out)."))
            : GraphTscClient.WriteTicketAsync(ticketString, new[] { date }, graphToken, _config.Graph, onLog, onTscProgress, ct);
        var hrmTask = HrmMcpClient.LogTicketsAsync(tickets, date, _config.HrmApiKey, _config.HrmProjectId, onLog, onHrmProgress, ct);

        await Task.WhenAll(tscTask, hrmTask);
        return new EntryLogResult(tscTask.Result.Success, tscTask.Result.Error, hrmTask.Result.Success, hrmTask.Result.Error);
    }

    // Read back each date's coverage from both destinations for the weekly check:
    // TSC ticket cell + HRM total hours. Acquires one Graph token, then runs the TSC
    // read and HRM hours in parallel (HRM is browser-free). Future dates are not read
    // (HRM rejects future stop times and nothing is logged yet) and come back as nulls.
    internal async Task<IReadOnlyList<DayCoverage>> CheckWeekAsync(
        IReadOnlyList<DateOnly> dates, Action<string>? onLog = null, CancellationToken ct = default)
    {
        var today = Hcm.Today();
        var readable = dates.Where(d => d <= today).ToList();

        var token = await AcquireGraphTokenAsync(onLog);

        var tscTask = string.IsNullOrEmpty(token)
            ? Task.FromResult((IReadOnlyDictionary<DateOnly, string?>)new Dictionary<DateOnly, string?>())
            : GraphTscClient.ReadTicketsAsync(readable, token, _config.Graph, onLog, ct);
        var hrmTask = HrmMcpClient.GetDayHoursAsync(readable, _config.HrmApiKey, onLog, ct);

        await Task.WhenAll(tscTask, hrmTask);

        var result = new List<DayCoverage>();
        foreach (var d in dates)
        {
            tscTask.Result.TryGetValue(d, out var ticket);
            hrmTask.Result.TryGetValue(d, out var hours);
            result.Add(new DayCoverage(d, hours, ticket));
        }
        return result;
    }

    // Drain the queue: sniff one Graph token, log every entry to both
    // destinations, and remove only the entries that fully succeeded (re-runs are
    // idempotent via TSC skip-if-equal + HRM LOGTIME_OVERLAP, so kept entries are
    // safe to retry).
    internal async Task<DrainResult> DrainQueueAsync(Action<string>? onLog = null, CancellationToken ct = default)
    {
        var entries = TicketQueue.Read().ToList();
        if (entries.Count == 0)
        {
            onLog?.Invoke("[drain] Queue is empty; nothing to log.");
            return new DrainResult(0, 0, 0);
        }

        onLog?.Invoke($"[drain] {entries.Count} queued entr{(entries.Count == 1 ? "y" : "ies")}.");
        var token = await AcquireGraphTokenAsync(onLog);

        // Entries we are done with (logged, or a permanently-bad date). Removed from the
        // CURRENT queue at the end so anything queued mid-drain survives.
        var processed = new List<QueueEntry>();
        var logged = 0;
        var kept = 0;
        foreach (var entry in entries)
        {
            if (!DateOnly.TryParseExact(entry.Date, "yyyy-MM-dd", out var date))
            {
                onLog?.Invoke($"[drain] Dropping entry with bad date '{entry.Date}'.");
                processed.Add(entry);
                continue;
            }

            onLog?.Invoke($"[drain] Logging {entry.Date}: {string.Join(", ", entry.Tickets)}");
            var result = await LogEntryAsync(date, entry.Tickets, token, onLog, ct: ct);
            if (result.AllSuccess)
            {
                logged++;
                processed.Add(entry);
            }
            else
            {
                kept++;
                onLog?.Invoke($"[drain] {entry.Date} kept for retry (tsc={(result.TscSuccess ? "ok" : result.TscError)}, hrm={(result.HrmSuccess ? "ok" : result.HrmError)}).");
            }
        }

        TicketQueue.RemoveLogged(processed);
        onLog?.Invoke($"[drain] Done. logged={logged}, kept={kept}.");
        return new DrainResult(entries.Count, logged, kept);
    }
}
