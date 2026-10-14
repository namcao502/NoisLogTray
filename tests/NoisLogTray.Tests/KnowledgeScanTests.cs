namespace NoisLogTray.Tests;

public class KnowledgeScanTests
{
    private const string TwoRunsLog = """
        2026-10-13 09:00:02 Found 2 ticket(s) in the last 1440 minute(s): MDP-1, MDP-2
        2026-10-13 09:00:02 Mining MDP-1 ...
        2026-10-13 09:00:20   FAILED (exit 1): {"is_error":true}
        2026-10-13 09:00:20 Mining MDP-2 ...
        2026-10-13 09:05:54   MDP-2: added findings/x.md

        The fix failed QA once, then passed.
        2026-10-13 09:05:55 Refreshing map jit.md [Backend-DotNet] (aaa..bbb, 3 cited file(s) changed) ...
        2026-10-13 09:07:00   jit.md: updated.
        2026-10-13 09:07:01 Daily knowledge scan complete.
        2026-10-14 09:00:02 Found 1 ticket(s) in the last 1440 minute(s): MDP-3
        2026-10-14 09:00:02 Mining MDP-3 ...
        2026-10-14 09:20:00   MDP-3: nothing durable.
        """;

    // A run that stopped mid-way, then a hand run of the script that finished.
    private const string KilledThenCleanLog = """
        2026-10-13 09:00:02 Found 1 ticket(s) in the last 1440 minute(s): MDP-1
        2026-10-13 09:00:02 Mining MDP-1 ...
        2026-10-13 09:58:51   MDP-1: nothing durable.
        2026-10-13 10:05:00 No tickets reached Done in the last 65 minute(s).
        2026-10-13 10:05:01 Daily knowledge scan complete.
        """;

    private static readonly DateTime LastTaskStart = new(2026, 10, 14, 9, 0, 1);

    [Fact]
    public void ParseRuns_SplitsTheLogIntoRuns_AtEachStartLine()
    {
        var runs = KnowledgeScan.ParseRuns(TwoRunsLog);

        Assert.Equal(2, runs.Count);
        Assert.Equal(new DateTime(2026, 10, 13, 9, 0, 2), runs[0].StartedAt);
        Assert.Equal(new DateTime(2026, 10, 13, 9, 7, 1), runs[0].EndedAt);
        Assert.Equal(new DateTime(2026, 10, 14, 9, 0, 2), runs[1].StartedAt);
        Assert.Equal(1, runs[0].FirstLineNumber);
        Assert.Equal(11, runs[1].FirstLineNumber);
    }

    [Fact]
    public void ParseRuns_CountsTicketsAndMaps_AndMarksOnlyTheFinishedRunComplete()
    {
        var runs = KnowledgeScan.ParseRuns(TwoRunsLog);

        Assert.True(runs[0].IsComplete);
        Assert.Equal(2, runs[0].FoundTicketCount);
        Assert.Equal(2, runs[0].MinedTicketCount);
        Assert.Equal(1, runs[0].RefreshedMapCount);
        Assert.False(runs[1].IsComplete);
    }

    [Fact]
    public void ParseRuns_TakesProblemsOnlyFromTheScriptsOwnLines()
    {
        var runs = KnowledgeScan.ParseRuns(TwoRunsLog);

        Assert.Equal("FAILED (exit 1): {\"is_error\":true}", Assert.Single(runs[0].ProblemLines));
        Assert.Empty(runs[1].ProblemLines);
    }

    [Fact]
    public void ParseRuns_KeepsAFailedJiraQueryAndItsNoTicketsLineInOneRun()
    {
        var log = """
            2026-10-12 09:00:02 ERROR: Jira query failed: 401
            2026-10-12 09:00:02 No tickets reached Done in the last 1440 minute(s).
            2026-10-12 09:01:00 ERROR: git fetch origin main failed for Backend-DotNet; its maps not checked.
            2026-10-12 09:01:01 Daily knowledge scan complete.
            """;

        var run = Assert.Single(KnowledgeScan.ParseRuns(log));

        Assert.True(run.IsComplete);
        Assert.Equal(2, run.ProblemLines.Count);
    }

    [Fact]
    public void ParseRuns_SkipsTheTraysOwnMineEntries()
    {
        var log = """
            2026-10-13 09:00:02 No tickets reached Done in the last 1440 minute(s).
            2026-10-13 09:00:03 Daily knowledge scan complete.
            2026-10-13 15:00:00 Mining MDP-9 ... (NoisLogTray)
            2026-10-13 15:00:00   MDP-9: added findings/y.md
            2026-10-13 16:00:00 mine-knowledge MDP-8 from NoisLogTray FAILED (exit 1): boom
            """;

        var run = Assert.Single(KnowledgeScan.ParseRuns(log));

        Assert.Equal(0, run.MinedTicketCount);
        Assert.Empty(run.ProblemLines);
        Assert.Equal(2, run.Lines.Count);
    }

