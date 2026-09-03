using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace NoisLogTray;

// Write TSC tickets to the shared Excel file via the Microsoft Graph Excel API
// (port of lib/graph-tsc.ts). The caller supplies a delegated Graph token
// (sniffed from the TSC session, or MS_GRAPH_TOKEN). row = 2 + dayOfYear; writes
// M and J, overwriting; a fail-closed read-back of column B guards wrong-day logs.
internal static class GraphTscClient
{
    private const string Graph = "https://graph.microsoft.com/v1.0";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private sealed record DriveItemRef(string DriveId, string ItemId);

    // Graph "shares" API share id: base64(url) -> url-safe -> "u!" prefix, no padding.
    internal static string EncodeShareId(string url)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(url));
        return "u!" + b64.TrimEnd('=').Replace('/', '_').Replace('+', '-');
    }

    // Parse a JWT's payload (middle segment, base64url) into a JsonDocument, or null
    // if the token is opaque/unparseable. Caller disposes the document.
    private static JsonDocument? ParseJwtPayload(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = (payload.Length % 4) switch
            {
                2 => payload + "==",
                3 => payload + "=",
                _ => payload,
            };
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            return JsonDocument.Parse(json);
        }
        catch
        {
            return null;
        }
    }

    // Decode a JWT's delegated scopes (scp/roles) for diagnostics.
    internal static string DecodeJwtScopes(string token)
    {
        using var doc = ParseJwtPayload(token);
        if (doc is null) return "";
        var root = doc.RootElement;
        if (root.TryGetProperty("scp", out var scp) && scp.ValueKind == JsonValueKind.String)
            return scp.GetString() ?? "";
        if (root.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array)
            return string.Join(" ", roles.EnumerateArray().Select(r => r.GetString()));
        return "";
    }

    // Decode a JWT's expiry (exp, Unix seconds). Null for opaque tokens or if absent.
    internal static DateTimeOffset? DecodeJwtExpiry(string token)
    {
        using var doc = ParseJwtPayload(token);
        if (doc is null) return null;
        if (doc.RootElement.TryGetProperty("exp", out var exp) && exp.ValueKind == JsonValueKind.Number)
            return DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64());
        return null;
    }

    internal static async Task<(bool Success, string Cell, string? Error)> WriteTicketAsync(
        string ticket,
        IReadOnlyList<DateOnly> dates,
        string token,
        GraphTscOptions options,
        Action<string>? onLog = null,
        Action<int, int>? onProgress = null,
        CancellationToken ct = default)
    {
        void Emit(string line) => onLog?.Invoke(line);

        if (string.IsNullOrWhiteSpace(token))
            return (false, "", "No Graph token (sniff failed and MS_GRAPH_TOKEN not set).");

        var columns = options.Columns != null && options.Columns.Count != 0
            ? options.Columns
            : TscCells.TargetColumns;
        var effectiveDates = dates.Count > 0 ? dates : new[] { Hcm.Today() };
        var totalCells = effectiveDates.Count * columns.Count;
        var doneCells = 0;

        try
        {
            var reference = await ResolveDriveItemAsync(token, options, Emit, ct);
            Emit($"[graph-tsc] Resolved workbook (driveId={Trunc(reference.DriveId)}..., itemId={Trunc(reference.ItemId)}...)");

            var sessionId = await CreateSessionAsync(token, reference, ct);
            Emit("[graph-tsc] Opened persistent workbook session (persistChanges)");

            try
            {
                var writtenCells = new List<string>();
                foreach (var d in effectiveDates)
                {
                    var worksheet = string.IsNullOrEmpty(options.Worksheet) ? TscCells.GetWorksheetForDate(d) : options.Worksheet;
                    var cells = TscCells.GetCellsForDate(d, columns);
                    var row = TscCells.GetRowForDate(d);
                    var bCell = $"B{row}";
                    var expected = TscCells.GetExpectedDateLabel(d);
                    Emit($"[graph-tsc] Worksheet: \"{worksheet}\"");

                    // Date safety (fail-closed): read column B and abort on any mismatch.
                    var bVal = await ReadCellAsync(token, reference, worksheet, bCell, sessionId, ct);
                    Emit($"[graph-tsc] Date check {bCell}: read \"{bVal}\", expected \"{expected}\"");
                    if (!TscCells.DateLabelsMatch(bVal, expected))
                    {
                        var why = bVal.Length == 0
                            ? $"Date check could not read {bCell} (empty)."
                            : $"Date safety check failed: {bCell} shows \"{bVal}\", expected \"{expected}\".";
                        return (false, string.Join(", ", cells), $"{why} Aborting to avoid wrong-day logging.");
                    }

                    foreach (var cell in cells)
                    {
                        var cur = await ReadCellAsync(token, reference, worksheet, cell, sessionId, ct);
                        if (cur == ticket)
                        {
                            Emit($"[graph-tsc] {cell} already \"{ticket}\", skipping");
                            writtenCells.Add(cell);
                            doneCells++;
                            onProgress?.Invoke(doneCells, totalCells);
                            continue;
                        }
                        if (cur.Length != 0) Emit($"[graph-tsc] {cell} had \"{cur}\", overwriting");
                        await WriteCellAsync(token, reference, worksheet, cell, ticket, sessionId, ct);
                        // Real work replacing an OFF marker must lose the yellow with it.
                        if (cur == TscCells.OffMarker)
                            await ClearFillAsync(token, reference, worksheet, cell, sessionId, ct);
                        Emit($"[graph-tsc] Wrote \"{ticket}\" to {cell}");
                        writtenCells.Add(cell);
                        doneCells++;
                        onProgress?.Invoke(doneCells, totalCells);
                    }
                }

                return (true, string.Join(", ", writtenCells), null);
            }
            finally
            {
                await CloseSessionAsync(token, reference, sessionId);
                Emit("[graph-tsc] Closed workbook session");
            }
        }
        catch (Exception e)
        {
            Emit($"[graph-tsc] Error: {e.Message}");
            return (false, "", e.Message);
        }
    }

    // Mark each date OFF. Without overwrite a cell holding real work is left alone and
    // reported as skipped, so the automatic sync can never destroy a ticket.
    internal static async Task<OffWriteResult> WriteOffAsync(
        IReadOnlyList<DateOnly> dates,
        string token,
        GraphTscOptions options,
        bool overwrite,
        Action<string>? onLog = null,
        Action<int, int>? onProgress = null,
        CancellationToken ct = default)
    {
        void Emit(string line) => onLog?.Invoke(line);
        var marked = new List<DateOnly>();
        var skipped = new List<DateOnly>();

        if (string.IsNullOrWhiteSpace(token))
            return new OffWriteResult(marked, skipped, "No Graph token (sniff failed and MS_GRAPH_TOKEN not set).");
        if (dates.Count == 0) return new OffWriteResult(marked, skipped, null);

        var columns = options.Columns != null && options.Columns.Count != 0
            ? options.Columns
            : TscCells.TargetColumns;
        var totalCells = dates.Count * columns.Count;
        var doneCells = 0;

        try
        {
            var reference = await ResolveDriveItemAsync(token, options, Emit, ct);
            var sessionId = await CreateSessionAsync(token, reference, ct);
            Emit("[graph-off] Opened persistent workbook session (persistChanges)");

            try
            {
                foreach (var date in dates)
                {
                    var worksheet = string.IsNullOrEmpty(options.Worksheet) ? TscCells.GetWorksheetForDate(date) : options.Worksheet;
                    var row = TscCells.GetRowForDate(date);
                    var expected = TscCells.GetExpectedDateLabel(date);

                    // Date safety (fail-closed): same column B guard as WriteTicketAsync.
                    var bVal = await ReadCellAsync(token, reference, worksheet, $"B{row}", sessionId, ct);
                    if (!TscCells.DateLabelsMatch(bVal, expected))
                    {
                        var why = bVal.Length == 0
                            ? $"Date check could not read B{row} (empty)."
                            : $"Date safety check failed: B{row} shows \"{bVal}\", expected \"{expected}\".";
                        return new OffWriteResult(marked, skipped, $"{why} Aborting to avoid wrong-day logging.");
                    }

                    var allCellsOff = true;
                    foreach (var cell in TscCells.GetCellsForDate(date, columns))
                    {
                        var cur = await ReadCellAsync(token, reference, worksheet, cell, sessionId, ct);
                        if (cur.Length != 0 && cur != TscCells.OffMarker && !overwrite)
                        {
                            Emit($"[graph-off] {cell} holds \"{cur}\"; leaving it (not overwriting real work)");
                            allCellsOff = false;
                            doneCells++;
                            onProgress?.Invoke(doneCells, totalCells);
                            continue;
                        }

                        if (cur != TscCells.OffMarker)
                        {
                            if (cur.Length != 0) Emit($"[graph-off] {cell} had \"{cur}\", overwriting");
                            await WriteCellAsync(token, reference, worksheet, cell, TscCells.OffMarker, sessionId, ct);
                        }
                        // Outside the write guard on purpose: a hand-typed OFF gets coloured too.
                        await SetFillAsync(token, reference, worksheet, cell, TscCells.OffFillColor, sessionId, ct);
                        Emit($"[graph-off] {cell} = \"{TscCells.OffMarker}\" on {TscCells.OffFillColor}");
                        doneCells++;
                        onProgress?.Invoke(doneCells, totalCells);
                    }

                    if (allCellsOff) marked.Add(date);
                    else skipped.Add(date);
                }

                return new OffWriteResult(marked, skipped, null);
            }
            finally
            {
                await CloseSessionAsync(token, reference, sessionId);
                Emit("[graph-off] Closed workbook session");
            }
        }
        catch (Exception e)
        {
            Emit($"[graph-off] Error: {e.Message}");
            return new OffWriteResult(marked, skipped, e.Message);
        }
    }

    // Read the ticket text present in the target cells for each date (for the weekly
    // coverage check). Returns a per-date map: the first non-empty target cell's text,
    // or null when the day is empty, the sheet's date cell (B) does not match, or a read
    // fails. One persistent workbook session for the whole batch.
    internal static async Task<IReadOnlyDictionary<DateOnly, string?>> ReadTicketsAsync(
        IReadOnlyList<DateOnly> dates,
        string token,
        GraphTscOptions options,
        Action<string>? onLog = null,
        CancellationToken ct = default)
    {
        void Emit(string line) => onLog?.Invoke(line);
        var result = new Dictionary<DateOnly, string?>();
        foreach (var d in dates) result[d] = null;

        if (string.IsNullOrWhiteSpace(token) || dates.Count == 0) return result;

        var columns = options.Columns != null && options.Columns.Count != 0
            ? options.Columns
            : TscCells.TargetColumns;

        try
        {
            var reference = await ResolveDriveItemAsync(token, options, Emit, ct);
            var sessionId = await CreateSessionAsync(token, reference, ct);
            try
            {
                foreach (var d in dates)
                {
                    var worksheet = string.IsNullOrEmpty(options.Worksheet) ? TscCells.GetWorksheetForDate(d) : options.Worksheet;
                    var row = TscCells.GetRowForDate(d);
                    var expected = TscCells.GetExpectedDateLabel(d);

                    // Sanity: only trust cells on a row whose column B matches the date.
                    var bVal = await ReadCellAsync(token, reference, worksheet, $"B{row}", sessionId, ct);
                    if (!TscCells.DateLabelsMatch(bVal, expected))
                    {
                        Emit($"[tsc-check] {d:yyyy-MM-dd}: B{row} \"{bVal}\" != expected \"{expected}\"; skipping");
                        continue;
                    }

                    string? found = null;
                    foreach (var cell in TscCells.GetCellsForDate(d, columns))
                    {
                        var val = await ReadCellAsync(token, reference, worksheet, cell, sessionId, ct);
                        if (val.Length != 0) { found = val; break; }
                    }
                    result[d] = found;
                    Emit($"[tsc-check] {d:yyyy-MM-dd}: {(found is null ? "(empty)" : found)}");
                }
            }
            finally
            {
                await CloseSessionAsync(token, reference, sessionId);
            }
        }
        catch (Exception e)
        {
            Emit($"[tsc-check] Error: {e.Message}");
        }
        return result;
    }

    private static async Task<DriveItemRef> ResolveDriveItemAsync(string token, GraphTscOptions options, Action<string> emit, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(options.DriveId) && !string.IsNullOrEmpty(options.ItemId))
        {
            emit("[graph-tsc] Using TSC_GRAPH_DRIVE_ID / TSC_GRAPH_ITEM_ID (skipping shares resolve)");
            return new DriveItemRef(options.DriveId, options.ItemId);
        }

        var shareUrl = string.IsNullOrEmpty(options.ShareUrl) ? TscCells.ExcelUrl : options.ShareUrl;
        // Idempotent GET; retry transient transport failures.
        return await Retry.OnTransientAsync(async c =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{Graph}/shares/{EncodeShareId(shareUrl)}/driveItem");
            AddAuth(req, token);
            using var res = await Http.SendAsync(req, c);
            if (!res.IsSuccessStatusCode) throw await GraphErrorAsync(res, "shares/driveItem");

            var json = await res.Content.ReadAsStringAsync(c);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var itemId = root.TryGetProperty("id", out var id) ? id.GetString() : null;
            var driveId = root.TryGetProperty("parentReference", out var pr) && pr.TryGetProperty("driveId", out var dv)
                ? dv.GetString()
                : null;
            if (string.IsNullOrEmpty(driveId) || string.IsNullOrEmpty(itemId))
                throw new InvalidOperationException("shares/driveItem: response missing driveId/itemId");
            return new DriveItemRef(driveId, itemId);
        }, ct: ct);
    }

    // A persistent workbook session (persistChanges:true) so PATCHes commit to the
    // shared/co-authored file instead of a throwaway session that never flushes.
    private static async Task<string> CreateSessionAsync(string token, DriveItemRef reference, CancellationToken ct)
    {
        // A session is disposable (auto-expires ~5 min idle), so a retry that opens a
        // second one is harmless; retry transient transport failures.
        return await Retry.OnTransientAsync(async c =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{Graph}/drives/{reference.DriveId}/items/{reference.ItemId}/workbook/createSession")
            {
                Content = new StringContent("{\"persistChanges\":true}", Encoding.UTF8, "application/json"),
            };
            AddAuth(req, token);
            using var res = await Http.SendAsync(req, c);
            if (!res.IsSuccessStatusCode) throw await GraphErrorAsync(res, "createSession");

            var json = await res.Content.ReadAsStringAsync(c);
            using var doc = JsonDocument.Parse(json);
            var sessionId = doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
            if (string.IsNullOrEmpty(sessionId)) throw new InvalidOperationException("createSession: response missing session id");
            return sessionId;
        }, ct: ct);
    }

    private static async Task CloseSessionAsync(string token, DriveItemRef reference, string sessionId)
    {
        // Best-effort: the session auto-expires (~5 min idle) even if this fails.
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{Graph}/drives/{reference.DriveId}/items/{reference.ItemId}/workbook/closeSession");
            AddAuth(req, token);
            req.Headers.Add("workbook-session-id", sessionId);
            using var _ = await Http.SendAsync(req);
        }
        catch
        {
            // ignore
        }
    }

    private static async Task<string> ReadCellAsync(string token, DriveItemRef reference, string worksheet, string address, string sessionId, CancellationToken ct)
    {
        // Idempotent read; retry transient transport failures.
        return await Retry.OnTransientAsync(async c =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, RangeUrl(reference, worksheet, address));
            AddAuth(req, token);
            req.Headers.Add("workbook-session-id", sessionId);
            using var res = await Http.SendAsync(req, c);
            if (!res.IsSuccessStatusCode) throw await GraphErrorAsync(res, $"read {address}");

            var json = await res.Content.ReadAsStringAsync(c);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var value = FirstCell(root, "text") ?? FirstCell(root, "values") ?? "";
            return value.Trim();
        }, ct: ct);
    }

    private static async Task WriteCellAsync(string token, DriveItemRef reference, string worksheet, string address, string value, string sessionId, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { values = new[] { new[] { value } } });
        // PATCH writes the same value to the same cell, so re-sending is idempotent;
        // retry transient transport failures.
        await Retry.OnTransientAsync(async c =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Patch, RangeUrl(reference, worksheet, address))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            AddAuth(req, token);
            req.Headers.Add("workbook-session-id", sessionId);
            using var res = await Http.SendAsync(req, c);
            if (!res.IsSuccessStatusCode) throw await GraphErrorAsync(res, $"write {address}");
            return true;
        }, ct: ct);
    }

    private static async Task SetFillAsync(string token, DriveItemRef reference, string worksheet, string address, string color, string sessionId, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { color });
        // PATCHing the same color to the same cell is idempotent; retry transient faults.
        await Retry.OnTransientAsync(async c =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Patch, $"{RangeUrl(reference, worksheet, address)}/format/fill")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            AddAuth(req, token);
            req.Headers.Add("workbook-session-id", sessionId);
            using var res = await Http.SendAsync(req, c);
            if (!res.IsSuccessStatusCode) throw await GraphErrorAsync(res, $"fill {address}");
            return true;
        }, ct: ct);
    }

    private static async Task ClearFillAsync(string token, DriveItemRef reference, string worksheet, string address, string sessionId, CancellationToken ct)
    {
        // Best-effort: a leftover fill is cosmetic, so it must not fail the write itself.
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{RangeUrl(reference, worksheet, address)}/format/fill/clear");
            AddAuth(req, token);
            req.Headers.Add("workbook-session-id", sessionId);
            using var _ = await Http.SendAsync(req, ct);
        }
        catch
        {
            // ignore
        }
    }

    private static string? FirstCell(JsonElement root, string prop)
    {
        if (!root.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return null;
        var row0 = arr[0];
        if (row0.ValueKind != JsonValueKind.Array || row0.GetArrayLength() == 0) return null;
        var cell = row0[0];
        return cell.ValueKind == JsonValueKind.String ? cell.GetString() : cell.ToString();
    }

    private static string RangeUrl(DriveItemRef reference, string worksheet, string address)
    {
        var ws = Uri.EscapeDataString(worksheet);
        var addr = Uri.EscapeDataString(address);
        return $"{Graph}/drives/{reference.DriveId}/items/{reference.ItemId}/workbook/worksheets('{ws}')/range(address='{addr}')";
    }

    private static void AddAuth(HttpRequestMessage req, string token)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static async Task<Exception> GraphErrorAsync(HttpResponseMessage res, string what)
    {
        var body = "";
        try { body = await res.Content.ReadAsStringAsync(); } catch { /* ignore */ }
        var snippet = body.Length > 300 ? body[..300] : body;
        return new InvalidOperationException($"{what} -> {(int)res.StatusCode} {res.ReasonPhrase}: {snippet}");
    }

    private static string Trunc(string s) => s.Length > 10 ? s[..10] : s;
}
