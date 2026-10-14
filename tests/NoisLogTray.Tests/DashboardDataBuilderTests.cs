namespace NoisLogTray.Tests;

public class DashboardDataBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 10, 0, 0, TimeSpan.FromHours(7));
    private static readonly TimeOnly LogTime = new(18, 0);
    private static readonly IReadOnlyDictionary<string, JiraTicket> NoTickets = new Dictionary<string, JiraTicket>();
    private static readonly IReadOnlyDictionary<string, TrackedPr> NoPrs = new Dictionary<string, TrackedPr>();
    private static readonly IReadOnlyDictionary<string, PrCommentAlert> NoAlerts = new Dictionary<string, PrCommentAlert>();

    private static JiraTicket Ticket(string key, string? due = null, string status = "In Progress", string category = "indeterminate") =>
        new(key, "s", due, null, Status: status, StatusCategory: category);

    private static IReadOnlyDictionary<string, JiraTicket> Map(params JiraTicket[] tickets) =>
        tickets.ToDictionary(ticket => ticket.Key);

    private static DashboardData Build(
        IReadOnlyDictionary<string, JiraTicket>? pending = null,
        IReadOnlyDictionary<string, JiraTicket>? started = null,
        IReadOnlyDictionary<string, JiraTicket>? done = null,
        IReadOnlyDictionary<string, TrackedPr>? prs = null,
        IReadOnlyDictionary<string, PrCommentAlert>? alerts = null,
        IReadOnlyList<QueueEntry>? queue = null,
        DateTimeOffset? now = null) =>
        DashboardDataBuilder.Build(pending ?? NoTickets, started ?? NoTickets, done ?? NoTickets, prs ?? NoPrs,
            alerts ?? NoAlerts, queue ?? new List<QueueEntry>(), now ?? Now, LogTime);

    [Fact]
    public void Build_SortsInProgressByDue()
    {
        var data = Build(started: Map(Ticket("MDP-1"), Ticket("MDP-2", "2026-10-20"), Ticket("MDP-3", "2026-10-10")));

        Assert.Equal(new[] { "MDP-3", "MDP-2", "MDP-1" }, data.InProgress.Select(row => row.Ticket.Key));
    }

    [Fact]
    public void Build_ReadyToTestMatchesTasksRule()
    {
        var prs = new Dictionary<string, TrackedPr>
        {
            ["MDP-1|dev|5"] = new("completed", 0),
            ["MDP-2|dev|6"] = new("completed", 0),
            ["MDP-3|dev|7"] = new("active", 0),
        };
        var data = Build(
            started: Map(Ticket("MDP-1", status: "Deployed to Dev"), Ticket("MDP-3", status: "Deployed to Dev")),
            done: Map(Ticket("MDP-2", status: "Deployed to Dev", category: "done")),
            prs: prs);

        var ready = data.TicketTodos.Where(item => item.Kind == TodoKind.ReadyToTest).ToList();
        Assert.Equal(new[] { "MDP-1", "MDP-2" }, ready.Select(item => item.Ticket!.Key));
        Assert.All(ready, item => Assert.Equal("→ Ready to Test", item.ActionLabel));
    }

    [Fact]
    public void Build_JiraDoneOnlyForStarted()
    {
        var data = Build(
            started: Map(Ticket("MDP-1", category: "done")),
            done: Map(Ticket("MDP-2", category: "done")));

        var item = Assert.Single(data.TicketTodos, todo => todo.Kind == TodoKind.JiraDone);
        Assert.Equal("MDP-1", item.Ticket!.Key);
    }

    [Fact]
    public void Build_OverdueSkipsJiraDone()
    {
        var data = Build(
            pending: Map(Ticket("MDP-1", "2026-10-01")),
            started: Map(Ticket("MDP-2", "2026-10-01"), Ticket("MDP-3", "2026-10-01", category: "done")));

        var overdue = data.TicketTodos.Where(item => item.Kind == TodoKind.Overdue).Select(item => item.Ticket!.Key);
        Assert.Equal(new[] { "MDP-2", "MDP-1" }, overdue);
    }

    [Fact]
    public void Build_PrCommentsSkipUnknownTickets()
    {
        var alerts = new Dictionary<string, PrCommentAlert>
        {
            ["MDP-1"] = new("MDP-1", 3, "http://pr/1", Now),
            ["MDP-9"] = new("MDP-9", 1, "http://pr/9", Now),
        };
        var data = Build(started: Map(Ticket("MDP-1")), alerts: alerts);

        var item = Assert.Single(data.TicketTodos, todo => todo.Kind == TodoKind.PrComments);
        Assert.Equal("MDP-1", item.Ticket!.Key);
        Assert.Equal(3, item.CommentCount);
        Assert.Equal("http://pr/1", item.PrUrl);
    }

    [Fact]
    public void Build_TicketTodos_CarryTheAttentionKinds_AndOverduesSection()
    {
        var prs = new Dictionary<string, TrackedPr> { ["MDP-1|dev|5"] = new("completed", 0) };
        var alerts = new Dictionary<string, PrCommentAlert> { ["MDP-2"] = new("MDP-2", 3, "http://pr/2", Now) };
        var data = Build(
            pending: Map(Ticket("MDP-4", "2026-10-01")),
            started: Map(Ticket("MDP-1", status: "Deployed to Dev"), Ticket("MDP-2"), Ticket("MDP-3", category: "done")),
            prs: prs, alerts: alerts);

        Assert.Equal(new[] { TodoKind.ReadyToTest, TodoKind.JiraDone, TodoKind.Overdue, TodoKind.PrComments }, data.TicketTodos.Select(ti => ti.Kind));
        Assert.Equal("→ Ready to Test", data.TicketTodos[0].ActionLabel);
        Assert.Equal(("MDP-4", TicketSection.New), (data.TicketTodos[2].Ticket!.Key, data.TicketTodos[2].Section));
        Assert.Equal((3, "http://pr/2"), (data.TicketTodos[3].CommentCount, data.TicketTodos[3].PrUrl));
    }

    [Fact]
    public void ShippedKeys_NeedEveryLiveMainPrMerged()
    {
        var prs = new Dictionary<string, TrackedPr>
        {
            ["MDP-1|main|1"] = new("completed", 0),
            ["MDP-2|main|2"] = new("completed", 0),
            ["MDP-2|main|3"] = new("active", 0, "Portal-Angular"),
            ["MDP-3|main|4"] = new("completed", 0),
            ["MDP-3|main|5"] = new("abandoned", 0, "Portal-Angular"),
            ["MDP-4|dev|6"] = new("completed", 0),
            ["MDP-5|main|7"] = new("abandoned", 0),
            ["MDP-6|main|8"] = new("completed", 0),
        };
        var done = Map(Ticket("MDP-1"), Ticket("MDP-2"), Ticket("MDP-3"), Ticket("MDP-4"), Ticket("MDP-5"));

        // MDP-6 merged too, but is not Done on the board.
        Assert.Equal(new[] { "MDP-1", "MDP-3" }, DashboardDataBuilder.ShippedKeys(done, prs));
    }

    [Fact]
    public void Build_CountsOnlyTodayQueue()
    {
        var queue = new List<QueueEntry>
        {
            new("2026-10-09", new[] { "MDP-1", "MDP-2" }),
            new("2026-10-08", new[] { "MDP-3" }),
        };
        var data = Build(queue: queue);

        Assert.Equal(3, data.QueueCount);
        Assert.Equal(2, data.TodayQueuedCount);
        Assert.Equal(new[] { "MDP-1", "MDP-2" }, data.TodayQueuedKeys.OrderBy(key => key));
        Assert.Equal(TimeSpan.FromHours(8), data.UntilLogTime);
    }

    [Fact]
    public void Build_UntilLogTimeNullAfterLogTime()
    {
        var data = Build(now: new DateTimeOffset(2026, 10, 9, 18, 30, 0, TimeSpan.FromHours(7)));

        Assert.Null(data.UntilLogTime);
    }

    [Fact]
    public void Build_GivesEachInProgressTicketItsLatestPrs()
    {
        var prs = new Dictionary<string, TrackedPr>
        {
            ["MDP-1|dev|2"] = new("active", 0),
            ["MDP-1|main|1"] = new("completed", 0),
        };
        var data = Build(started: Map(Ticket("MDP-1")), prs: prs);

        Assert.Equal(DashboardDataBuilder.LatestPrs("MDP-1", prs), data.InProgress.Single().Prs);
    }

    [Fact]
    public void LatestPrs_UsesLatestPrPerBranch()
    {
        var prs = new Dictionary<string, TrackedPr>
        {
            ["MDP-1|dev|10"] = new("active", 0),
            ["MDP-1|dev|9"] = new("abandoned", 0),
        };

        Assert.Equal(new[] { new TicketPr("Backend-DotNet", "dev", 10, "active", 0) }, DashboardDataBuilder.LatestPrs("MDP-1", prs));
    }

    [Fact]
    public void LatestPrs_SkipsLegacyKeys()
    {
        var prs = new Dictionary<string, TrackedPr> { ["MDP-1|dev"] = new("active", 0) };

        Assert.Empty(DashboardDataBuilder.LatestPrs("MDP-1", prs));
    }

    [Fact]
    public void LatestPrs_IgnoresKeyPrefixCollision()
    {
        var prs = new Dictionary<string, TrackedPr> { ["MDP-12|dev|3"] = new("active", 0) };

        Assert.Empty(DashboardDataBuilder.LatestPrs("MDP-1", prs));
    }

    [Fact]
    public void LatestPrs_KeepsNewestPerRepoAndBranch_BackendFirst()
    {
        // Inserted out of order, and "Admin-Repo" sorts before "BE", so only the BE-first rule puts BE on top.
        var prs = new Dictionary<string, TrackedPr>
        {
            ["MDP-1|dev|9"] = new("active", 4, "Portal-Angular"),
            ["MDP-1|dev|3"] = new("active", 0, "Admin-Repo"),
            ["MDP-1|dev|5"] = new("completed", 1, "Backend-DotNet"),
            ["MDP-1|dev|7"] = new("active", 2, "Backend-DotNet"),
            ["MDP-1|main|6"] = new("completed", 3),
            ["MDP-2|dev|8"] = new("active", 5, "Backend-DotNet"),
        };

        var latest = DashboardDataBuilder.LatestPrs("MDP-1", prs);

        Assert.Equal(
            new[]
            {
                new TicketPr("Backend-DotNet", "main", 6, "completed", 3),
                new TicketPr("Backend-DotNet", "dev", 7, "active", 2),
                new TicketPr("Admin-Repo", "dev", 3, "active", 0),
                new TicketPr("Portal-Angular", "dev", 9, "active", 4),
            },
            latest);
    }

    [Theory]
    [InlineData(null, "BE")]
    [InlineData("Backend-DotNet", "BE")]
    [InlineData("Portal-Angular", "FE Portal")]
    [InlineData("ExternalPortal-Angular", "FE External")]
    [InlineData("MobileApp-Angular", "FE Mobile")]
    [InlineData("Foo-Angular", "FE Foo")]
    [InlineData("Other", "Other")]
    public void RepoName_NamesTheFrontendApp(string? repository, string expected)
    {
        Assert.Equal(expected, DashboardDataBuilder.RepoName(repository));
    }
}