    [Fact]
    public void ParseRuns_KeepsTheScansOwnLines_WhenTheTraysMineLandsInTheMiddle()
    {
        var log = """
            2026-10-13 09:00:02 Found 1 ticket(s) in the last 1440 minute(s): MDP-1
            2026-10-13 09:00:02 Mining MDP-1 ...
            2026-10-13 09:01:00 Mining MDP-9 ... (NoisLogTray)
            2026-10-13 09:01:00   MDP-9: added findings/y.md
            2026-10-13 09:02:00   FAILED (exit 1): boom
            """;

        var run = Assert.Single(KnowledgeScan.ParseRuns(log));

        Assert.Equal(1, run.MinedTicketCount);
        Assert.Equal("FAILED (exit 1): boom", Assert.Single(run.ProblemLines));
    }

    [Fact]
    public void ParseRuns_NumbersLinesFromTheGivenFirstLine_AndDropsLinesBeforeTheFirstRun()
    {
        var log = "\uFEFF2026-10-13 09:30:00   tail of a run cut off by the read\n"
            + "2026-10-14 09:00:02 No tickets reached Done in the last 1440 minute(s).\n"
            + "2026-10-14 09:00:03 Daily knowledge scan complete.\n";

        var run = Assert.Single(KnowledgeScan.ParseRuns(log, firstLineNumber: 101));

        Assert.Equal(102, run.FirstLineNumber);
        Assert.Equal(2, run.Lines.Count);
    }

    [Fact]
    public void RunOfLastTaskStart_IsTheRunThatBeganAtTheTasksLastStart()
    {
        var runs = KnowledgeScan.ParseRuns(TwoRunsLog);

        Assert.Same(runs[1], KnowledgeScan.RunOfLastTaskStart(runs, LastTaskStart));
        Assert.Same(runs[0], KnowledgeScan.RunOfLastTaskStart(runs, new DateTime(2026, 10, 13, 9, 0, 1)));
        Assert.Null(KnowledgeScan.RunOfLastTaskStart(runs, new DateTime(2026, 10, 15, 9, 0, 1)));
        Assert.Null(KnowledgeScan.RunOfLastTaskStart(runs, null));
    }

    [Fact]
    public void RunOfLastTaskStart_IsNotAHandRunThatBeganLater()
    {
        var runs = KnowledgeScan.ParseRuns(TwoRunsLog);

        Assert.Null(KnowledgeScan.RunOfLastTaskStart(runs, runs[1].StartedAt.AddHours(-5)));
    }

    [Fact]
    public void OutcomeOf_IsRunning_WhileTheTaskRunsIt()
    {
        var runs = KnowledgeScan.ParseRuns(TwoRunsLog);

        var outcome = KnowledgeScan.OutcomeOf(runs[1], runs, ScanTask(ScheduledTaskState.Running, 0x41301), runs[1].EndedAt.AddHours(2));

        Assert.Equal(ScanRunOutcome.Running, outcome);
    }

    [Fact]
    public void OutcomeOf_IsStopped_WhenTaskSchedulerEndedTheTasksRun()
    {
        var runs = KnowledgeScan.ParseRuns(TwoRunsLog);

        var outcome = KnowledgeScan.OutcomeOf(runs[1], runs, ScanTask(ScheduledTaskState.Ready, KnowledgeScan.TaskTerminatedResult),
            runs[1].EndedAt.AddHours(2));

        Assert.Equal(ScanRunOutcome.StoppedByTaskScheduler, outcome);
    }

    [Fact]
    public void OutcomeOf_IsUnfinished_ForARunWithoutItsCompleteLine()
    {
        var runs = KnowledgeScan.ParseRuns(TwoRunsLog);

        Assert.Equal(ScanRunOutcome.NotFinished, KnowledgeScan.OutcomeOf(runs[1], runs, null, runs[1].EndedAt.AddHours(2)));
    }

    [Fact]
    public void OutcomeOf_IsUnfinished_ForAnOlderRunEvenRightAfterItsLastLine()
    {
        var runs = KnowledgeScan.ParseRuns(KilledThenCleanLog);

        Assert.Equal(ScanRunOutcome.NotFinished, KnowledgeScan.OutcomeOf(runs[0], runs, null, runs[0].EndedAt.AddMinutes(10)));
    }

    [Fact]
    public void OutcomeOf_IsRunning_ForAHandRunWhoseLogStillMoves()
    {
        var runs = KnowledgeScan.ParseRuns(TwoRunsLog);
        var task = ScanTask(ScheduledTaskState.Ready, KnowledgeScan.TaskTerminatedResult, runs[1].StartedAt.AddHours(-5));

        Assert.Equal(ScanRunOutcome.Running, KnowledgeScan.OutcomeOf(runs[1], runs, task, runs[1].EndedAt.AddMinutes(5)));
    }

    [Fact]
    public void OutcomeOf_TellsFinishedRunsApartByTheirProblems()
    {
        var withProblems = KnowledgeScan.ParseRuns(TwoRunsLog);
        var clean = KnowledgeScan.ParseRuns(KilledThenCleanLog);

        Assert.Equal(ScanRunOutcome.FinishedWithProblems, KnowledgeScan.OutcomeOf(withProblems[0], withProblems, null, LastTaskStart));
        Assert.Equal(ScanRunOutcome.Succeeded, KnowledgeScan.OutcomeOf(clean[1], clean, null, LastTaskStart));
    }

