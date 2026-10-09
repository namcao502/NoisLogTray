namespace NoisLogTray.Tests;

public class JiraTicketTests
{
    [Theory]
    [InlineData(null, "-")]
    [InlineData(0L, "-")]
    [InlineData(1800L, "30m")]
    [InlineData(3600L, "1h")]
    [InlineData(5400L, "1h 30m")]
    [InlineData(36000L, "10h")]
    public void FormatTimeSpent_ReturnsJiraStyleDuration_ForSeconds(long? seconds, string expected)
        => Assert.Equal(expected, JiraTicket.FormatTimeSpent(seconds));

    [Theory]
    [InlineData("PR & Code Review")]
    [InlineData("Deployed to Dev")]
    [InlineData("Ready to Test")]
    [InlineData("QA Failed")]
    public void IsTimeLogMissing_ReturnsTrue_WhenPastInProgressAndNothingLogged(string status)
    {
        var ticket = new JiraTicket("MDP-1", "Summary", null, null, Status: status, TimeSpentSeconds: null);
        Assert.True(ticket.IsTimeLogMissing);
    }

    [Theory]
    [InlineData("Backlog")]
    [InlineData("Selected for Development")]
    [InlineData("in progress")]
    [InlineData("Hold")]
    public void IsTimeLogMissing_ReturnsFalse_WhenWorkHasNotReachedReview(string status)
    {
        var ticket = new JiraTicket("MDP-1", "Summary", null, null, Status: status);
        Assert.False(ticket.IsTimeLogMissing);
    }

    [Fact]
    public void IsTimeLogMissing_ReturnsFalse_WhenTimeIsLogged()
    {
        var ticket = new JiraTicket("MDP-1", "Summary", null, null, Status: "PR & Code Review", TimeSpentSeconds: 600);
        Assert.False(ticket.IsTimeLogMissing);
    }

    [Fact]
    public void IsTimeLogMissing_ReturnsFalse_WhenStatusUnknown()
    {
        var ticket = new JiraTicket("MDP-1", "Summary", null, null);
        Assert.False(ticket.IsTimeLogMissing);
    }

    [Theory]
    [InlineData("indeterminate", false)]
    [InlineData("new", false)]
    [InlineData(null, false)]
    [InlineData("DONE", true)]
    public void IsStatusDone_FollowsJiraStatusCategory(string? statusCategory, bool expected)
    {
        var ticket = new JiraTicket("MDP-1", "Summary", null, null, StatusCategory: statusCategory);
        Assert.Equal(expected, ticket.IsStatusDone);
    }

    [Theory]
    [InlineData("2026-09-28", "Overdue")]
    [InlineData("2026-09-29", "Soon")]
    [InlineData("2026-10-01", "Soon")]
    [InlineData("2026-10-02", "None")]
    [InlineData(null, "None")]
    [InlineData("not-a-date", "None")]
    public void GetDueUrgency_ComparesDueDateWithToday(string? dueDate, string expected)
    {
        var ticket = new JiraTicket("MDP-1", "Summary", dueDate, null, Status: "In Progress", StatusCategory: "indeterminate");
        Assert.Equal(Enum.Parse<DueUrgency>(expected), ticket.GetDueUrgency(new DateOnly(2026, 9, 29)));
    }

    [Fact]
    public void GetDueUrgency_IsNone_WhenJiraAlreadyFinishedTheTicket()
    {
        var ticket = new JiraTicket("MDP-1", "Summary", "2026-09-01", null, Status: "Deployed to Production", StatusCategory: "done");
        Assert.Equal(DueUrgency.None, ticket.GetDueUrgency(new DateOnly(2026, 9, 29)));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("2026-10-02", true)]
    public void HasEta_ReflectsWhetherEtaIsSet(string? eta, bool expected)
    {
        var ticket = new JiraTicket("MDP-1", "Summary", null, eta);
        Assert.Equal(expected, ticket.HasEta);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(2.0, true)]
    public void HasDevPriority_ReflectsWhetherDevPriorityIsSet(double? devPriority, bool expected)
    {
        var ticket = new JiraTicket("MDP-1", "Summary", null, null, DevPriority: devPriority);
        Assert.Equal(expected, ticket.HasDevPriority);
    }
}
