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
