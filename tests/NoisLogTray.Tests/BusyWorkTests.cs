namespace NoisLogTray.Tests;

public class BusyWorkTests
{
    [Fact]
    public void Begin_ListsTheWork_UntilItsHandleIsDisposed()
    {
        var busyWork = new BusyWork();

        var handle = busyWork.Begin("Checking Jira...");
        Assert.True(busyWork.IsBusy);
        Assert.Equal(new[] { "Checking Jira..." }, busyWork.Labels);

        handle.Dispose();
        Assert.False(busyWork.IsBusy);
        Assert.Empty(busyWork.Labels);
    }

    [Fact]
    public void Dispose_Twice_EndsTheWorkOnce()
    {
        var busyWork = new BusyWork();
        var changedCount = 0;
        busyWork.Changed += () => changedCount++;
        var first = busyWork.Begin("Logging the queue...");
        using var second = busyWork.Begin("Logging the queue...");

        first.Dispose();
        first.Dispose();

        // Begin, Begin, one End: the second run of the same work is still going.
        Assert.Equal(3, changedCount);
        Assert.True(busyWork.IsBusy);
    }

    [Fact]
    public void Labels_NameOverlappingRunsOfTheSameWorkOnce_OldestFirst()
    {
        var busyWork = new BusyWork();
        using var check = busyWork.Begin("Checking Jira...");
        using var week = busyWork.Begin("Reading this week...");
        using var checkAgain = busyWork.Begin("Checking Jira...");

        Assert.Equal(new[] { "Checking Jira...", "Reading this week..." }, busyWork.Labels);
    }
}
