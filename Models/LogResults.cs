namespace NoisLogTray;

internal readonly record struct DrainResult(int Total, int Logged, int Kept);

internal readonly record struct EntryLogResult(bool TscSuccess, string? TscError, bool HrmSuccess, string? HrmError)
{
    internal bool AllSuccess => TscSuccess && HrmSuccess;
}

// Skipped = left alone because a cell held real work; only Marked is safe to record as done.
internal readonly record struct OffWriteResult(
    IReadOnlyList<DateOnly> Marked, IReadOnlyList<DateOnly> Skipped, string? Error)
{
    internal bool Success => Error is null;
}
