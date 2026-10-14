namespace NoisLogTray;

// One of the tray's own jobs as the Schedules dialog shows it; NextRunAt is null when it has none (not set up, or the job is
// event-driven). Runs are oldest first.
internal sealed record AppJobStatus(AppJob Job, string ScheduleText, DateTime? NextRunAt, IReadOnlyList<JobRun> Runs);
