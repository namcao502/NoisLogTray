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
        lock (Gate) return ReadUnlocked(path ?? AppPaths.QueuePath);
    }

    // Overwrite the queue (an empty list clears it). Throws on write failure so a
    // failed save never looks like success.
    internal static void Write(IReadOnlyList<QueueEntry> entries, string? path = null)
    {
        lock (Gate) WriteUnlocked(entries, path ?? AppPaths.QueuePath);
    }

    // Remove already-processed entries from the CURRENT on-disk queue under the lock,
    // so tickets queued concurrently during a drain are not clobbered by a stale
    // snapshot. For each processed entry only its listed tickets are dropped from that
    // date; a date that still has tickets left is kept.
    internal static void RemoveLogged(IReadOnlyList<QueueEntry> processed, string? path = null)
    {
        if (processed.Count == 0) return;
        var queuePath = path ?? AppPaths.QueuePath;
        lock (Gate)
        {
            var result = new List<QueueEntry>();
            foreach (var entry in ReadUnlocked(queuePath))
            {
                var drop = processed.Where(p => p.Date == entry.Date).SelectMany(p => p.Tickets).ToHashSet();
                if (drop.Count == 0) { result.Add(entry); continue; }
                var keep = entry.Tickets.Where(t => !drop.Contains(t)).ToList();
                if (keep.Count != 0) result.Add(new QueueEntry(entry.Date, keep));
            }
            WriteUnlocked(result, queuePath);
        }
    }

    // Parse + sanitize the queue file. A missing or malformed file yields an empty
    // list (fail safe). Caller holds Gate.
    private static IReadOnlyList<QueueEntry> ReadUnlocked(string queuePath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(queuePath));
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

    // Serialize + write the queue. Throws on write failure. Caller holds Gate.
    private static void WriteUnlocked(IReadOnlyList<QueueEntry> entries, string queuePath)
    {
        var dir = Path.GetDirectoryName(queuePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var payload = new { entries = entries.Select(e => new { date = e.Date, tickets = e.Tickets }) };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(queuePath, json);
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
