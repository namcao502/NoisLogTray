namespace NoisLogTray;

internal enum ScanRunOutcome
{
    Running,
    Succeeded,
    FinishedWithProblems,
    StoppedByTaskScheduler,
    NotFinished,
    StartFailed, // the task started but ended before its first log line
}
