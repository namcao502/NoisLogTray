namespace NoisLogTray.Tests;

public class TicketStoreTests : IDisposable
{
    private readonly TempDirectory _stateDir = new();
    private readonly TempDirectory _sourceDir = new();

    public void Dispose()
    {
        _stateDir.Dispose();
        _sourceDir.Dispose();
    }

    [Fact]
    public void PrTracking_RoundTrips()
    {
        var tracking = new Dictionary<string, TrackedPr> { ["MDP-1|main|7"] = new("active", 2) };
        new TicketStore(_stateDir.Path).SavePrTracking(tracking);

        var loadedTracking = new TicketStore(_stateDir.Path).LoadPrTracking();

        Assert.Equal(new TrackedPr("active", 2), loadedTracking["MDP-1|main|7"]);
    }

    [Fact]
    public void PrCommentAlerts_RoundTrip()
    {
        var at = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(7));
        var alerts = new Dictionary<string, PrCommentAlert> { ["MDP-1"] = new("MDP-1", 3, "https://pr/1", at) };
        new TicketStore(_stateDir.Path).SavePrCommentAlerts(alerts);

        var loadedAlerts = new TicketStore(_stateDir.Path).LoadPrCommentAlerts();

        Assert.Equal(alerts["MDP-1"], loadedAlerts["MDP-1"]);
    }

    [Fact]
    public void PrBaseline_IsDoneAfterMark()
    {
        var store = new TicketStore(_stateDir.Path);
        Assert.False(store.IsPrBaselineDone());

        store.MarkPrBaselineDone();

        Assert.True(new TicketStore(_stateDir.Path).IsPrBaselineDone());
    }

    [Fact]
    public void LoadPending_ReturnsEmpty_WhenFileMissing()
    {
        Assert.Empty(new TicketStore(_stateDir.Path).LoadPending());
    }

    [Fact]
    public void SavePending_RoundTripsTicket_WhenReloadedByNewInstance()
    {
        var ticket = new JiraTicket("MDP-1", "Summary", "2026-09-30", "2026-09-29", 3);
        new TicketStore(_stateDir.Path).SavePending(new Dictionary<string, JiraTicket> { ["MDP-1"] = ticket });

        var loadedTicket = new TicketStore(_stateDir.Path).LoadPending()["MDP-1"];

        Assert.Equal(ticket, loadedTicket);
    }

    [Fact]
    public void LoadStarted_ReturnsEmpty_WhenFileIsCorrupt()
    {
        File.WriteAllText(Path.Combine(_stateDir.Path, ".jira-started-tickets.json"), "{not json");

        Assert.Empty(new TicketStore(_stateDir.Path).LoadStarted());
    }

    [Fact]
    public void LoadPending_BacksUpCorruptFile_AndReturnsEmpty()
    {
        var path = Path.Combine(_stateDir.Path, ".jira-pending-queue.json");
        File.WriteAllText(path, "{not json");

        Assert.Empty(new TicketStore(_stateDir.Path).LoadPending());
        Assert.Equal("{not json", File.ReadAllText(path + ".bad"));
    }

    [Fact]
    public void SaveDismissed_RoundTripsTicketAndOrigin_WhenReloaded()
    {
        var dismissedTicket = new DismissedTicket(new JiraTicket("MDP-2", "Summary", null, null), TicketSection.InProgress);
        new TicketStore(_stateDir.Path).SaveDismissed(new Dictionary<string, DismissedTicket> { ["MDP-2"] = dismissedTicket });

        var loadedTicket = new TicketStore(_stateDir.Path).LoadDismissed()["MDP-2"];

        Assert.Equal(dismissedTicket, loadedTicket);
    }

    [Fact]
    public void SaveSeen_DropsDuplicateKeys_WhenKeysRepeat()
    {
        var ticketStore = new TicketStore(_stateDir.Path);

        ticketStore.SaveSeen(new[] { "MDP-1", "MDP-2", "MDP-1" });

        Assert.Equal(new[] { "MDP-1", "MDP-2" }, ticketStore.LoadSeen());
        Assert.True(ticketStore.HasSeenFile());
    }

    [Fact]
    public void LoadSeen_ReturnsNull_WhenCorrupt()
    {
        File.WriteAllText(Path.Combine(_stateDir.Path, ".jira-assignment-seen.json"), "[not json");

        Assert.Null(new TicketStore(_stateDir.Path).LoadSeen());
    }

    [Fact]
    public void LoadSeen_ReturnsNull_WhenEmpty()
    {
        File.WriteAllText(Path.Combine(_stateDir.Path, ".jira-assignment-seen.json"), "  ");

        Assert.Null(new TicketStore(_stateDir.Path).LoadSeen());
    }

    [Fact]
    public void ImportFromTaskWatcher_CopiesStateFiles_WhenSeenFileMissing()
    {
        File.WriteAllText(Path.Combine(_sourceDir.Path, ".jira-pending-queue.json"), "{}");
        File.WriteAllText(Path.Combine(_sourceDir.Path, ".jira-assignment-seen.json"), """["MDP-1"]""");
        var ticketStore = new TicketStore(_stateDir.Path);

        var copiedCount = ticketStore.ImportFromTaskWatcher(_sourceDir.Path);

        Assert.Equal(2, copiedCount);
        Assert.True(ticketStore.HasSeenFile());
        Assert.Equal(new[] { "MDP-1" }, ticketStore.LoadSeen());
    }

    [Fact]
    public void ImportFromTaskWatcher_ReturnsZero_WhenSeenFileExists()
    {
        File.WriteAllText(Path.Combine(_sourceDir.Path, ".jira-pending-queue.json"), "{}");
        var ticketStore = new TicketStore(_stateDir.Path);
        ticketStore.SaveSeen(new List<string>());

        Assert.Equal(0, ticketStore.ImportFromTaskWatcher(_sourceDir.Path));
        Assert.False(File.Exists(Path.Combine(_stateDir.Path, ".jira-pending-queue.json")));
    }

    [Fact]
    public void ImportFromTaskWatcher_LeavesSeenAbsent_WhenACopyFails()
    {
        File.WriteAllText(Path.Combine(_sourceDir.Path, ".jira-started-tickets.json"), "{}");
        File.WriteAllText(Path.Combine(_sourceDir.Path, ".jira-assignment-seen.json"), "[]");
        var ticketStore = new TicketStore(_stateDir.Path);
        Directory.CreateDirectory(Path.Combine(_stateDir.Path, ".jira-started-tickets.json"));

        Assert.ThrowsAny<Exception>(() => ticketStore.ImportFromTaskWatcher(_sourceDir.Path));
        Assert.False(ticketStore.HasSeenFile());
    }

    [Fact]
    public void ImportFromTaskWatcher_DoesNotOverwriteExistingFile()
    {
        var startedPath = Path.Combine(_stateDir.Path, ".jira-started-tickets.json");
        File.WriteAllText(startedPath, "{}");
        File.WriteAllText(Path.Combine(_sourceDir.Path, ".jira-started-tickets.json"), """{"MDP-1":null}""");
        File.WriteAllText(Path.Combine(_sourceDir.Path, ".jira-assignment-seen.json"), "[]");
        var ticketStore = new TicketStore(_stateDir.Path);

        Assert.Equal(1, ticketStore.ImportFromTaskWatcher(_sourceDir.Path));

        Assert.Equal("{}", File.ReadAllText(startedPath));
        Assert.True(ticketStore.HasSeenFile());
    }
}
