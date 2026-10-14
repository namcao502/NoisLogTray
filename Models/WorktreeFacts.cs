namespace NoisLogTray;

internal sealed record WorktreeFacts(
    int UnpushedCommits,
    int DirtyFiles,
    bool TerminalRunning,
    bool InProgress,
    bool IsBroken,
    string? OtherBranch,
    bool BranchMerged,
    bool HasTrackedPrs,
    bool AllPrsClosed,
    bool AnyPrCompleted,
    bool MainPrMerged,
    int LocalConfigDiffs,
    bool InspectFailed);