    [Fact]
    public void NewestOutcome_IsAFailedStart_WhenTheTasksLastStartWroteNothing()
    {
        var runs = KnowledgeScan.ParseRuns(TwoRunsLog);
        var failedStart = new DateTime(2026, 10, 15, 9, 0, 1);

        var newest = KnowledgeScan.NewestOutcome(runs, ScanTask(ScheduledTaskState.Ready, 1, failedStart), failedStart.AddHours(1));

        Assert.Equal((failedStart, ScanRunOutcome.StartFailed), newest);
    }

    [Fact]
    public void NewestOutcome_IsTheNewestRun_OtherwiseAndNullWithoutRuns()
    {
        var runs = KnowledgeScan.ParseRuns(TwoRunsLog);

        var newest = KnowledgeScan.NewestOutcome(runs, ScanTask(ScheduledTaskState.Ready, KnowledgeScan.TaskTerminatedResult), LastTaskStart.AddHours(2));

        Assert.Equal((runs[1].StartedAt, ScanRunOutcome.StoppedByTaskScheduler), newest);
        Assert.Null(KnowledgeScan.NewestOutcome([], null, LastTaskStart));
    }

    [Fact]
    public void NoticeFor_OnlyRecordsABaseline_OnTheFirstLook()
    {
        var notice = KnowledgeScan.NoticeFor(LastTaskStart, ScanRunOutcome.StoppedByTaskScheduler, null, null, false);

        Assert.Equal(new KnowledgeScanNotice(false, false, LastTaskStart, LastTaskStart), notice);
    }

    [Fact]
    public void NoticeFor_NotifiesAndBadges_ANewBadRun()
    {
        var earlier = LastTaskStart.AddDays(-1);

        var notice = KnowledgeScan.NoticeFor(LastTaskStart, ScanRunOutcome.StoppedByTaskScheduler, earlier, earlier, false);

        Assert.Equal(new KnowledgeScanNotice(true, true, LastTaskStart, earlier), notice);
    }

    [Fact]
    public void NoticeFor_OnlyBadges_ARunThatFinishedWithProblems()
    {
        var earlier = LastTaskStart.AddDays(-1);

        var notice = KnowledgeScan.NoticeFor(LastTaskStart, ScanRunOutcome.FinishedWithProblems, earlier, earlier, false);

        Assert.Equal(new KnowledgeScanNotice(false, true, LastTaskStart, earlier), notice);
    }

    [Fact]
    public void NoticeFor_KeepsTheBadgeWithoutNotifyingAgain_ForTheSameRun()
    {
        var earlier = LastTaskStart.AddDays(-1);

        var notice = KnowledgeScan.NoticeFor(LastTaskStart, ScanRunOutcome.NotFinished, LastTaskStart, earlier, false);

        Assert.Equal(new KnowledgeScanNotice(false, true, LastTaskStart, earlier), notice);
    }

    [Fact]
    public void NoticeFor_DropsTheBadge_OnceTheRunWasSeen()
    {
        var notice = KnowledgeScan.NoticeFor(LastTaskStart, ScanRunOutcome.NotFinished, LastTaskStart, LastTaskStart, false);

        Assert.Equal(new KnowledgeScanNotice(false, false, LastTaskStart, LastTaskStart), notice);
    }

    [Fact]
    public void NoticeFor_MarksTheRunSeen_WhileTheSchedulesPageIsWatched()
    {
        var earlier = LastTaskStart.AddDays(-1);

        var notice = KnowledgeScan.NoticeFor(LastTaskStart, ScanRunOutcome.StartFailed, earlier, earlier, true);

        Assert.Equal(new KnowledgeScanNotice(false, false, LastTaskStart, LastTaskStart), notice);
    }

    [Fact]
    public void NoticeFor_StaysQuiet_ForAGoodRun()
    {
        var earlier = LastTaskStart.AddDays(-1);

        var notice = KnowledgeScan.NoticeFor(LastTaskStart, ScanRunOutcome.Succeeded, earlier, earlier, false);

        Assert.Equal(new KnowledgeScanNotice(false, false, LastTaskStart, earlier), notice);
    }

    [Fact]
    public void NoticeFor_DecidesNothing_WhileTheRunRuns()
    {
        var earlier = LastTaskStart.AddDays(-1);

        Assert.Null(KnowledgeScan.NoticeFor(LastTaskStart, ScanRunOutcome.Running, earlier, earlier, false));
    }

    private static ScheduledTask ScanTask(ScheduledTaskState state, int lastTaskResult, DateTime? lastRunTime = null)
    {
        return new ScheduledTask(state, lastRunTime ?? LastTaskStart, lastTaskResult, null, TimeSpan.FromHours(3), new TimeOnly(9, 0), 1);
    }
}
