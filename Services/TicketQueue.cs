using System.Text.Json;
using System.Text.RegularExpressions;

namespace NoisLogTray;

// Persistence for the "queue a ticket now, auto-log at 18:00" flow (port of
// lib/queue.ts). Entries live in %AppData%\NoisLogTray\queue.json. A missing or
// malformed file yields an empty queue (fail safe) so the 18:00 runner never
// throws just because nothing is queued.
internal static class TicketQueue
{
    private static readonly Regex TicketPattern = new(@"^MDP-\d+$", RegexOptions.Compiled);
    private static readonly Regex DatePattern = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);
    private static readonly object Gate = new();

    internal static IReadOnlyList<QueueEntry> Read(string? path = null)
    {
        var queuePath = path ?? AppPaths.QueuePath;
        try
        {
            string json;
            lock (Gate) json = File.ReadAllText(queuePath);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                return Array.Empty<QueueEntry>();

            var result = new List<QueueEntry>();
            foreach (var raw in entries.EnumerateArray())
            {
                var entry = Sanitize(raw);
                if (entry != null) result.Add(entry);
            }
            return result;
        }
        catch
        {
            return Array.Empty<QueueEntry>();
        }
    }

    // Overwrite the queue (an empty list clears it). Throws on write failure so a
    // failed save never looks like success.
    internal static void Write(IReadOnlyList<QueueEntry> entries, string? path = null)
    {
        var queuePath = path ?? AppPaths.QueuePath;
        var dir = Path.GetDirectoryName(queuePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var payload = new { entries = entries.Select(e => new { date = e.Date, tickets = e.Tickets }) };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        lock (Gate) File.WriteAllText(queuePath, json);
    }

    // Coerce one raw record into a clean QueueEntry, or null to drop it. Keeps a
    // hand-edited or partially-written file from ever reaching the runner.
    private static QueueEntry? Sanitize(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object) return null;
        if (!raw.TryGetProperty("date", out var d) || d.ValueKind != JsonValueKind.String) return null;
        var date = d.GetString()!;
        if (!DatePattern.IsMatch(date)) return null;
        if (!raw.TryGetProperty("tickets", out var t) || t.ValueKind != JsonValueKind.Array) return null;

        var clean = new List<string>();
        foreach (var item in t.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var ticket = item.GetString()!;
            if (TicketPattern.IsMatch(ticket)) clean.Add(ticket);
        }
        if (clean.Count == 0) return null;
        return new QueueEntry(date, clean);
    }
}
