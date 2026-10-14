namespace NoisLogTray;

internal sealed record WorktreeCleanupRow(
    string TicketKey,
    IReadOnlyList<TicketWorktree> Worktrees,
    CleanupVerdict Verdict,
    IReadOnlyList<string> Reasons,
    int DirtyFiles,
    bool IsMined,
    bool IsMerged,
    long SlimBytes);
