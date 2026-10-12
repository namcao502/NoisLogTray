namespace NoisLogTray;

// Replicates the /worktree skill's git mechanics (fetch + worktree add off origin/main)
// so Start can give the ticket its own worktree.
internal static class WorktreeService
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);

    internal static bool TryCreate(
        string ticketKey,
        string repoPath,
        string worktreePath,
        IProgress<StartStage>? progress = null)
    {
        // Fetch first so origin/main is current before the new branch is cut from it.
        progress?.Report(StartStage.Fetching);
        if (!RunGit(repoPath, isFailureLogged: true, "fetch", "origin"))
        {
            return false;
        }

        progress?.Report(StartStage.CreatingWorktree);

        // A branch left over from a removed worktree makes `-b` fail, so check it out as-is.
        // Not logged: a missing branch is the normal case.
        if (RunGit(repoPath, isFailureLogged: false, "rev-parse", "--verify", "--quiet", $"refs/heads/{ticketKey}"))
        {
            return RunGit(repoPath, isFailureLogged: true, "worktree", "add", worktreePath, ticketKey);
        }

        return RunGit(repoPath, isFailureLogged: true, "worktree", "add", worktreePath, "-b", ticketKey, "origin/main");
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
