namespace NoisLogTray;

internal static class GitChanges
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);
    internal const int MaxChars = 1 << 20;

    // The worktree skill carries this edit into every BE worktree and git-cp never commits it.
    private static readonly string[] LocalOnlyPaths = { "src/docker-compose.yml" };

    // Null when git fails (not a repo, no origin/main, ...), logged.
    internal static ChangeSet? List(string worktreePath, string ticketKey)
    {
        var ticketRef = $"refs/heads/{ticketKey}";
        var hasTicketBranch = Git(worktreePath, "rev-parse", "--verify", "--quiet", ticketRef).Ok;
        var tip = hasTicketBranch ? ticketRef : "HEAD";
        var currentBranch = Git(worktreePath, "branch", "--show-current");
        var isWorkingTree = !hasTicketBranch || currentBranch.Output.Trim() == ticketKey;

        // A failed branch read would silently flip to committed-only, so it fails the listing like the rest.
        var mergeBase = currentBranch.Ok ? Git(worktreePath, "merge-base", tip, "origin/main") : currentBranch;
        var baseCommit = mergeBase.Output.Trim();
        var diff = !mergeBase.Ok ? mergeBase : isWorkingTree
            ? Git(worktreePath, "diff", "--name-status", "--no-renames", "-z", baseCommit, "--")
            : Git(worktreePath, "diff", "--name-status", "--no-renames", "-z", baseCommit, tip, "--");
        var untracked = !diff.Ok || !isWorkingTree ? diff
            : Git(worktreePath, "ls-files", "--others", "--exclude-standard", "-z");
        if (!untracked.Ok)
        {
            AppLogger.Error($"[git] changes in {worktreePath}: {untracked.Error.Trim()}");
            return null;
        }
        var files = ParseNameStatus(diff.Output);
        if (isWorkingTree)
            files.AddRange(untracked.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(path => new FileChange(path, FileChangeStatus.Added)));
        // A path both deleted from the index and untracked (`git rm --cached`) is one modified file.
        var merged = files
            .Where(fc => !LocalOnlyPaths.Contains(fc.Path, StringComparer.OrdinalIgnoreCase))
            .GroupBy(fc => fc.Path, StringComparer.Ordinal)
            .Select(group => group.Count() == 1 ? group.First() : new FileChange(group.Key, FileChangeStatus.Modified))
            .OrderBy(fc => fc.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ChangeSet(baseCommit, tip, isWorkingTree, merged);
    }

    // `--name-status -z` is "<status>\0<path>\0" pairs.
    internal static List<FileChange> ParseNameStatus(string output)
    {
        var parts = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var files = new List<FileChange>();
        for (var partIndex = 0; partIndex + 1 < parts.Length; partIndex += 2)
        {
            var status = parts[partIndex][0] switch
            {
                'A' => FileChangeStatus.Added,
                'D' => FileChangeStatus.Deleted,
                _ => FileChangeStatus.Modified,
            };
            files.Add(new FileChange(parts[partIndex + 1], status));
        }
        return files;
    }

    // Each side: "" when the file does not exist on it, null when it is not showable (binary or over MaxChars).
    internal static string? ReadOriginal(string worktreePath, ChangeSet set, FileChange file) =>
        file.Status == FileChangeStatus.Added ? "" : ReadBlob(worktreePath, set.BaseCommit, file.Path);

    // Throws IOException / UnauthorizedAccessException for a locked working file; the caller logs it.
    internal static string? ReadModified(string worktreePath, ChangeSet set, FileChange file)
    {
        if (file.Status == FileChangeStatus.Deleted) return "";
        if (!set.IsWorkingTree) return ReadBlob(worktreePath, set.Tip, file.Path);
        var path = Path.Combine(worktreePath, file.Path);
        if (!File.Exists(path)) return "";
        return new FileInfo(path).Length > MaxChars ? null : Displayable(File.ReadAllText(path));
    }

    // The size check first, so a large blob is never read whole.
    private static string? ReadBlob(string worktreePath, string revision, string path)
    {
        var size = Git(worktreePath, "cat-file", "-s", $"{revision}:{path}");
        if (!size.Ok || !long.TryParse(size.Output.Trim(), out var bytes) || bytes > MaxChars) return null;
        var show = Git(worktreePath, "show", $"{revision}:{path}");
        return show.Ok ? Displayable(show.Output) : null;
    }

    internal static string? Displayable(string text) => text.Length > MaxChars || text.Contains('\0') ? null : text;

    // Read-only: no index refresh/lock that could collide with Claude running git in the same worktree.
    private static GitCommand.Result Git(string worktreePath, params string[] arguments) =>
        GitCommand.Run(worktreePath, GitTimeout, ["--no-optional-locks", .. arguments]);
}
