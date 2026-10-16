namespace NoisLogTray;

// FilePath without the leading "/", null for a PR-level thread; Line (and EndLine, the last line it covers) on the new side unless IsOnOldSide.
internal sealed record PrThread(int Id, PrThreadStatus Status, string? FilePath, int? Line, bool IsOnOldSide,
    IReadOnlyList<PrComment> Comments, int? EndLine = null)
{
    public bool IsOpen => Status == PrThreadStatus.Active || Status == PrThreadStatus.Pending;
}
