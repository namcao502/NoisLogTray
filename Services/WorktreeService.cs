using System.Diagnostics;

namespace NoisLogTray;

// Replicates the /worktree skill's git mechanics (fetch + worktree add off origin/main)
// so Start can open VS Code on the ticket's worktree instead of the main repo.
internal static class WorktreeService
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StderrDrainTimeout = TimeSpan.FromSeconds(5);

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
        var commandLabel = $"git {string.Join(' ', arguments)}";
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repoPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            // Never block on a terminal credential prompt; the credential-manager GUI still works.
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                AppLogger.Error($"[git] {commandLabel}: Process.Start returned null");
                return false;
            }

            // Read stderr without awaiting so a full pipe cannot block git while we wait for exit.
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(GitTimeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception killException)
                {
                    // Throws if the process exited in the meantime; the timeout is still worth logging.
                    AppLogger.Error($"[git] {commandLabel}: kill failed: {killException.Message}");
                }

                AppLogger.Error($"[git] {commandLabel}: timed out after {GitTimeout.TotalMinutes} minutes");
                try
                {
                    stderrTask.Wait(StderrDrainTimeout);
                }
                catch (Exception drainException)
                {
                    AppLogger.Error($"[git] {commandLabel}: stderr read failed: {drainException.Message}");
                }

                return false;
            }

            if (process.ExitCode != 0)
            {
                if (isFailureLogged)
                {
                    // Bounded so a grandchild holding the pipe open cannot block forever.
                    var error = stderrTask.Wait(StderrDrainTimeout) ? stderrTask.Result : "";
                    AppLogger.Error($"[git] {commandLabel}: {error}");
                }

                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[git] {commandLabel}: {ex.Message}");
            return false;
        }
    }
}
