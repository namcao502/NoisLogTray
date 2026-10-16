namespace NoisLogTray.Tests;

public class PrUpdateCheckerTests
{
    private sealed class FakeAzureDevOpsClient : IAzureDevOpsClient
    {
        public Dictionary<string, List<PrStatus>> PrsByTicket { get; } = new();
        public Dictionary<string, Exception> ErrorsByTicket { get; } = new();
        public List<string> RequestedTickets { get; } = new();
        public HashSet<string> IncompleteTickets { get; } = new();

        public Task<PrLookupResult> GetTicketPrsAsync(string ticketKey)
        {
            RequestedTickets.Add(ticketKey);
            if (ErrorsByTicket.TryGetValue(ticketKey, out var error))
            {
                throw error;
            }

            var prs = PrsByTicket.GetValueOrDefault(ticketKey) ?? new List<PrStatus>();
            return Task.FromResult(new PrLookupResult(prs, !IncompleteTickets.Contains(ticketKey)));
        }

        public Task<IReadOnlyList<PrThread>> GetThreadsAsync(string repository, int pullRequestId, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<PrThread>>(new List<PrThread>());
        }
    }

    private static JiraTicket Ticket(string key, string status = "In Progress")
        => new(key, "summary", null, null, Status: status);

    private static PrStatus Pr(string branch, string status, int comments, int id = 1)
        => new(id, branch, status, comments, "https://example/pr");

    [Fact]
    public async Task CheckAsync_RecordsBaselineOnly_ForNewPr()
    {
        using var temp = new TempDirectory();
        var store = new TicketStore(temp.Path);
        store.SaveStarted(new() { ["MDP-1"] = Ticket("MDP-1") });
        var ado = new FakeAzureDevOpsClient();
        ado.PrsByTicket["MDP-1"] = new() { Pr("main", "active", 3) };

        var result = await PrUpdateChecker.CheckAsync(ado, store, baselineOnly: false);

        Assert.Empty(result.Notifications);
        Assert.Equal(new TrackedPr("active", 3, "Backend-DotNet"), store.LoadPrTracking()["MDP-1|main|1"]);
    }

    [Fact]
    public async Task CheckAsync_ReportsMergeOnce()
    {
        using var temp = new TempDirectory();
        var store = new TicketStore(temp.Path);
        store.SaveStarted(new() { ["MDP-1"] = Ticket("MDP-1") });
        store.SavePrTracking(new() { ["MDP-1|main|1"] = new TrackedPr("active", 0) });
        var ado = new FakeAzureDevOpsClient();
        ado.PrsByTicket["MDP-1"] = new() { Pr("main", "completed", 0) };

        var first = await PrUpdateChecker.CheckAsync(ado, store, baselineOnly: false);
        var second = await PrUpdateChecker.CheckAsync(ado, store, baselineOnly: false);

        Assert.Single(first.Notifications);
        Assert.True(first.Notifications[0].Notification.IsMerged);
        Assert.Empty(second.Notifications);
    }

    [Fact]
    public async Task CheckAsync_ReportsNewComments()
    {
        using var temp = new TempDirectory();
        var store = new TicketStore(temp.Path);
        store.SaveStarted(new() { ["MDP-1"] = Ticket("MDP-1") });
        store.SavePrTracking(new() { ["MDP-1|main|1"] = new TrackedPr("active", 2) });
        var ado = new FakeAzureDevOpsClient();
        ado.PrsByTicket["MDP-1"] = new() { Pr("main", "active", 5) };

        var result = await PrUpdateChecker.CheckAsync(ado, store, baselineOnly: false);

        Assert.Single(result.Notifications);
        Assert.Equal("MDP-1", result.Notifications[0].TicketKey);
        Assert.Equal(3, result.Notifications[0].Notification.NewCommentCount);
        Assert.Equal(5, store.LoadPrTracking()["MDP-1|main|1"].CommentCount);
    }

