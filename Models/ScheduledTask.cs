namespace NoisLogTray;

// A Windows scheduled task as Task Scheduler reports it. Times are local and null when there is none (never run, no next
// run); ExecutionTimeLimit null = no limit; DailyStartTime is set only when the first trigger is a daily one.
internal sealed record ScheduledTask(
    ScheduledTaskState State,
    DateTime? LastRunTime,
    int LastTaskResult,
    DateTime? NextRunTime,
    TimeSpan? ExecutionTimeLimit,
    TimeOnly? DailyStartTime,
    int DailyIntervalDays);
