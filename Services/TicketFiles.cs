namespace NoisLogTray;

internal static class TicketFiles
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);
    internal const long MaxImageBytes = 10 * 1024 * 1024;
    private static readonly Dictionary<string, string> ImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
        [".bmp"] = "image/bmp", [".webp"] = "image/webp", [".svg"] = "image/svg+xml",
    };

    // The dev repo's copy first: the docs hook mirrors worktree edits up to it (as TeamsPost.ReadDraft).
    internal static string? DocsPath(string devRepoPath, string ticketKey)
    {
        foreach (var root in new[] { devRepoPath, TicketWorktrees.BackendPath(devRepoPath, ticketKey) })
        {
            var path = Path.Combine(root, "docs", ticketKey);
            if (Directory.Exists(path)) return path;
        }
        return null;
    }

    // Relative paths with '/', by name; null when the folder cannot be read (logged).
    internal static List<string>? ListDocs(string docsPath)
    {
        try
        {
            return Directory.EnumerateFiles(docsPath, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(docsPath, path).Replace('\\', '/'))
                .Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            AppLogger.Error($"[files] listing {docsPath} failed: {e.Message}");
            return null;
        }
    }

    // Tracked + untracked-not-ignored, minus tracked files deleted on disk; null when git fails (logged).
    // A nested untracked repo comes back as "sub/" (a folder, not a file); a conflicted path once per index stage.
    internal static List<string>? ListWorktree(string worktreePath)
    {
        var listed = Git(worktreePath, "ls-files", "-z", "--cached", "--others", "--exclude-standard");
        var deleted = listed.Ok ? Git(worktreePath, "ls-files", "-z", "--deleted") : listed;
        if (!deleted.Ok)
        {
            AppLogger.Error($"[git] files in {worktreePath}: {deleted.Error.Trim()}");
            return null;
        }
        var deletedPaths = deleted.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return listed.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(path => !path.EndsWith('/') && !deletedPaths.Contains(path)).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Throws IOException / UnauthorizedAccessException (locked or vanished file); the caller logs it.
    internal static TicketFileContent Read(string fullPath)
    {
        var extension = Path.GetExtension(fullPath);
        var length = new FileInfo(fullPath).Length;
        if (ImageMimeTypes.TryGetValue(extension, out var mimeType))
            return length > MaxImageBytes
                ? new(TicketFileKind.TooLarge, null)
                : new(TicketFileKind.Image, $"data:{mimeType};base64,{Convert.ToBase64String(File.ReadAllBytes(fullPath))}");
        if (length > GitChanges.MaxChars) return new(TicketFileKind.TooLarge, null);
        var text = GitChanges.Displayable(File.ReadAllText(fullPath));
        if (text == null) return new(TicketFileKind.Binary, null);
        var isMarkdown = extension.Equals(".md", StringComparison.OrdinalIgnoreCase) || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
        return new(isMarkdown ? TicketFileKind.Markdown : TicketFileKind.Text, text);
    }

    // Read-only, like GitChanges: no index lock that could collide with Claude's git in the same worktree.
    private static GitCommand.Result Git(string worktreePath, params string[] arguments) =>
        GitCommand.Run(worktreePath, GitTimeout, ["--no-optional-locks", .. arguments]);
}
