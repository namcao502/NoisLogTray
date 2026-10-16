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
    public void FactCells_GivesFiveTitledCells_WithTheTableColors()
    {
        var overdue = new JiraTicket("MDP-1", "S", "2020-01-01", null, 1, "QA Confirmed", StatusCategory: "done");

        var facts = TicketWorkspaceView.FactCells(overdue, TicketSection.InProgress);

        Assert.Equal(new[] { TaskTableColumn.Status, TaskTableColumn.Due, TaskTableColumn.Eta, TaskTableColumn.Priority, TaskTableColumn.Logged },
            facts.Select(fc => fc.Cell.Column));
        Assert.All(facts, fc => Assert.False(string.IsNullOrEmpty(fc.Title)));
        Assert.Equal(TaskRow.Orange, facts[0].Cell.Color);
        Assert.NotNull(facts[0].Cell.ToolTip); // Done in Jira, still In progress here
        Assert.Equal(TaskRow.EmptyCell, facts[2].Cell.Text);
        Assert.Equal(TaskRow.Red, facts[3].Cell.Color);
    }

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

    [Fact]
    public void TicketWorkspaceView_ShowsTeamsDot_WhenTeamsViewNeedsReply()
    {
        using var temp = new TempDirectory();
        using var host = new ClaudeSessionHost(() => temp.Path, fallBackToProfile: false);
        TeamsThreadView? teamsView = null;
        using var view = new TicketWorkspaceView("MDP-1", "https://example.atlassian.net", host, () => Array.Empty<TicketWorktree>(), () => null,
            () =>
            {
                teamsView = NewTeamsView();
                return teamsView;
            }, () => null);
        var teamsButton = FindButton(view, "Teams");
        Assert.NotNull(teamsButton);

        teamsButton.PerformClick();
        var root = TeamsMessageOf("1", "8:orgid:me", 0);
        teamsView!.ApplyResult(TeamsFound(root, TeamsMessageOf("2", "8:orgid:other", 1)));
        Assert.True(teamsButton.ShowDot);

        teamsView.ApplyResult(TeamsFound(root, TeamsMessageOf("2", "8:orgid:other", 1), TeamsMessageOf("3", "8:orgid:me", 2)));
        Assert.False(teamsButton.ShowDot);
    }

    [Fact]
    public void TicketWorkspacePage_OpensNewTabOnTeams_WhenAskedBeforeSetTabs()
    {
        using var temp = new TempDirectory();
        using var host = new ClaudeSessionHost(() => temp.Path, fallBackToProfile: false);
        using var page = new TicketWorkspacePage(null, "https://example.atlassian.net", () => temp.Path, _ => host, _ => NewTeamsView(), () => null);

        page.OpenOnTeams("MDP-1", "");
        page.SetTabs(new[] { "MDP-1" }, "MDP-1");

        var teamsButton = FindButton(page, "Teams");
        Assert.NotNull(teamsButton);
        Assert.True(teamsButton.Selected);
        Assert.False(host.IsRunning);
    }

    [Fact]
    public void TicketWorkspacePage_GivesNewTabTheTeamsDot_WhenItsTicketHasAttention()
    {
        using var temp = new TempDirectory();
        using var host = new ClaudeSessionHost(() => temp.Path, fallBackToProfile: false);
        using var page = new TicketWorkspacePage(null, "https://example.atlassian.net", () => temp.Path, _ => host, _ => NewTeamsView(), () => null);

        page.SetTeamsAttention(new HashSet<string> { "MDP-1" });
        page.SetTabs(new[] { "MDP-1" }, "MDP-1");

        var teamsButton = FindButton(page, "Teams");
        Assert.NotNull(teamsButton);
        Assert.True(teamsButton.ShowDot);
    }

    private static TeamsThreadView NewTeamsView()
    {
        var sent = Task.FromResult(new TeamsReplyResult(TeamsReplyState.Sent, null, null));
        return new TeamsThreadView("MDP-1",
            (_, _, _) => new TaskCompletionSource<TeamsThreadResult>().Task,
            _ => new TaskCompletionSource<TeamsThreadResult>().Task,
            (_, _, _, _, _) => Task.FromResult<(TeamsAnalysis? Analysis, string? Error)>((null, null)),
            (_, _, _) => sent,
            (_, _, _) => sent,
            (_, _, _, _, _) => sent,
            (_, _, _) => sent,
            (_, _, _) => Task.FromResult<string?>(null),
            () => ("", null),
            (_, _) => Task.FromResult<string?>(null),
            (_, _, _) => Task.FromResult<string?>(null),
            () => Task.FromResult(false),
            _ => { },
            null,
            (_, _, _, _) => { },
            (_, _, _) => Task.FromResult<string?>(null),
            (_, _) => { });
    }

    private static TeamsMessage TeamsMessageOf(string id, string senderId, int minutes)
    {
        return new TeamsMessage(id, senderId, "name", new DateTimeOffset(2026, 10, 14, 9, minutes, 0, TimeSpan.Zero), "text", true, id == "1" ? "MDP-1 x" : null);
    }

    private static TeamsThreadResult TeamsFound(params TeamsMessage[] messages)
    {
        return new TeamsThreadResult(TeamsThreadState.Found, new TeamsThread("19:c@thread.tacv2", "1", messages), "8:orgid:me",
            new[] { new TeamsChannel("19:c@thread.tacv2", "MDP", "PR in the hole") }, "19:c@thread.tacv2", null);
    }

    private static MacButton? FindButton(Control parent, string text)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is MacButton button && button.Text == text)
            {
                return button;
            }
            var found = FindButton(child, text);
            if (found != null)
            {
                return found;
            }
        }
        return null;
    }
}
