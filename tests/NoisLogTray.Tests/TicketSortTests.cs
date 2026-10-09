namespace NoisLogTray.Tests;

public class TicketSortTests
{
    [Fact]
    public void ByDue_UndatedLast()
    {
        var tickets = new[]
        {
            new JiraTicket("MDP-3", "Summary", null, null),
            new JiraTicket("MDP-2", "Summary", "2026-10-20", null),
            new JiraTicket("MDP-1", "Summary", null, null),
            new JiraTicket("MDP-4", "Summary", "2026-10-10", null),
        };

        var sorted = TicketSort.ByDue(tickets, jt => jt);

        Assert.Equal(new[] { "MDP-4", "MDP-2", "MDP-1", "MDP-3" }, sorted.Select(jt => jt.Key));
    }
}