    [Fact]
    public async Task CheckAsync_FlagsDevMerge_WhenTicketInReview()
    {
        using var temp = new TempDirectory();
        var store = new TicketStore(temp.Path);
        store.SaveStarted(new()
        {
            ["MDP-1"] = Ticket("MDP-1", "PR & Code Review"),
            ["MDP-2"] = Ticket("MDP-2", "In Progress"),
        });
        store.SavePrTracking(new()
        {
            ["MDP-1|dev|1"] = new TrackedPr("active", 0),
            ["MDP-2|dev|1"] = new TrackedPr("active", 0),
        });
        var ado = new FakeAzureDevOpsClient();
        ado.PrsByTicket["MDP-1"] = new() { Pr("dev", "completed", 0) };
        ado.PrsByTicket["MDP-2"] = new() { Pr("dev", "completed", 0) };

        var result = await PrUpdateChecker.CheckAsync(ado, store, baselineOnly: false);

        Assert.Equal(new[] { "MDP-1" }, result.DevMergedNeedingReadyToTest);
        Assert.Equal(2, result.Notifications.Count);
    }

    [Fact]
    public async Task CheckAsync_SkipsDoneTicket_WhenAllPrsClosed()
    {
        using var temp = new TempDirectory();
        var store = new TicketStore(temp.Path);
        store.SaveDone(new() { ["MDP-1"] = Ticket("MDP-1", "Done"), ["MDP-2"] = Ticket("MDP-2", "Done") });
        store.SavePrTracking(new()
        {
            ["MDP-1|main|1"] = new TrackedPr("completed", 0),
            ["MDP-2|main|1"] = new TrackedPr("active", 0),
        });
        var ado = new FakeAzureDevOpsClient();

        await PrUpdateChecker.CheckAsync(ado, store, baselineOnly: false);

        Assert.Equal(new[] { "MDP-2" }, ado.RequestedTickets);
    }

    [Fact]
    public async Task CheckAsync_StopsAfterNetworkFailure()
    {
        using var temp = new TempDirectory();
        var store = new TicketStore(temp.Path);
        store.SaveStarted(new() { ["MDP-1"] = Ticket("MDP-1"), ["MDP-2"] = Ticket("MDP-2") });
        var ado = new FakeAzureDevOpsClient();
        ado.ErrorsByTicket["MDP-1"] = new HttpRequestException("offline");

        await PrUpdateChecker.CheckAsync(ado, store, baselineOnly: false);

        Assert.Equal(new[] { "MDP-1" }, ado.RequestedTickets);
    }

    [Fact]
    public async Task CheckAsync_ReportsNothing_WhenBaselineOnly()
    {
        using var temp = new TempDirectory();
        var store = new TicketStore(temp.Path);
        store.SaveStarted(new() { ["MDP-1"] = Ticket("MDP-1", "PR & Code Review") });
        store.SavePrTracking(new() { ["MDP-1|dev|1"] = new TrackedPr("active", 0) });
        var ado = new FakeAzureDevOpsClient();
        ado.PrsByTicket["MDP-1"] = new() { Pr("dev", "completed", 4) };

        var result = await PrUpdateChecker.CheckAsync(ado, store, baselineOnly: true);

        Assert.Empty(result.Notifications);
        Assert.Empty(result.DevMergedNeedingReadyToTest);
        Assert.Equal(new TrackedPr("completed", 4, "Backend-DotNet"), store.LoadPrTracking()["MDP-1|dev|1"]);
    }

    [Fact]
    public async Task CheckAsync_SkipsTicket_WhenNonNetworkError()
    {
        using var temp = new TempDirectory();
        var store = new TicketStore(temp.Path);
        store.SaveStarted(new() { ["MDP-1"] = Ticket("MDP-1"), ["MDP-2"] = Ticket("MDP-2") });
        store.SavePrTracking(new() { ["MDP-2|main|1"] = new TrackedPr("active", 0) });
        var ado = new FakeAzureDevOpsClient();
        ado.ErrorsByTicket["MDP-1"] = new InvalidOperationException("boom");
        ado.PrsByTicket["MDP-2"] = new() { Pr("main", "completed", 0) };

        var result = await PrUpdateChecker.CheckAsync(ado, store, baselineOnly: false);

        Assert.Equal(new[] { "MDP-1", "MDP-2" }, ado.RequestedTickets);
        Assert.Single(result.Notifications);
        Assert.Equal("MDP-2", result.Notifications[0].TicketKey);
    }

