namespace NoisLogTray;

// Replicates the /worktree skill's git mechanics (fetch + worktree add off origin/main)
// so Start can give the ticket its own worktree.
internal static class WorktreeService
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);

    internal static WorktreeCreateResult Create(
        string ticketKey,
        string repoPath,
        string worktreePath,
        IProgress<StartStage>? progress = null)
    {
        // Fetch first so origin/main is current before the new branch is cut from it.
        progress?.Report(StartStage.Fetching);
        if (!RunGit(repoPath, isFailureLogged: true, "fetch", "origin"))
        {
            return WorktreeCreateResult.Failed;
        }

        progress?.Report(StartStage.CreatingWorktree);

        // A reworked ticket gets its own branch back (git makes the local one from origin/<KEY> when only that is left).
        // Not logged: a missing branch is the normal case.
        var hasBranch = RunGit(repoPath, isFailureLogged: false, "rev-parse", "--verify", "--quiet", $"refs/heads/{ticketKey}")
            || RunGit(repoPath, isFailureLogged: false, "rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{ticketKey}");
        if (!hasBranch)
        {
            return RunGit(repoPath, isFailureLogged: true, "worktree", "add", worktreePath, "-b", ticketKey, "origin/main")
                ? WorktreeCreateResult.Created
                : WorktreeCreateResult.Failed;
        }

        if (!RunGit(repoPath, isFailureLogged: true, "worktree", "add", worktreePath, ticketKey))
        {
            return WorktreeCreateResult.Failed;
        }

        progress?.Report(StartStage.MergingMain);
        if (RunGit(worktreePath, isFailureLogged: true, "merge", "--no-edit", "origin/main"))
        {
            return WorktreeCreateResult.Created;
        }

        // Only a conflicted merge leaves MERGE_HEAD; one that never started (a local edit in the way) has nothing to abort.
        if (RunGit(worktreePath, isFailureLogged: false, "rev-parse", "-q", "--verify", "MERGE_HEAD"))
        {
            RunGit(worktreePath, isFailureLogged: true, "merge", "--abort");
        }

        return WorktreeCreateResult.MainNotMerged;
    }

    private static bool RunGit(string repoPath, bool isFailureLogged, params string[] arguments)
    {
        var result = GitCommand.Run(repoPath, GitTimeout, arguments);
        // NotRunExitCode is already logged by GitCommand; any other non-zero exit (crash codes are negative) is ours.
        if (!result.Ok && result.ExitCode != GitCommand.NotRunExitCode && isFailureLogged)
        {
            AppLogger.Error($"[git] git {string.Join(' ', arguments)}: {result.Error}");
        }

        return result.Ok;
    }
}
