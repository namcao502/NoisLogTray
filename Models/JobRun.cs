namespace NoisLogTray;

// One run of an AppJob. Summary is in the UI language of the moment it ran, like a notice.
internal sealed record JobRun(DateTimeOffset StartedAt, DateTimeOffset EndedAt, bool Succeeded, string Summary);
