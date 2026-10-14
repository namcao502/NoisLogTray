namespace NoisLogTray;

// One read of the knowledge scan for the Schedules dialog and the tray's check. Task null with no TaskError = no such task;
// LogError is a read failure, not a missing log.
internal sealed record KnowledgeScanSnapshot(
    ScheduledTask? Task,
    string? TaskError,
    IReadOnlyList<KnowledgeScanRun> Runs,
    bool IsLogMissing,
    string? LogError,
    string LogPath);
