using NoisLogTray;

namespace NoisLogTray.Tests;

public class TicketWorkspaceViewTests
{
    private static readonly IReadOnlyDictionary<string, JiraTicket> NoTickets = new Dictionary<string, JiraTicket>();
    private static readonly IReadOnlyDictionary<string, DismissedTicket> NoDismissed = new Dictionary<string, DismissedTicket>();

    private static JiraTicket Ticket(string key, string summary) => new(key, summary, null, null);

    private static IReadOnlyDictionary<string, JiraTicket> Map(params JiraTicket[] tickets) =>
        tickets.ToDictionary(ticket => ticket.Key);

    [Fact]
    public void KeyRow_PutsTheFactsRightAfterAShortSummary()
    {
        Assert.Equal((300, true), TicketWorkspaceView.KeyRow(available: 900, summaryTextWidth: 300, factsWidth: 350, minSummaryWidth: 120, gap: 8));
    }

    [Fact]
    public void KeyRow_CutsALongSummary_SoTheFactsStillFit()
    {
        Assert.Equal((192, true), TicketWorkspaceView.KeyRow(available: 500, summaryTextWidth: 400, factsWidth: 300, minSummaryWidth: 120, gap: 8));
    }

    [Fact]
    public void KeyRow_MovesTheFactsToTheirOwnRow_RatherThanSqueezeTheSummary()
    {
        Assert.Equal((400, false), TicketWorkspaceView.KeyRow(available: 400, summaryTextWidth: 600, factsWidth: 350, minSummaryWidth: 120, gap: 8));
    }

    [Fact]
    public void PrGroups_OneRowPerRepo_InTheGivenOrder()
    {
        var prs = new[]
        {
            new TicketPr(AzureDevOpsClient.BackendRepository, "main", 1, "active", 0),
            new TicketPr(AzureDevOpsClient.BackendRepository, "dev", 2, "completed", 0),
            new TicketPr("ExternalPortal-Angular", "main", 3, "active", 4),
            new TicketPr("Portal-Angular", "dev", 4, "active", 0),
        };

        var groups = TicketWorkspaceView.PrGroups(prs);

        Assert.Equal(new[] { "BE", "FE External", "FE Portal" }, groups.Select(g => g.RepoName));
        Assert.Equal(new[] { 1, 2 }, groups[0].Prs.Select(tp => tp.PullRequestId));
    }

    [Fact]
    public void PrTagText_LeavesTheRepoToItsRowLabel()
    {
        var pr = new TicketPr("Portal-Angular", "main", 54991, "active", 6);

        Assert.Equal("main ● #54991 · 6 comments", TicketWorkspaceView.PrTagText(pr, vietnamese: false));
        Assert.Equal("main ● #54991 · 6 bình luận", TicketWorkspaceView.PrTagText(pr, vietnamese: true));
        Assert.Equal("dev ✓ #2", TicketWorkspaceView.PrTagText(new TicketPr("Portal-Angular", "dev", 2, "completed", 0), vietnamese: false));
    }

    [Fact]
    public void FindTicket_PrefersStartedThenDoneThenPending()
    {
        var started = Map(Ticket("MDP-1", "started"));
        var done = Map(Ticket("MDP-1", "done"), Ticket("MDP-2", "done"));
        var pending = Map(Ticket("MDP-1", "pending"), Ticket("MDP-2", "pending"), Ticket("MDP-3", "pending"));

        var inProgress = TicketWorkspaceView.FindTicket("MDP-1", started, done, pending, NoDismissed);
        var finished = TicketWorkspaceView.FindTicket("MDP-2", started, done, pending, NoDismissed);
        var waiting = TicketWorkspaceView.FindTicket("MDP-3", started, done, pending, NoDismissed);

        Assert.Equal(("started", TicketSection.InProgress), (inProgress!.Value.Ticket.Summary, inProgress.Value.Section));
        Assert.Equal(("done", TicketSection.Done), (finished!.Value.Ticket.Summary, finished.Value.Section));
        Assert.Equal(("pending", TicketSection.New), (waiting!.Value.Ticket.Summary, waiting.Value.Section));
    }

    [Fact]
    public void FindTicket_UsesDismissedOrigin()
    {
        var dismissed = new Dictionary<string, DismissedTicket>
        {
            ["MDP-4"] = new(Ticket("MDP-4", "hidden"), TicketSection.InProgress),
        };

        var found = TicketWorkspaceView.FindTicket("MDP-4", NoTickets, NoTickets, NoTickets, dismissed);

        Assert.Equal(("hidden", TicketSection.InProgress), (found!.Value.Ticket.Summary, found.Value.Section));
    }

    [Fact]
    public void FindTicket_ReturnsNull_WhenUnknown()
    {
        var started = Map(Ticket("MDP-1", "started"));

        Assert.Null(TicketWorkspaceView.FindTicket("MDP-9", started, NoTickets, NoTickets, NoDismissed));
    }
}
