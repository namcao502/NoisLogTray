using System.Text;
using System.Text.RegularExpressions;

namespace NoisLogTray;

// The cleanup dialog's git and disk work: each ticket's worktrees (BE + sibling *-Angular) and docs, read into a row,
// and the Remove and Slim docs actions.
internal static class WorktreeCleanupService
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RemoveTimeout = TimeSpan.FromMinutes(10);
    private static readonly Regex TicketKeyPattern = new(@"^[A-Z][A-Z0-9]*-[0-9]+\z", RegexOptions.Compiled);
    private const int RowReadParallelism = 6;
    private const int IsAncestorNoExitCode = 1;
    private static readonly string[] MergeTargetBranches = ["origin/main", "origin/development"];

    internal static List<WorktreeCleanupRow> LoadRows(string devRepoPath, IReadOnlyDictionary<string, TrackedPr> prTracking,
        IReadOnlySet<string> startedKeys, IReadOnlySet<string> runningKeys)
    {
        var minedKeys = MinedKeys(devRepoPath);
        var mergedKeys = MergedKeys(devRepoPath);
        // About eight short git runs per worktree; read-only, so several rows can be read at once.
        return ListKeys(devRepoPath)
            .AsParallel()
            .AsOrdered()
            .WithDegreeOfParallelism(RowReadParallelism)
            .Select(tk => LoadRow(devRepoPath, tk, prTracking, startedKeys, runningKeys, minedKeys, mergedKeys))
            .ToList();
    }

    internal static WorktreeCleanupRow LoadRow(string devRepoPath, string ticketKey, IReadOnlyDictionary<string, TrackedPr> prTracking,
        IReadOnlySet<string> startedKeys, IReadOnlySet<string> runningKeys, IReadOnlySet<string> minedKeys, IReadOnlySet<string> mergedKeys)
    {
        var worktrees = TicketWorktrees.Find(devRepoPath, ticketKey);
        var isMined = minedKeys.Contains(ticketKey);
        var isMerged = mergedKeys.Contains(ticketKey)
            || prTracking.Any(kv => kv.Key.StartsWith($"{ticketKey}|", StringComparison.Ordinal) && kv.Value.Status == "completed");
        var slimBytes = SlimBytes(Path.Combine(devRepoPath, "docs", ticketKey));
        if (worktrees.Count == 0)
        {
            return new WorktreeCleanupRow(ticketKey, worktrees, CleanupVerdict.NoWorktree, [], 0, isMined, isMerged, slimBytes);
        }

        var facts = Inspect(devRepoPath, ticketKey, prTracking, startedKeys, runningKeys.Contains(ticketKey));
        var (verdict, reasons) = WorktreeCleanup.Classify(facts);
        return new WorktreeCleanupRow(ticketKey, worktrees, verdict, reasons, facts.DirtyFiles, isMined, isMerged, slimBytes);
    }

    // Keys with a worktree in the dev repo or a sibling *-Angular repo, or a docs\MDP-* folder in the dev repo.
    internal static List<string> ListKeys(string devRepoPath)
    {
        var repo = devRepoPath.TrimEnd('\\', '/');
        var keyFolderPaths = new List<string>();
        try
        {
            var worktreeRoots = new List<string> { Path.Combine(repo, ".claude", "worktrees") };
            var projectsRoot = Path.GetDirectoryName(repo);
            if (projectsRoot != null && Directory.Exists(projectsRoot))
            {
                worktreeRoots.AddRange(Directory.EnumerateDirectories(projectsRoot, "*-Angular")
                    .Select(frp => Path.Combine(frp, ".claude", "worktrees")));
            }

            foreach (var worktreeRoot in worktreeRoots.Where(Directory.Exists))
            {
                keyFolderPaths.AddRange(Directory.EnumerateDirectories(worktreeRoot));
            }

            var docsPath = Path.Combine(repo, "docs");
            if (Directory.Exists(docsPath))
            {
                keyFolderPaths.AddRange(Directory.EnumerateDirectories(docsPath, "MDP-*"));
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            AppLogger.Error($"[cleanup] listing the ticket folders of {repo} failed: {exception.Message}");
        }

        return keyFolderPaths
            .Select(kfp => Path.GetFileName(kfp))
            .Where(tk => TicketKeyPattern.IsMatch(tk))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    // An unreadable file leaves every ticket unmined, so slimming one asks first.
    // Tickets a merge commit on origin/main or origin/development names, in the dev repo or any sibling *-Angular repo.
    // One log per repo and branch, so a whole load costs a handful of git runs, not one per ticket.
    internal static HashSet<string> MergedKeys(string devRepoPath)
    {
        var repo = devRepoPath.TrimEnd('\\', '/');
        var repoPaths = new List<string> { repo };
        var projectsRoot = Path.GetDirectoryName(repo);
        try
        {
            if (projectsRoot != null && Directory.Exists(projectsRoot))
            {
                repoPaths.AddRange(Directory.EnumerateDirectories(projectsRoot, "*-Angular")
                    .Where(frp => Directory.Exists(Path.Combine(frp, ".git"))));
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            AppLogger.Error($"[cleanup] listing the frontend repos beside {repo} failed: {exception.Message}");
        }

        var mergeSubjects = new StringBuilder();
        foreach (var repoPath in repoPaths)
        {
            foreach (var branch in MergeTargetBranches)
            {
                // A repo without a development branch just adds nothing.
                var subjects = Git(repoPath, isFailureLogged: false, "log", branch, "--first-parent", "--merges", "--format=%s");
                if (subjects != null)
                {
                    mergeSubjects.AppendLine(subjects);
                }
            }
        }

        return WorktreeCleanup.MergedTickets(mergeSubjects.ToString());
    }

    internal static HashSet<string> MinedKeys(string devRepoPath)
    {
        var localConfigPath = Path.Combine(devRepoPath, "docs", "local-config");
        var scanLogPath = KnowledgeMiner.ScanLogPath(devRepoPath);
        var retryFilePath = KnowledgeMiner.RetryFilePath(devRepoPath);
        try
        {
            var scanLog = File.Exists(scanLogPath) ? File.ReadAllText(scanLogPath) : "";
            var retryFile = File.Exists(retryFilePath) ? File.ReadAllText(retryFilePath) : "";
            return WorktreeCleanup.MinedTickets(scanLog, retryFile);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            AppLogger.Error($"[cleanup] reading the knowledge scan files in {localConfigPath} failed: {exception.Message}");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static WorktreeFacts Inspect(string devRepoPath, string ticketKey, IReadOnlyDictionary<string, TrackedPr> prTracking,
        IReadOnlySet<string> startedKeys, bool terminalRunning)
    {
        var dirtyFiles = 0;
        var unpushedCommits = 0;
        var anyMergeCommit = false;
        var allAncestors = true;
        var isBroken = false;
        var isInspectFailed = false;
        string? otherBranch = null;
        foreach (var worktree in TicketWorktrees.Find(devRepoPath, ticketKey))
        {
            // Without its admin entry, git run in the folder would read the main checkout instead.
            var isRegistered = IsRegistered(worktree);
            if (isRegistered == null)
            {
                isInspectFailed = true;
                continue;
            }

            if (isRegistered == false)
            {
                isBroken = true;
                continue;
            }

            var porcelainStatus = Git(worktree.Path, isFailureLogged: true, "status", "--porcelain");
            dirtyFiles += porcelainStatus?.Split('\n').Count(l => l.Length > 3 && !WorktreeCleanup.LocalOnlyFiles.Contains(l[3..].Trim())) ?? 0;
            var ticketBranches = new[] { ticketKey, $"{ticketKey}-dev" }
                .Where(b => Git(worktree.Path, isFailureLogged: false, "rev-parse", "--verify", "--quiet", $"refs/heads/{b}") != null)
                .ToList();
            var ticketBranchRefs = ticketBranches.Select(b => $"refs/heads/{b}").ToList();
            // Commits on no remote-tracking branch, on HEAD or either ticket branch: the only work a removal could lose.
            var unpushedCount = Git(worktree.Path, isFailureLogged: true, ["rev-list", "--count", "HEAD", .. ticketBranchRefs, "--not", "--remotes"]);
            unpushedCommits += int.TryParse(unpushedCount, out var worktreeUnpushedCommits) ? worktreeUnpushedCommits : 0;
            var currentBranch = Git(worktree.Path, isFailureLogged: true, "branch", "--show-current");
            if (!string.IsNullOrEmpty(currentBranch) && !ticketBranches.Contains(currentBranch))
            {
                otherBranch = currentBranch;
            }

            // A branch with no commits of its own is always an ancestor; only a merge commit naming the key proves it shipped.
            var mergeCommit = Git(worktree.Path, isFailureLogged: true, "log", "origin/main", "--first-parent", "--merges", "-1", "--format=%h", "-E", $"--grep={ticketKey}([^0-9]|$)");
            anyMergeCommit |= !string.IsNullOrEmpty(mergeCommit);
            var ticketTip = ticketBranches.Contains(ticketKey) ? $"refs/heads/{ticketKey}" : "HEAD";
            var ancestry = GitCommand.Run(worktree.Path, GitTimeout, "merge-base", "--is-ancestor", ticketTip, "origin/main");
            // Exit 1 means "not an ancestor"; any other failure (no origin/main) is a failed read, not an answer.
            var isAncestryUnknown = !ancestry.Ok && ancestry.ExitCode != IsAncestorNoExitCode;
            if (isAncestryUnknown && ancestry.ExitCode != GitCommand.NotRunExitCode)
            {
                AppLogger.Error($"[cleanup] git merge-base --is-ancestor {ticketTip} origin/main in {worktree.Path}: {ancestry.Error.Trim()}");
            }

            allAncestors &= ancestry.Ok;
            isInspectFailed |= porcelainStatus == null || unpushedCount == null || mergeCommit == null || isAncestryUnknown;
        }

        var ticketPrs = prTracking.Where(kv => kv.Key.StartsWith($"{ticketKey}|", StringComparison.Ordinal)).ToList();
        var mainPrs = ticketPrs.Where(kv => kv.Key.StartsWith($"{ticketKey}|main|", StringComparison.Ordinal)).ToList();
        return new WorktreeFacts(unpushedCommits, dirtyFiles, terminalRunning, startedKeys.Contains(ticketKey), isBroken, otherBranch,
            anyMergeCommit && allAncestors, ticketPrs.Count != 0, PrTracker.AreAllPrsClosed(prTracking, ticketKey),
            ticketPrs.Any(kv => kv.Value.Status == "completed"), mainPrs.Count != 0 && mainPrs.All(kv => kv.Value.Status == "completed"),
            LocalConfigDiffs(devRepoPath, ticketKey), isInspectFailed);
    }

    // Re-reads the state the dialog showed; a different dirty count means the user confirmed something else.
    internal static string? RemoveChecked(string devRepoPath, string ticketKey, IReadOnlyDictionary<string, TrackedPr> prTracking,
        IReadOnlySet<string> startedKeys, bool terminalRunning, int dirtyFilesShown)
    {
        var facts = Inspect(devRepoPath, ticketKey, prTracking, startedKeys, terminalRunning);
        var (verdict, reasons) = WorktreeCleanup.Classify(facts);
        if (verdict == CleanupVerdict.Keep)
        {
            return string.Join("; ", reasons);
        }

        if (facts.DirtyFiles > dirtyFilesShown)
        {
            return Lang.T("the state changed, reopen the dialog", "trạng thái đã đổi, mở lại dialog");
        }

        return Remove(devRepoPath, ticketKey);
    }

    // Error text for the row, or null once every worktree of the ticket is gone.
    internal static string? Remove(string devRepoPath, string ticketKey)
    {
        // The key ends up in folder paths that get deleted, so nothing but a real ticket key gets that far.
        if (!TicketKeyPattern.IsMatch(ticketKey))
        {
            return Lang.T($"not a ticket key: {ticketKey}", $"không phải mã ticket: {ticketKey}");
        }

        try
        {
            var worktrees = TicketWorktrees.Find(devRepoPath, ticketKey);
            var backend = worktrees.FirstOrDefault(tw => tw.Repository == AzureDevOpsClient.BackendRepository);
            if (backend != null)
            {
                SyncTicketDocs(Path.Combine(backend.Path, "docs"), Path.Combine(devRepoPath, "docs"));
            }

            // The backend worktree goes last: while it exists the row and the ticket's tab stay, so a failed frontend removal can be retried.
            foreach (var worktree in worktrees.OrderBy(tw => tw == backend))
            {
                var error = RemoveWorktree(worktree, ticketKey);
                if (error != null)
                {
                    return error;
                }
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            AppLogger.Error($"[cleanup] removing {ticketKey} failed: {exception.Message}");
            return exception.Message;
        }
    }

    private static string? RemoveWorktree(TicketWorktree worktree, string ticketKey)
    {
        var repoPath = Path.GetFullPath(Path.Combine(worktree.Path, "..", "..", ".."));
        var unreadableError = Lang.T(
            $"{worktree.Repository}: could not read the git worktrees (see log)",
            $"{worktree.Repository}: không đọc được danh sách worktree của git (xem log)");

        var isRegistered = IsRegistered(worktree);
        if (isRegistered == null)
        {
            return unreadableError;
        }

        // git may follow the junction and delete the real node_modules, so unlink it before any removal.
        var nodeModulesPath = Path.Combine(worktree.Path, "node_modules");
        if (Directory.Exists(nodeModulesPath) && File.GetAttributes(nodeModulesPath).HasFlag(FileAttributes.ReparsePoint))
        {
            Directory.Delete(nodeModulesPath);
        }

        var isFolderDropped = isRegistered == false;
        if (isRegistered == true)
        {
            var removal = GitCommand.Run(repoPath, RemoveTimeout, "worktree", "remove", "--force", worktree.Path);
            if (!removal.Ok)
            {
                // A removal cut short by a locked bin\obj file drops the admin entry; git can no longer remove the rest.
                var isRegisteredAfter = IsRegistered(worktree);
                if (isRegisteredAfter == null)
                {
                    return unreadableError;
                }

                if (isRegisteredAfter == true)
                {
                    return $"{worktree.Repository}: {removal.Error.Trim()}";
                }

                isFolderDropped = true;
            }
        }

        if (isFolderDropped)
        {
            GitCommand.Run(repoPath, GitTimeout, "worktree", "prune");
            if (Directory.Exists(worktree.Path))
            {
                Directory.Delete(worktree.Path, recursive: true);
            }
        }

        foreach (var branchName in new[] { ticketKey, $"{ticketKey}-dev" })
        {
            // Unmerged, missing or checked out elsewhere: kept on purpose, not an error.
            GitCommand.Run(repoPath, GitTimeout, "branch", "-d", branchName);
        }

        return null;
    }

    // One-way worktree -> main like the sync-worktree-docs hook: copies what is missing or newer, never deletes.
    private static void SyncTicketDocs(string worktreeDocsPath, string mainDocsPath)
    {
        if (!Directory.Exists(worktreeDocsPath))
        {
            return;
        }

        foreach (var ticketFolderPath in Directory.EnumerateDirectories(worktreeDocsPath, "MDP-*"))
        {
            foreach (var worktreeFilePath in Directory.EnumerateFiles(ticketFolderPath, "*", SearchOption.AllDirectories))
            {
                var mainFilePath = Path.Combine(mainDocsPath, Path.GetRelativePath(worktreeDocsPath, worktreeFilePath));
                var isCopied = !File.Exists(mainFilePath)
                    || File.GetLastWriteTimeUtc(worktreeFilePath) > File.GetLastWriteTimeUtc(mainFilePath);
                if (isCopied)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(mainFilePath)!);
                    File.Copy(worktreeFilePath, mainFilePath, overwrite: true);
                }
            }
        }
    }

    // Deletes the slim targets of docs\<KEY> in the main checkout and in every worktree folder (a hook copies docs into each).
    internal static string? SlimDocs(string devRepoPath, string ticketKey)
    {
        if (!TicketKeyPattern.IsMatch(ticketKey))
        {
            return Lang.T($"not a ticket key: {ticketKey}", $"không phải mã ticket: {ticketKey}");
        }

        if (TicketWorktrees.Find(devRepoPath, ticketKey).Count != 0)
        {
            return Lang.T("the ticket still has a worktree", "ticket vẫn còn worktree");
        }

        var ticketDocsPaths = new List<string> { Path.Combine(devRepoPath, "docs", ticketKey) };
        var worktreesPath = Path.Combine(devRepoPath, ".claude", "worktrees");
        try
        {
            if (Directory.Exists(worktreesPath))
            {
                ticketDocsPaths.AddRange(Directory.EnumerateDirectories(worktreesPath).Select(wp => Path.Combine(wp, "docs", ticketKey)));
            }

            long deletedBytes = 0;
            foreach (var ticketDocsPath in ticketDocsPaths)
            {
                deletedBytes += SlimBytes(ticketDocsPath);
                foreach (var targetPath in WorktreeCleanup.SlimTargets(ticketDocsPath))
                {
                    if (Directory.Exists(targetPath))
                    {
                        Directory.Delete(targetPath, recursive: true);
                    }
                    else
                    {
                        File.Delete(targetPath);
                    }
                }
            }

            AppLogger.Info($"[cleanup] slimmed docs of {ticketKey}: {deletedBytes} bytes");
            return null;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            AppLogger.Error($"[cleanup] slimming the docs of {ticketKey} failed: {exception.Message}");
            return exception.Message;
        }
    }

    // Null when git could not list the worktrees: only a real "not listed" may lead a removal to delete the folder.
    // git lists real paths (junctions resolved), so the path is rebuilt on the main checkout's entry, always listed first.
    private static bool? IsRegistered(TicketWorktree worktree)
    {
        var worktreePath = Path.GetFullPath(worktree.Path);
        var repoPath = Path.GetFullPath(Path.Combine(worktreePath, "..", "..", ".."));
        // -z: without it git may C-quote a path holding unusual characters, which would then never match.
        var worktreeList = Git(repoPath, isFailureLogged: true, "worktree", "list", "--porcelain", "-z");
        if (worktreeList == null)
        {
            return null;
        }

        var listedPaths = worktreeList.Split('\0')
            .Where(l => l.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(l => l["worktree ".Length..].Replace('/', '\\'))
            .ToList();
        if (listedPaths.Count == 0)
        {
            return null;
        }

        var listedWorktreePath = Path.Combine(listedPaths[0], Path.GetRelativePath(repoPath, worktreePath));
        return listedPaths.Skip(1).Contains(listedWorktreePath, StringComparer.OrdinalIgnoreCase);
    }

    // Files a removal would lose: the docs hook only syncs MDP-* folders back to the main checkout.
    private static int LocalConfigDiffs(string devRepoPath, string ticketKey)
    {
        var worktreeConfigPath = Path.Combine(TicketWorktrees.BackendPath(devRepoPath, ticketKey), "docs", "local-config");
        var mainConfigPath = Path.Combine(devRepoPath, "docs", "local-config");
        if (!Directory.Exists(worktreeConfigPath))
        {
            return 0;
        }

        var differingFiles = 0;
        try
        {
            foreach (var worktreeFilePath in Directory.EnumerateFiles(worktreeConfigPath, "*", SearchOption.AllDirectories))
            {
                // The scan's own state: copied in with the worktree, only ever updated (or deleted) in the main checkout.
                if (Path.GetFileName(worktreeFilePath).StartsWith(KnowledgeMiner.ScanFilePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var mainFilePath = Path.Combine(mainConfigPath, Path.GetRelativePath(worktreeConfigPath, worktreeFilePath));
                var isMissingInMain = !File.Exists(mainFilePath);
                var isNewerAndDifferent = !isMissingInMain
                    && File.GetLastWriteTimeUtc(worktreeFilePath) > File.GetLastWriteTimeUtc(mainFilePath)
                    && !File.ReadAllBytes(worktreeFilePath).AsSpan().SequenceEqual(File.ReadAllBytes(mainFilePath));
                if (isMissingInMain || isNewerAndDifferent)
                {
                    differingFiles++;
                }
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            // A file that cannot be compared cannot be shown safe to lose.
            AppLogger.Error($"[cleanup] comparing {worktreeConfigPath} with the main checkout failed: {exception.Message}");
            differingFiles++;
        }

        return differingFiles;
    }

    // 0 (logged) when the docs folder cannot be read.
    private static long SlimBytes(string ticketDocsPath)
    {
        try
        {
            return WorktreeCleanup.SlimTargets(ticketDocsPath)
                .Sum(stp => Directory.Exists(stp)
                    ? Directory.EnumerateFiles(stp, "*", SearchOption.AllDirectories).Sum(fp => new FileInfo(fp).Length)
                    : new FileInfo(stp).Length);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            AppLogger.Error($"[cleanup] sizing the slim targets of {ticketDocsPath} failed: {exception.Message}");
            return 0;
        }
    }

    // Read-only, like GitChanges: no index lock that could collide with Claude's git in the same worktree.
    private static string? Git(string workingDirectory, bool isFailureLogged, params string[] arguments)
    {
        var result = GitCommand.Run(workingDirectory, GitTimeout, ["--no-optional-locks", .. arguments]);
        if (result.Ok)
        {
            return result.Output.TrimEnd();
        }

        // NotRunExitCode is already logged by GitCommand; any other non-zero exit is ours.
        if (isFailureLogged && result.ExitCode != GitCommand.NotRunExitCode)
        {
            AppLogger.Error($"[cleanup] git {string.Join(' ', arguments)} in {workingDirectory}: {result.Error.Trim()}");
        }

        return null;
    }
}
