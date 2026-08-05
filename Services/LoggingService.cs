namespace NoisLogTray;

// Orchestrates the two logging destinations (TSC via Graph, HRM via MCP) on top
// of AppConfig. Mirrors the web app's API routes, minus the HTTP layer. All
// methods accept an onLog callback so the UI can stream progress.
internal sealed class LoggingService
{
    private readonly AppConfig _config;
    private readonly IJiraClient _jira;

    internal LoggingService(AppConfig config)
    {
        _config = config;
        _jira = new JiraClient(config.JiraBaseUrl, config.JiraEmail, config.JiraToken);
    }

    internal Task<JiraVerifyResult> VerifyAsync(string ticketId, CancellationToken ct = default)
        => _jira.VerifyTicketAsync(ticketId, ct);

    internal Task<IReadOnlyList<JiraSuggestion>> GetMyTicketsAsync(int limit = 5, CancellationToken ct = default)
        => _jira.GetMyTicketsAsync(limit, ct);

    // Prefer MS_GRAPH_TOKEN override; otherwise sniff a delegated token from the
    // saved TSC session (launches headless Chrome once, guarded by BrowserLock).
    internal async Task<string?> AcquireGraphTokenAsync(Action<string>? onLog = null)
    {
        if (!string.IsNullOrEmpty(_config.MsGraphToken))
        {
            onLog?.Invoke("[tsc] Using MS_GRAPH_TOKEN override.");
            return _config.MsGraphToken;
        }
        onLog?.Invoke("[tsc] Sniffing Graph token from the saved TSC session...");
        var sniff = await TscTokenSniffer.SniffGraphTokenAsync(onLog);
        return sniff?.Token;
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

        var remaining = new List<QueueEntry>();
        var logged = 0;
        foreach (var entry in entries)
        {
            if (!DateOnly.TryParseExact(entry.Date, "yyyy-MM-dd", out var date))
            {
                onLog?.Invoke($"[drain] Skipping entry with bad date '{entry.Date}'.");
                continue;
            }

            onLog?.Invoke($"[drain] Logging {entry.Date}: {string.Join(", ", entry.Tickets)}");
            var result = await LogEntryAsync(date, entry.Tickets, token, onLog, ct: ct);
            if (result.AllSuccess)
            {
                logged++;
            }
            else
            {
                remaining.Add(entry);
                onLog?.Invoke($"[drain] {entry.Date} kept for retry (tsc={(result.TscSuccess ? "ok" : result.TscError)}, hrm={(result.HrmSuccess ? "ok" : result.HrmError)}).");
            }
        }

        TicketQueue.Write(remaining);
        onLog?.Invoke($"[drain] Done. logged={logged}, kept={remaining.Count}.");
        return new DrainResult(entries.Count, logged, remaining.Count);
    }
}
