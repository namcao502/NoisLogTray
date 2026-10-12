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
