namespace NoisLogTray;

internal readonly record struct DrainResult(int Total, int Logged, int Kept);

internal readonly record struct EntryLogResult(bool TscSuccess, string? TscError, bool HrmSuccess, string? HrmError)
{
    internal bool AllSuccess => TscSuccess && HrmSuccess;
}
