using System.Globalization;
using System.Text.RegularExpressions;

namespace NoisLogTray;

// The daily knowledge scan (Windows task DailyKnowledgeScan running docs\local-config\daily-knowledge-scan.ps1) read back
// from its log: the runs, each run's outcome, and what the tray says about the newest one. The IO is KnowledgeScanService's.
internal static class KnowledgeScan
{
    internal const string TaskName = "DailyKnowledgeScan";
    internal const int TaskTerminatedResult = 0x41306; // SCHED_S_TASK_TERMINATED: Task Scheduler ended the run (time limit, or End)
    internal static readonly int[] TimeLimitChoicesHours = [1, 2, 3, 4, 6];
    // A run the task did not start (the script run by hand) counts as running while its log keeps moving.
    private static readonly TimeSpan QuietRunLimit = TimeSpan.FromMinutes(20);
    // A task start logs its first line only after the Jira query, which Invoke-RestMethod gives up on after 100s.
    private static readonly TimeSpan TaskStartWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TaskStartTolerance = TimeSpan.FromSeconds(5);
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";
    private static readonly Regex TimestampedLine = new(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) (.*)$");
    private static readonly Regex FoundLine = new(@"^Found (\d+) ticket");
    private static readonly Regex MiningLine = new(@"^Mining \S+ \.\.\.$");
    private const string NoTicketsPrefix = "No tickets reached Done";
    private const string MissingTokenPrefix = "ERROR: JIRA_API_TOKEN not found";
    private const string JiraQueryFailedPrefix = "ERROR: Jira query failed";
    private const string RefreshingMapPrefix = "Refreshing map ";
    private const string CompleteMessage = "Daily knowledge scan complete.";
    // KnowledgeMiner's lines (the cleanup dialog's Mine knowledge) belong to no run; a success adds one indented result line.
    private const string MinerSuffix = "(NoisLogTray)";
    private const string MinerFailedMarker = " from NoisLogTray FAILED";
    private static readonly string[] ProblemPrefixes = ["FAILED (exit", "Giving up on ", "ERROR:"];

    // Oldest first. A message indented after its timestamp, or a line with no timestamp, continues the entry above it;
    // lines before the first run start (a tail cut mid-run) are dropped.
    internal static List<KnowledgeScanRun> ParseRuns(string logText, int firstLineNumber = 1)
    {
        var runs = new List<KnowledgeScanRun>();
        var runLines = new List<string>();
        var runFirstLineNumber = 0;
        var previousMessage = "";
        var isMinerResultNext = false;
        var lines = logText.Split('\n');
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex].TrimEnd('\r').TrimStart('\uFEFF');
            var match = TimestampedLine.Match(line);
            var isEntryStart = match.Success && !match.Groups[2].Value.StartsWith(' ');
            if (isMinerResultNext && match.Success && !isEntryStart)
            {
                isMinerResultNext = false;
                continue;
            }

            isMinerResultNext = false;
            if (isEntryStart)
            {
                var message = match.Groups[2].Value;
                if (message.EndsWith(MinerSuffix, StringComparison.Ordinal) || message.Contains(MinerFailedMarker, StringComparison.Ordinal))
                {
                    isMinerResultNext = message.EndsWith(MinerSuffix, StringComparison.Ordinal);
                    continue;
                }

                if (IsRunStart(message, previousMessage))
                {
                    if (runLines.Count != 0)
                    {
                        runs.Add(ToRun(runLines, runFirstLineNumber));
                    }

                    runLines = new List<string>();
                    runFirstLineNumber = firstLineNumber + lineIndex;
                }

                previousMessage = message;
            }

            var isTrailingEmptyLine = line.Length == 0 && lineIndex == lines.Length - 1;
            if (runFirstLineNumber == 0 || isTrailingEmptyLine)
            {
                continue;
            }

