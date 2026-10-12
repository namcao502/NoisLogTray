namespace NoisLogTray;

// The folders the Claude page offers: the dev repo first, then the ones picked before. Windows paths compare
// case-insensitively and a trailing separator does not make a different folder.
internal static class ClaudeFolders
{
    internal const int MaxRecent = 6;

    // Folders that still exist, each once.
    internal static List<string> MenuEntries(string devRepoPath, IReadOnlyList<string> recentDirectories) =>
        new[] { devRepoPath }.Concat(recentDirectories)
            .Select(Path.TrimEndingDirectorySeparator)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // The chosen folder moves to the front; at most MaxRecent are kept.
    internal static List<string> WithRecent(IReadOnlyList<string> recentDirectories, string directory) =>
        new[] { directory }.Concat(recentDirectories)
            .Select(Path.TrimEndingDirectorySeparator)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxRecent)
            .ToList();
}
