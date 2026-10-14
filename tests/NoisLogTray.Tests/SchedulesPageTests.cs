namespace NoisLogTray.Tests;

public class SchedulesPageTests
{
    // Twelve daily runs, Oct 1 to Oct 12, each finished.
    private static readonly string TwelveRunsLog = string.Join("\n", Enumerable.Range(1, 12).SelectMany(day => new[]
    {
        $"2026-10-{day:00} 09:00:02 No tickets reached Done in the last 1440 minute(s).",
        $"2026-10-{day:00} 09:10:00 Daily knowledge scan complete.",
    }));

    [Fact]
    public void ShowData_ListsTheTenNewestRuns_AndShowsTheNewestRunsLog()
    {
        using var page = new SchedulesPage(() => "", () => Jobs());

        page.ShowData(Snapshot(ScanTask(ScheduledTaskState.Ready)), Jobs());

        Assert.Equal(10, page.RunRows().Count);
        Assert.Contains("2026-10-12 09:00:02", page.LogText());
    }

    [Fact]
    public void ShowData_KeepsTheRows_WhenNothingChanged()
    {
        using var page = new SchedulesPage(() => "", () => Jobs());
        var firstSnapshot = Snapshot(ScanTask(ScheduledTaskState.Ready));
        page.ShowData(firstSnapshot, Jobs());
        var firstRunRow = page.RunRows()[0];
        var firstJobRow = page.JobRows()[0];

        page.ShowData(Snapshot(ScanTask(ScheduledTaskState.Ready)), Jobs());

        Assert.Same(firstRunRow, page.RunRows()[0]);
        Assert.Same(firstJobRow, page.JobRows()[0]);
        // The kept row still holds the first read's run, and must still paint as selected.
        Assert.True(page.IsSelected(firstSnapshot.Runs[^1]));
    }

    [Fact]
    public void SelectRun_ShowsThatRunsLines()
    {
        using var page = new SchedulesPage(() => "", () => Jobs());
        page.ShowData(Snapshot(ScanTask(ScheduledTaskState.Ready)), Jobs());

        page.SelectRun(new DateTime(2026, 10, 5, 9, 0, 2));

        Assert.Contains("2026-10-05 09:10:00 Daily knowledge scan complete.", page.LogText());
        Assert.DoesNotContain("2026-10-12", page.LogText());
    }

    [Fact]
    public void ShowData_SelectsThePillOfTheTasksTimeLimit()
    {
        using var page = new SchedulesPage(() => "", () => Jobs());

        page.ShowData(Snapshot(ScanTask(ScheduledTaskState.Ready)), Jobs());

        Assert.True(page.TimeLimitPill(3).Selected);
        Assert.False(page.TimeLimitPill(1).Selected);
    }

    [Theory]
    [InlineData((int)ScheduledTaskState.Ready, true)]
    [InlineData((int)ScheduledTaskState.Running, false)]
    [InlineData((int)ScheduledTaskState.Disabled, false)]
    public void RunNow_IsOffWhileTheTaskRunsOrIsDisabled(int stateValue, bool expectedEnabled)
    {
        using var page = new SchedulesPage(() => "", () => Jobs());

        page.ShowData(Snapshot(ScanTask((ScheduledTaskState)stateValue)), Jobs());

        Assert.Equal(expectedEnabled, page.RunNowButton.Enabled);
    }

    [Fact]
    public void RunNowAndPills_AreOff_WhenThereIsNoTask()
    {
        using var page = new SchedulesPage(() => "", () => Jobs());

        page.ShowData(Snapshot(null), Jobs());

        Assert.False(page.RunNowButton.Enabled);
        Assert.All(KnowledgeScan.TimeLimitChoicesHours, h => Assert.False(page.TimeLimitPill(h).Enabled));
        Assert.Contains(KnowledgeScan.TaskName, page.StatusText());
    }

    [Fact]
    public void OpenLog_IsOff_WhenThereIsNoLog()
    {
        using var page = new SchedulesPage(() => "", () => Jobs());
        var logPath = @"C:\repo\docs\local-config\.daily-knowledge-scan.log";

        page.ShowData(new KnowledgeScanSnapshot(ScanTask(ScheduledTaskState.Ready), null, [], true, null, logPath), Jobs());

        Assert.False(page.OpenLogButton.Enabled);
        Assert.Contains(logPath, page.StatusText());
    }

    [Fact]
    public void ShowData_ListsEveryAppJob()
    {
        using var page = new SchedulesPage(() => "", () => Jobs());

        page.ShowData(Snapshot(ScanTask(ScheduledTaskState.Ready)), Jobs());

        Assert.Equal(3, page.JobRows().Count);
    }

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(58 * 60, "58m")]
    [InlineData(65 * 60, "1h 05m")]
    [InlineData(3 * 60 * 60, "3h")]
    public void Duration_IsShort(int seconds, string expected)
    {
        Assert.Equal(expected, SchedulesPage.Duration(TimeSpan.FromSeconds(seconds)));
    }

    private static KnowledgeScanSnapshot Snapshot(ScheduledTask? task)
    {
        return new KnowledgeScanSnapshot(task, null, KnowledgeScan.ParseRuns(TwelveRunsLog), false, null,
            @"C:\repo\docs\local-config\.daily-knowledge-scan.log");
    }

    private static ScheduledTask ScanTask(ScheduledTaskState state)
    {
        return new ScheduledTask(state, new DateTime(2026, 10, 12, 9, 0, 1), 0, new DateTime(2026, 10, 13, 9, 0, 0),
            TimeSpan.FromHours(3), new TimeOnly(9, 0), 1);
    }

    private static IReadOnlyList<AppJobStatus> Jobs()
    {
        return
        [
            new AppJobStatus(AppJob.AutoLog, "Every day at 6:00 PM", null, []),
            new AppJobStatus(AppJob.TicketCheck, "Every 5 min", null, []),
            new AppJobStatus(AppJob.CatchUp, "At startup", null, []),
        ];
    }

    [Fact]
    public void Page_RefreshesOnlyWhileShown()
    {
        using var page = new SchedulesPage(() => "", () => Jobs());

        Assert.False(page.RefreshTimerEnabled);
        page.OnPageShown();
        Assert.True(page.RefreshTimerEnabled);
        page.OnHidden();
        Assert.False(page.RefreshTimerEnabled);
    }
}
