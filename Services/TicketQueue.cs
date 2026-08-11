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

                // Keep the surviving tickets, and keep each one's minutes in lockstep so
                // a partially-logged custom entry does not lose its per-ticket durations.
                var keep = new List<string>();
                var keepMinutes = entry.Minutes != null ? new List<int>() : null;
                for (var i = 0; i < entry.Tickets.Count; i++)
                {
                    if (drop.Contains(entry.Tickets[i])) continue;
                    keep.Add(entry.Tickets[i]);
                    keepMinutes?.Add(entry.Minutes![i]);
                }
                if (keep.Count != 0) result.Add(new QueueEntry(entry.Date, keep, keepMinutes));
            }
            WriteUnlocked(result, queuePath);
        }
    }

    // Merge a newly-typed set into an existing same-date entry: append the tickets that
    // are not already present, keeping Minutes in lockstep. Concrete minutes are only
    // materialized when either side is custom; otherwise Minutes stays null (even split).
    // Pure - the caller decides whether to persist the result.
    internal static QueueEntry MergeInto(QueueEntry existing, IReadOnlyList<string> newTickets,
        IReadOnlyList<int>? newMinutes)
    {
        var mergedTickets = existing.Tickets.ToList();
        var custom = existing.Minutes != null || newMinutes != null;
        var mergedMinutes = custom
            ? (existing.Minutes ?? TimeSlots.EvenSplit(existing.Tickets.Count)).ToList()
            : null;
        var newMins = newMinutes ?? TimeSlots.EvenSplit(newTickets.Count);

        for (var i = 0; i < newTickets.Count; i++)
        {
            if (mergedTickets.Contains(newTickets[i])) continue;
            mergedTickets.Add(newTickets[i]);
            mergedMinutes?.Add(newMins[i]);
        }
        return new QueueEntry(existing.Date, mergedTickets, mergedMinutes);
    }

    // Effective logged minutes for a day: a custom entry's actual sum, or a full workday
    // for the even split (which always fills the day). Lets a caller detect an over-8h day
    // uniformly with `DayMinutes(entry) > TimeSlots.TotalWorkMinutes`.
    internal static int DayMinutes(QueueEntry entry)
        => entry.Minutes != null ? entry.Minutes.Sum() : TimeSlots.TotalWorkMinutes;

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
        // Omit "minutes" entirely for the default (even-split) case so untouched entries
        // stay byte-for-byte compatible with the pre-feature schema.
        var payload = new
        {
            entries = entries.Select(e => e.Minutes != null
                ? (object)new { date = e.Date, tickets = e.Tickets, minutes = e.Minutes }
                : new { date = e.Date, tickets = e.Tickets }),
        };
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

        return new QueueEntry(date, clean, ReadMinutes(raw, clean.Count));
    }

    // Read the optional per-ticket "minutes" array. Accepted only when it is well-formed
    // and consistent (one positive int per ticket, summing to no more than a full day);
    // anything off falls back to null (the even split), never dropping the entry.
    private static IReadOnlyList<int>? ReadMinutes(JsonElement raw, int ticketCount)
    {
        if (!raw.TryGetProperty("minutes", out var m) || m.ValueKind != JsonValueKind.Array) return null;
        if (m.GetArrayLength() != ticketCount) return null;

        var minutes = new List<int>(ticketCount);
        var sum = 0;
        foreach (var item in m.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var value) || value <= 0) return null;
            minutes.Add(value);
            sum += value;
        }
        return sum <= TimeSlots.TotalWorkMinutes ? minutes : null;
    }
}
