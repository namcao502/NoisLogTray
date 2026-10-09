namespace NoisLogTray.Tests;

public class TasksPageTests
{
    [Fact]
    public void StageText_ReturnsDistinctTextForEveryStage()
    {
        var texts = Enum.GetValues<StartStage>().Select(ss => TasksPage.StageText(ss)).ToList();

        Assert.All(texts, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.Equal(texts.Count, texts.Distinct().Count());
    }

    [Fact]
    public void ReadyToTestLabel_ReturnsTwoSteps_WhenInProgressMergedAndInCodeReview()
    {
        var label = TaskRow.ReadyToTestLabel(Ticket("PR & Code Review"), TicketSection.InProgress, true);

        Assert.Equal("→ Deployed to Dev → Ready to Test", label);
    }

    [Fact]
    public void ReadyToTestLabel_ReturnsOneStep_WhenDeployedToDev()
    {
        var label = TaskRow.ReadyToTestLabel(Ticket("Deployed to Dev"), TicketSection.InProgress, true);

        Assert.Equal("→ Ready to Test", label);
    }

    [Fact]
    public void ReadyToTestLabel_Works_WhenInDoneSection()
    {
        var label = TaskRow.ReadyToTestLabel(Ticket("Deployed to Dev"), TicketSection.Done, true);

        Assert.Equal("→ Ready to Test", label);
    }

    [Fact]
    public void ReadyToTestLabel_ReturnsNull_WhenSectionIsNew()
    {
        Assert.Null(TaskRow.ReadyToTestLabel(Ticket("PR & Code Review"), TicketSection.New, true));
    }

    [Fact]
    public void ReadyToTestLabel_ReturnsNull_WhenDevPrNotMerged()
    {
        Assert.Null(TaskRow.ReadyToTestLabel(Ticket("PR & Code Review"), TicketSection.InProgress, false));
    }

    [Fact]
    public void ReadyToTestLabel_ReturnsNull_WhenStatusHasNoSteps()
    {
        Assert.Null(TaskRow.ReadyToTestLabel(Ticket("Ready to Test"), TicketSection.InProgress, true));
    }

    [Fact]
    public void AttentionCount_CountsReadyToTestAndJiraDone()
    {
        var inProgress = new[]
        {
            new JiraTicket("MDP-1", "Summary", null, null, Status: "PR & Code Review"),
            new JiraTicket("MDP-2", "Summary", null, null, Status: "QA Confirmed", StatusCategory: "done"),
            new JiraTicket("MDP-3", "Summary", null, null, Status: "In Progress"),
        };
        var done = new[]
        {
            new JiraTicket("MDP-4", "Summary", null, null, Status: "Deployed to Dev"),
            new JiraTicket("MDP-5", "Summary", null, null, Status: "Deployed to Dev"),
        };
        var devPrMergedKeys = new HashSet<string> { "MDP-1", "MDP-4" };

        Assert.Equal(3, TasksPage.AttentionCount(inProgress, done, devPrMergedKeys));
    }

    private static JiraTicket Ticket(string status) => new("MDP-1", "Summary", null, null, Status: status);
}
