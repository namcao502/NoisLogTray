namespace NoisLogTray;

// BaseCommit against the working tree (worktree on the ticket branch) or against Tip (HEAD elsewhere,
// e.g. <KEY>-dev after git-cp, whose development merge must not show up).
internal sealed record ChangeSet(string BaseCommit, string Tip, bool IsWorkingTree, IReadOnlyList<FileChange> Files);
