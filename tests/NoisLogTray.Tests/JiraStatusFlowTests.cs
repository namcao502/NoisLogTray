namespace NoisLogTray.Tests;

public class JiraStatusFlowTests
{
    [Fact]
    public void GetStepsToReadyToTest_ReturnsBothSteps_WhenInPrAndCodeReview()
    {
        var steps = JiraStatusFlow.GetStepsToReadyToTest("PR & Code Review");

        Assert.Equal(new[] { "Deployed to Dev", "Ready to Test" }, steps);
    }

    [Fact]
    public void GetStepsToReadyToTest_ReturnsLastStep_WhenAlreadyDeployedToDev()
    {
        var steps = JiraStatusFlow.GetStepsToReadyToTest("deployed to dev");

        Assert.Equal(new[] { "Ready to Test" }, steps);
    }

    [Theory]
    [InlineData("In Progress")]
    [InlineData("Ready To Test")]
    [InlineData("QA Confirmed")]
    [InlineData(null)]
    public void GetStepsToReadyToTest_ReturnsNoSteps_WhenNotBetweenReviewAndReadyToTest(string? status)
    {
        var steps = JiraStatusFlow.GetStepsToReadyToTest(status);

        Assert.Empty(steps);
    }

    [Fact]
    public void ReadyToTestLabel_ReturnsSteps_WhenInProgressAndDevPrMerged()
    {
        var ticket = new JiraTicket("MDP-1", "Summary", null, null, Status: "PR & Code Review");

        var label = JiraStatusFlow.ReadyToTestLabel(ticket, TicketSection.InProgress, true);

        Assert.Equal("→ Deployed to Dev → Ready to Test", label);
    }
}
