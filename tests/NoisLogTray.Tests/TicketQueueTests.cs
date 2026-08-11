using NoisLogTray;

namespace NoisLogTray.Tests;

// Mirrors __tests__/queue.test.ts, using a temp file instead of the AppData path.
public class TicketQueueTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"noislog-queue-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { /* ignore */ }
    }

    [Fact]
    public void ReturnsEmptyWhenFileMissing()
    {
        Assert.Empty(TicketQueue.Read(_path));
    }

    [Fact]
    public void RoundTripsEntries()
    {
        var entries = new List<QueueEntry>
        {
            new("2026-07-24", new[] { "MDP-1234", "MDP-5678" }),
            new("2026-07-23", new[] { "MDP-9999" }),
        };
        TicketQueue.Write(entries, _path);

        var read = TicketQueue.Read(_path);
        Assert.Equal(2, read.Count);
        Assert.Equal("2026-07-24", read[0].Date);
        Assert.Equal(new[] { "MDP-1234", "MDP-5678" }, read[0].Tickets);
        Assert.Equal(new[] { "MDP-9999" }, read[1].Tickets);
        Assert.Contains("MDP-1234", File.ReadAllText(_path));
    }

    [Fact]
    public void ClearsWhenWrittenEmpty()
    {
        TicketQueue.Write(new List<QueueEntry> { new("2026-07-24", new[] { "MDP-1" }) }, _path);
        TicketQueue.Write(Array.Empty<QueueEntry>(), _path);
        Assert.Empty(TicketQueue.Read(_path));
    }

    [Fact]
    public void DropsMalformedEntriesAndInvalidTickets()
    {
        var json = """
        {
          "entries": [
            { "date": "2026-07-24", "tickets": ["MDP-1", "nope", 7] },
            { "date": "bad-date", "tickets": ["MDP-2"] },
            { "date": "2026-07-25", "tickets": [] },
            "garbage"
          ]
        }
        """;
        File.WriteAllText(_path, json);

        var read = TicketQueue.Read(_path);
        Assert.Single(read);
        Assert.Equal("2026-07-24", read[0].Date);
        Assert.Equal(new[] { "MDP-1" }, read[0].Tickets);
    }

    [Fact]
    public void ReturnsEmptyOnMalformedJson()
    {
        File.WriteAllText(_path, "{ not json");
        Assert.Empty(TicketQueue.Read(_path));
    }

    [Fact]
    public void RemoveLoggedDropsOnlyProcessedTickets()
    {
        TicketQueue.Write(new List<QueueEntry>
        {
            new("2026-07-24", new[] { "MDP-1", "MDP-2" }),
            new("2026-07-25", new[] { "MDP-3" }),
        }, _path);

        // Logged MDP-1 of the 24th, and all of the 25th.
        TicketQueue.RemoveLogged(new List<QueueEntry>
        {
            new("2026-07-24", new[] { "MDP-1" }),
            new("2026-07-25", new[] { "MDP-3" }),
        }, _path);

        var read = TicketQueue.Read(_path);
        Assert.Single(read);
        Assert.Equal("2026-07-24", read[0].Date);
        Assert.Equal(new[] { "MDP-2" }, read[0].Tickets); // MDP-2 kept; 25th fully removed
    }

    [Fact]
    public void RoundTripsCustomMinutes()
    {
        TicketQueue.Write(new List<QueueEntry>
        {
            new("2026-07-24", new[] { "MDP-1", "MDP-2" }, new[] { 360, 120 }),
            new("2026-07-25", new[] { "MDP-3" }), // default even split: no minutes stored
        }, _path);

        var text = File.ReadAllText(_path);
        Assert.Contains("minutes", text); // present for the custom entry...
        Assert.Contains("360", text);

        var read = TicketQueue.Read(_path);
        Assert.Equal(new[] { 360, 120 }, read[0].Minutes);
        Assert.Null(read[1].Minutes); // ...absent (null) for the default entry
    }

    [Fact]
    public void DropsMinutesThatDoNotMatchTheTickets()
    {
        // Wrong length / non-positive / over-a-day all fall back to null (even split)
        // rather than dropping the entry.
        var json = """
        {
          "entries": [
            { "date": "2026-07-24", "tickets": ["MDP-1", "MDP-2"], "minutes": [480] },
            { "date": "2026-07-25", "tickets": ["MDP-3"], "minutes": [0] },
            { "date": "2026-07-26", "tickets": ["MDP-4"], "minutes": [999] }
          ]
        }
        """;
        File.WriteAllText(_path, json);

        var read = TicketQueue.Read(_path);
        Assert.Equal(3, read.Count);
        Assert.All(read, e => Assert.Null(e.Minutes));
    }

    [Fact]
    public void RemoveLoggedSubsetsMinutesInLockstep()
    {
        TicketQueue.Write(new List<QueueEntry>
        {
            new("2026-07-24", new[] { "MDP-1", "MDP-2", "MDP-3" }, new[] { 120, 180, 180 }),
        }, _path);

        // Logged MDP-2; its minutes must be dropped alongside it, keeping 1 and 3 aligned.
        TicketQueue.RemoveLogged(new List<QueueEntry> { new("2026-07-24", new[] { "MDP-2" }) }, _path);

        var read = TicketQueue.Read(_path);
        Assert.Single(read);
        Assert.Equal(new[] { "MDP-1", "MDP-3" }, read[0].Tickets);
        Assert.Equal(new[] { 120, 180 }, read[0].Minutes);
    }

    [Fact]
    public void MergeIntoAppendsNewTicketsAndDedups()
    {
        var existing = new QueueEntry("2026-07-24", new[] { "MDP-1", "MDP-2" });
        var merged = TicketQueue.MergeInto(existing, new[] { "MDP-2", "MDP-3" }, null);

        Assert.Equal(new[] { "MDP-1", "MDP-2", "MDP-3" }, merged.Tickets); // MDP-2 not duplicated
        Assert.Null(merged.Minutes); // both sides even split -> stays null
    }

    [Fact]
    public void MergeIntoMaterializesMinutesWhenExistingIsCustom()
    {
        var existing = new QueueEntry("2026-07-24", new[] { "MDP-1", "MDP-2" }, new[] { 300, 60 });
        // New side kept the even split (null); its appended ticket gets the even-split value.
        var merged = TicketQueue.MergeInto(existing, new[] { "MDP-3" }, null);

        Assert.Equal(new[] { "MDP-1", "MDP-2", "MDP-3" }, merged.Tickets);
        Assert.Equal(new[] { 300, 60, TimeSlots.TotalWorkMinutes }, merged.Minutes); // 1 ticket even split = full day
    }

    [Fact]
    public void MergeIntoMaterializesMinutesWhenNewSideIsCustom()
    {
        var existing = new QueueEntry("2026-07-24", new[] { "MDP-1", "MDP-2" }); // even split, null
        var merged = TicketQueue.MergeInto(existing, new[] { "MDP-3" }, new[] { 120 });

        // Existing side materializes to its even split (2 tickets -> 240/240), new keeps 120.
        Assert.Equal(new[] { "MDP-1", "MDP-2", "MDP-3" }, merged.Tickets);
        Assert.Equal(new[] { 240, 240, 120 }, merged.Minutes);
    }

    [Fact]
    public void DayMinutesDetectsOverEightHours()
    {
        var evenSplit = new QueueEntry("2026-07-24", new[] { "MDP-1", "MDP-2", "MDP-3" }); // null
        Assert.Equal(TimeSlots.TotalWorkMinutes, TicketQueue.DayMinutes(evenSplit)); // always a full day

        var within = new QueueEntry("2026-07-24", new[] { "MDP-1", "MDP-2" }, new[] { 300, 180 });
        Assert.False(TicketQueue.DayMinutes(within) > TimeSlots.TotalWorkMinutes); // 480, exactly a day

        var over = TicketQueue.MergeInto(within, new[] { "MDP-3" }, new[] { 120 });
        Assert.True(TicketQueue.DayMinutes(over) > TimeSlots.TotalWorkMinutes); // 300+180+120 = 600
    }

    [Fact]
    public void RemoveLoggedKeepsEntriesQueuedDuringDrain()
    {
        // Simulates the race the fix addresses: the drain snapshotted [24th], and while
        // it ran the user queued [26th]. RemoveLogged is given only the snapshot it
        // processed, so the concurrently-added 26th must survive on disk.
        TicketQueue.Write(new List<QueueEntry>
        {
            new("2026-07-24", new[] { "MDP-1" }),
            new("2026-07-26", new[] { "MDP-9" }), // added mid-drain
        }, _path);

        TicketQueue.RemoveLogged(new List<QueueEntry> { new("2026-07-24", new[] { "MDP-1" }) }, _path);

        var read = TicketQueue.Read(_path);
        Assert.Single(read);
        Assert.Equal("2026-07-26", read[0].Date);
        Assert.Equal(new[] { "MDP-9" }, read[0].Tickets);
    }
}
