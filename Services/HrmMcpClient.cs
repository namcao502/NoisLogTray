using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace NoisLogTray;

// HRM logging via the HRM MCP server (port of lib/hrm-mcp.ts). Browser-free: the
// hrm_ API key authenticates as Authorization: Bearer over Streamable HTTP. The
// log_timesheet tool creates-or-appends the task and auto-fills the Jira title.
internal static class HrmMcpClient
{
    private const string HrmMcpUrl = "https://api-hrm.nois.vn/mcp";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    // Verify the API key by opening an MCP session and listing tools. Success =
    // valid; an auth-looking failure (401/403) = rejected; any other failure =
    // unreachable. Best-effort: if the server does not enforce auth on connect it
    // cannot detect a bad key (Jira is the definitive check).
    internal static async Task<CredentialCheck> ValidateAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return CredentialCheck.Rejected;
        try
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(HrmMcpUrl),
                TransportMode = HttpTransportMode.StreamableHttp,
                ConnectionTimeout = TimeSpan.FromSeconds(15),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" },
            });

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: cts.Token);
            await client.ListToolsAsync(cancellationToken: cts.Token);
            return CredentialCheck.Valid;
        }
        catch (Exception e)
        {
            return LooksLikeAuthFailure(e) ? CredentialCheck.Rejected : CredentialCheck.Unreachable;
        }
    }

    private static bool LooksLikeAuthFailure(Exception e)
    {
        var msg = e.Message.ToLowerInvariant();
        return msg.Contains("401") || msg.Contains("unauthorized")
            || msg.Contains("403") || msg.Contains("forbidden");
    }

    // Log each ticket via log_timesheet. A slot straddling lunch yields two
    // segments -> two calls (first creates the task, second appends). Returns on
    // the first hard error; LOGTIME_OVERLAP is treated as an idempotent skip.
    internal static async Task<(bool Success, string? Error)> LogTicketsAsync(
        IReadOnlyList<string> tickets,
        DateOnly date,
        string apiKey,
        string projectId,
        IReadOnlyList<int>? minutes = null,
        Action<string>? onLog = null,
        Action<int, int>? onProgress = null,
        CancellationToken ct = default)
    {
        var isoDate = Hcm.ApiDate(date);
        void Emit(string line) => onLog?.Invoke(line);
        Emit($"[hrm-log] Tickets: {string.Join(", ", tickets)}, Date: {isoDate}");

        if (string.IsNullOrWhiteSpace(apiKey))
            return (false, "HRM_API_KEY is not set (required for HRM MCP logging).");

        // Custom per-ticket durations when supplied, else the even split.
        IReadOnlyList<TimeSlot> SlotsFor(int index) =>
            minutes is null ? TimeSlots.Get(tickets.Count, index) : TimeSlots.Get(minutes, index);

        // Total sub-units = every ticket's time slots (a lunch-straddling slot splits in two).
        var total = 0;
        for (var i = 0; i < tickets.Count; i++) total += SlotsFor(i).Count;
        var done = 0;

        try
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(HrmMcpUrl),
                TransportMode = HttpTransportMode.StreamableHttp,
                ConnectionTimeout = TimeSpan.FromSeconds(30),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" },
            });

            Emit($"[hrm-mcp] Connecting to {HrmMcpUrl} ...");
            // Connecting opens a session (no side effects); retry transient transport faults.
            await using var client = await Retry.OnTransientAsync(
                c => McpClient.CreateAsync(transport, cancellationToken: c), onLog, ct: ct);

            var errors = new List<string>();
            for (var i = 0; i < tickets.Count; i++)
            {
                var ticket = tickets[i];
                foreach (var slot in SlotsFor(i))
                {
                    var args = Timesheet.BuildArgs(projectId, ticket, isoDate, slot);
                    Emit($"[hrm-mcp] log_timesheet {ticket} {args["startTime"]}-{args["stopTime"]}");

                    // Retry transient transport faults: a re-send after a lost response is
                    // absorbed as LOGTIME_OVERLAP below, so it never double-logs. Each attempt
                    // gets a fresh timeout so a retry is not born already cancelled.
                    var res = await Retry.OnTransientAsync(async c =>
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(c);
                        cts.CancelAfter(RequestTimeout);
                        return await client.CallToolAsync("log_timesheet", args, cancellationToken: cts.Token);
                    }, onLog, ct: ct);

                    var text = res.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
                    var env = Timesheet.ParseToolEnvelope(res.IsError is true, text);
                    if (!env.Success)
                    {
                        // Overlap = this slot is already logged -> treat as done (idempotent re-run).
                        if (env.Code == "LOGTIME_OVERLAP")
                        {
                            Emit($"[hrm-mcp] {ticket} {args["startTime"]}-{args["stopTime"]} already logged (overlap), skipping");
                            done++;
                            onProgress?.Invoke(done, total);
                            continue;
                        }
                        // Hard error: record it and move on to the next ticket (skip this
                        // ticket's remaining slots) so one bad ticket doesn't block the rest.
                        var reason = env.Error ?? "unknown error";
                        Emit($"[hrm-mcp] {ticket} rejected: {reason}");
                        errors.Add($"{ticket}: {reason}");
                        break;
                    }

                    Emit($"[hrm-mcp] {ticket} {args["startTime"]}-{args["stopTime"]} logged");
                    done++;
                    onProgress?.Invoke(done, total);
                }
            }

            return errors.Count == 0
                ? (true, null)
                : (false, "HRM rejected " + string.Join("; ", errors));
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
    }

    // Read the total hours logged (all projects) for each date via get_my_day_logs.
    // Returns a per-date map; a date maps to null when the read errors or the shape is
    // unrecognised (shown as "unknown"). One MCP session for the whole batch.
    internal static async Task<IReadOnlyDictionary<DateOnly, double?>> GetDayHoursAsync(
        IReadOnlyList<DateOnly> dates,
        string apiKey,
        Action<string>? onLog = null,
        CancellationToken ct = default)
    {
        void Emit(string line) => onLog?.Invoke(line);
        var result = new Dictionary<DateOnly, double?>();
        foreach (var d in dates) result[d] = null;

        if (string.IsNullOrWhiteSpace(apiKey) || dates.Count == 0) return result;

        try
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(HrmMcpUrl),
                TransportMode = HttpTransportMode.StreamableHttp,
                ConnectionTimeout = TimeSpan.FromSeconds(30),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" },
            });

            Emit($"[hrm-check] Connecting to {HrmMcpUrl} ...");
            // Connecting opens a session (no side effects); retry transient transport faults.
            await using var client = await Retry.OnTransientAsync(
                c => McpClient.CreateAsync(transport, cancellationToken: c), onLog, ct: ct);

            foreach (var date in dates)
            {
                var iso = Hcm.ApiDate(date);
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(RequestTimeout);
                    // projectId must be present even though it is "optional" (the schema
                    // marks it required, like log_timesheet); null = all projects.
                    var res = await client.CallToolAsync("get_my_day_logs",
                        new Dictionary<string, object?> { ["workDate"] = iso, ["projectId"] = null },
                        cancellationToken: cts.Token);

                    var text = res.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
                    var hours = Timesheet.ParseDayHours(res.IsError is true, text);
                    result[date] = hours;
                    Emit($"[hrm-check] {iso}: {(hours is null ? "unknown" : hours.Value.ToString("0.0") + "h")}");
                }
                catch (Exception e)
                {
                    Emit($"[hrm-check] {iso}: error ({e.Message})");
                }
            }
        }
        catch (Exception e)
        {
            Emit($"[hrm-check] connect failed: {e.Message}");
        }
        return result;
    }
}
