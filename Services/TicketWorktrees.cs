namespace NoisLogTray;

internal static class TicketWorktrees
{
    internal static string BackendPath(string devRepoPath, string ticketKey) =>
        Path.Combine(devRepoPath, ".claude", "worktrees", ticketKey);

    // The dev repo's worktree first (always the backend repo, whatever its folder name), then every sibling
    // *-Angular repo that has one. An unreadable projects folder just ends the search.
    internal static List<TicketWorktree> Find(string devRepoPath, string ticketKey)
    {
        var repo = devRepoPath.TrimEnd('\\', '/');
        var found = new List<TicketWorktree>();
        if (Directory.Exists(BackendPath(repo, ticketKey))) found.Add(new(AzureDevOpsClient.BackendRepository, BackendPath(repo, ticketKey)));
        var projectsRoot = Path.GetDirectoryName(repo);
        if (projectsRoot == null || !Directory.Exists(projectsRoot)) return found;
        try
        {
            foreach (var frontend in Directory.EnumerateDirectories(projectsRoot, "*-Angular").Order(StringComparer.OrdinalIgnoreCase))
            {
                var path = Path.Combine(frontend, ".claude", "worktrees", ticketKey);
                if (Directory.Exists(path)) found.Add(new(Path.GetFileName(frontend), path));
            }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            AppLogger.Error($"[git] Looking for {ticketKey} worktrees in {projectsRoot} failed: {e.Message}");
        }
        return found;
    }
}