    [Fact]
    public async Task CheckAsync_IsIncomplete_AfterNetworkFailure()
    {
        using var temp = new TempDirectory();
        var store = new TicketStore(temp.Path);
        store.SaveStarted(new() { ["MDP-1"] = Ticket("MDP-1"), ["MDP-2"] = Ticket("MDP-2") });
        var ado = new FakeAzureDevOpsClient();
        ado.ErrorsByTicket["MDP-1"] = new HttpRequestException("offline");

        var result = await PrUpdateChecker.CheckAsync(ado, store, baselineOnly: true);

        Assert.False(result.IsComplete);
    }

    [Fact]
    public async Task CheckAsync_IsIncomplete_WhenATicketFails()
    {
        using var temp = new TempDirectory();
        var store = new TicketStore(temp.Path);
        store.SaveStarted(new() { ["MDP-1"] = Ticket("MDP-1"), ["MDP-2"] = Ticket("MDP-2") });
        var ado = new FakeAzureDevOpsClient();
        ado.ErrorsByTicket["MDP-1"] = new InvalidOperationException("boom");

        var result = await PrUpdateChecker.CheckAsync(ado, store, baselineOnly: true);

        Assert.False(result.IsComplete);
        Assert.Equal(new[] { "MDP-1", "MDP-2" }, ado.RequestedTickets);
    }

    [Fact]
    public async Task CheckAsync_ReportsHealthyBranch_AndIsIncomplete_WhenOtherBranchFailed()
    {
        using var temp = new TempDirectory();
        var store = new TicketStore(temp.Path);
        store.SaveStarted(new() { ["MDP-1"] = Ticket("MDP-1") });
        store.SavePrTracking(new() { ["MDP-1|dev|1"] = new TrackedPr("active", 0) });
        var ado = new FakeAzureDevOpsClient();
        ado.PrsByTicket["MDP-1"] = new() { Pr("dev", "completed", 0) };
        ado.IncompleteTickets.Add("MDP-1");

        var result = await PrUpdateChecker.CheckAsync(ado, store, baselineOnly: false);

        Assert.Single(result.Notifications);
        Assert.True(result.Notifications[0].Notification.IsMerged);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public async Task CheckAsync_IsComplete_WhenAllTicketsChecked()
    {
        using var temp = new TempDirectory();
        var store = new TicketStore(temp.Path);
        store.SaveStarted(new() { ["MDP-1"] = Ticket("MDP-1") });
        var ado = new FakeAzureDevOpsClient();
        ado.PrsByTicket["MDP-1"] = new() { Pr("main", "active", 1) };

        var result = await PrUpdateChecker.CheckAsync(ado, store, baselineOnly: true);

        Assert.True(result.IsComplete);
    }

    [Fact]
    public async Task CheckAsync_DoesNotFlagDevMerge_ForMainBranchMerge()
    {
        using var temp = new TempDirectory();
        var store = new TicketStore(temp.Path);
        store.SaveStarted(new() { ["MDP-1"] = Ticket("MDP-1", "PR & Code Review") });
        store.SavePrTracking(new() { ["MDP-1|main|1"] = new TrackedPr("active", 0) });
        var ado = new FakeAzureDevOpsClient();
        ado.PrsByTicket["MDP-1"] = new() { Pr("main", "completed", 0) };

        var result = await PrUpdateChecker.CheckAsync(ado, store, baselineOnly: false);

        Assert.Single(result.Notifications);
        Assert.Empty(result.DevMergedNeedingReadyToTest);
    }
}
