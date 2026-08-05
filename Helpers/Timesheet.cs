using System.Text.Json;

namespace NoisLogTray;

// Pure HRM log_timesheet helpers (port of lib/hrm-timesheet-args.ts). Kept
// SDK-free so they can be unit-tested without the MCP client.
internal static class Timesheet
{
    // Map one ticket + time-slot segment to the log_timesheet tool args. All 11
    // fields are required by the tool schema; description/comment/issueType/
    // projectStageId are null so HRM auto-fills from Jira. idempotencyKey is null:
    // the server treats an arbitrary string as already-seen (silent no-op), so we
    // rely on its LOGTIME_OVERLAP check to make re-runs safe.
    internal static Dictionary<string, object?> BuildArgs(string projectId, string ticket, string isoDate, TimeSlot slot)
        => new()
        {
            ["projectId"] = projectId,
            ["taskId"] = ticket,
            ["workDate"] = isoDate,
            ["startTime"] = $"{slot.Start}:00",
            ["stopTime"] = $"{slot.End}:00",
            ["description"] = null,
            ["comment"] = null,
            ["isBillable"] = true,
            ["issueType"] = null,
            ["projectStageId"] = null,
            ["idempotencyKey"] = null,
        };

    // Parse the tool result. The business result is a JSON string inside the text
    // content: { success, data, error: { code, message } }. A tool-level isError
    // OR a business success:false (e.g. FUTURE_STOP) is a failure -- the MCP call
    // succeeding does NOT mean the write succeeded.
    internal static ToolEnvelope ParseToolEnvelope(bool isError, string? text)
    {
        text ??= "";
        if (isError) return new ToolEnvelope(false, text.Length > 0 ? text : "MCP tool error", null);

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var s) &&
                (s.ValueKind == JsonValueKind.True || s.ValueKind == JsonValueKind.False))
            {
                var success = s.GetBoolean();
                string? code = null;
                string? error = null;
                if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
                {
                    string? message = null;
                    if (err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String) code = c.GetString();
                    if (err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString();
                    var parts = new[] { code, message }.Where(p => !string.IsNullOrEmpty(p)).ToArray();
                    error = parts.Length > 0 ? string.Join(": ", parts) : null;
                }
                return new ToolEnvelope(success, error, code);
            }
        }
        catch (JsonException)
        {
            // not a JSON envelope -> treat as success (matches TS fall-through)
        }

        return new ToolEnvelope(true, null, null);
    }
}