            runLines.Add(line);
        }

        if (runLines.Count != 0)
        {
            runs.Add(ToRun(runLines, runFirstLineNumber));
        }

        return runs;
    }

    // The run the task's last start wrote; a hand run of the script after it is a run of its own.
    internal static KnowledgeScanRun? RunOfLastTaskStart(IReadOnlyList<KnowledgeScanRun> runs, DateTime? lastRunTime)
    {
        if (lastRunTime == null)
        {
            return null;
        }

        return runs.LastOrDefault(ksr => ksr.StartedAt >= lastRunTime.Value - TaskStartTolerance && ksr.StartedAt <= lastRunTime.Value + TaskStartWindow);
    }

    // Task Scheduler keeps only its last run's result, so any other run without its complete line is just NotFinished.
    internal static ScanRunOutcome OutcomeOf(KnowledgeScanRun run, IReadOnlyList<KnowledgeScanRun> runs, ScheduledTask? task, DateTime now)
    {
        var isLastTaskRun = task != null && ReferenceEquals(run, RunOfLastTaskStart(runs, task.LastRunTime));
        var isNewestRun = runs.Count != 0 && ReferenceEquals(run, runs[^1]);
        if (task != null && isLastTaskRun)
        {
            if (task.State == ScheduledTaskState.Running)
            {
                return ScanRunOutcome.Running;
            }
        }
        else if (isNewestRun && !run.IsComplete && now - run.EndedAt < QuietRunLimit)
        {
            return ScanRunOutcome.Running;
        }

        if (run.IsComplete)
        {
            return run.ProblemLines.Count != 0 ? ScanRunOutcome.FinishedWithProblems : ScanRunOutcome.Succeeded;
        }

        if (task != null && isLastTaskRun && task.LastTaskResult == TaskTerminatedResult)
        {
            return ScanRunOutcome.StoppedByTaskScheduler;
        }

        return ScanRunOutcome.NotFinished;
    }

    // The newest scan start to judge: a task start newer than every run that ended before its first log line, else the
    // newest run; null when there is neither.
    internal static (DateTime StartedAt, ScanRunOutcome Outcome)? NewestOutcome(IReadOnlyList<KnowledgeScanRun> runs, ScheduledTask? task, DateTime now)
    {
        if (task != null && task.LastRunTime != null && task.State != ScheduledTaskState.Running && task.LastTaskResult != 0
            && RunOfLastTaskStart(runs, task.LastRunTime) == null && (runs.Count == 0 || task.LastRunTime.Value > runs[^1].StartedAt))
        {
            return (task.LastRunTime.Value, ScanRunOutcome.StartFailed);
        }

        if (runs.Count == 0)
        {
            return null;
        }

        var newestRun = runs[^1];
        return (newestRun.StartedAt, OutcomeOf(newestRun, runs, task, now));
    }

    // Null while the run runs; the first look only records a baseline. A bad run is noticed once (JudgedRunAt) and badged until
    // seen (SeenRunAt), except that FinishedWithProblems gets no notice: the script shows its own toast for those.
    internal static KnowledgeScanNotice? NoticeFor(DateTime runStartedAt, ScanRunOutcome outcome, DateTime? judgedRunAt, DateTime? seenRunAt,
        bool isWatching)
    {
        if (outcome == ScanRunOutcome.Running)
        {
            return null;
        }

        if (judgedRunAt == null)
        {
            return new KnowledgeScanNotice(false, false, runStartedAt, runStartedAt);
        }

        var isBad = outcome != ScanRunOutcome.Succeeded;
        var isNew = runStartedAt > judgedRunAt.Value;
        var previousSeenRunAt = seenRunAt ?? judgedRunAt.Value;
        var isUnseen = isBad && runStartedAt > previousSeenRunAt;
        var isNoticed = isBad && outcome != ScanRunOutcome.FinishedWithProblems;
        return new KnowledgeScanNotice(
            isNoticed && isNew && !isWatching,
            isUnseen && !isWatching,
            isNew ? runStartedAt : judgedRunAt.Value,
            isUnseen && isWatching ? runStartedAt : previousSeenRunAt);
    }

    // The Jira-failed line and the "No tickets" line that always follows it open one run, not two.
    private static bool IsRunStart(string message, string previousMessage)
    {
        if (message.StartsWith(MissingTokenPrefix, StringComparison.Ordinal) || message.StartsWith(JiraQueryFailedPrefix, StringComparison.Ordinal))
        {
            return true;
        }

        var isWindowLine = FoundLine.IsMatch(message) || message.StartsWith(NoTicketsPrefix, StringComparison.Ordinal);
        return isWindowLine && !previousMessage.StartsWith(JiraQueryFailedPrefix, StringComparison.Ordinal);
    }

    private static KnowledgeScanRun ToRun(List<string> runLines, int firstLineNumber)
    {
        DateTime? startedAt = null;
        var endedAt = DateTime.MinValue;
        var isComplete = false;
        var foundTicketCount = 0;
        var minedTicketCount = 0;
        var refreshedMapCount = 0;
        var problemLines = new List<string>();
        foreach (var line in runLines)
        {
            var match = TimestampedLine.Match(line);
            if (!match.Success
                || !DateTime.TryParseExact(match.Groups[1].Value, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var loggedAt))
            {
                continue;
            }

            startedAt ??= loggedAt;
            endedAt = loggedAt;
            var message = match.Groups[2].Value;
            var foundMatch = FoundLine.Match(message);
            if (foundMatch.Success)
            {
                foundTicketCount = int.Parse(foundMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            else if (MiningLine.IsMatch(message))
            {
                minedTicketCount++;
            }
            else if (message.StartsWith(RefreshingMapPrefix, StringComparison.Ordinal))
            {
                refreshedMapCount++;
            }
            else if (message == CompleteMessage)
            {
                isComplete = true;
            }

            // Only the script's own failure lines: a mined ticket's summary may well say "failed".
            var trimmedMessage = message.TrimStart();
            if (ProblemPrefixes.Any(pp => trimmedMessage.StartsWith(pp, StringComparison.Ordinal)))
            {
                problemLines.Add(trimmedMessage);
            }
        }

        // A run's first line is its start line, so startedAt is always set here.
        return new KnowledgeScanRun(startedAt ?? endedAt, endedAt, isComplete, foundTicketCount, minedTicketCount, refreshedMapCount,
            problemLines, runLines, firstLineNumber);
    }
}
