using System.Text.RegularExpressions;

namespace NoisLogTray;

// The Terminal page's tabs: names (also their hooks' session names, so they reach a URL and a file name and must never
// look like a ticket key), labels, and the keys their resumable sessions live under.
internal static class TerminalTabs
{
    // The name the single Terminal page session had, so its hook settings file and resumable ids carry over.
    internal const string FirstName = "claude";

    // A ticket key always has a dash, so the two never collide.
    internal static readonly Regex NamePattern = new(@"^claude\d*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "claude", then the lowest free "claude2", "claude3", ...
    internal static string NextName(IEnumerable<string> usedNames)
    {
        var used = usedNames.ToHashSet(StringComparer.Ordinal);
        if (!used.Contains(FirstName)) return FirstName;
        var number = 2;
        while (used.Contains(FirstName + number)) number++;
        return FirstName + number;
    }

    internal static bool IsTerminalSession(string sessionName) => NamePattern.IsMatch(sessionName);

    // The folder's own name; a drive root has none, so it shows as the path.
    internal static string Label(string directory)
    {
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        return folderName.Length == 0 ? directory : folderName;
    }

    // Each tab resumes its own conversation per folder.
    internal static string SessionKey(string name, string directory) => $"{name}|{directory}";

    // Before tabs the ids were keyed by folder alone; those belong to the first tab. A key already in the new form wins.
    internal static Dictionary<string, string> MigrateSessionIds(IReadOnlyDictionary<string, string> sessionIds)
    {
        var migrated = sessionIds.Where(entry => entry.Key.Contains('|'))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, sessionId) in sessionIds.Where(entry => !entry.Key.Contains('|')))
            migrated.TryAdd(SessionKey(FirstName, folder), sessionId);
        return migrated;
    }

    // Tabs read from settings.json: a name that is not ours (it would reach a URL and a file name) or repeats is dropped.
    // A folder that is gone stays; the tab runs in the dev repo until it comes back.
    internal static List<TerminalTabSetting> ValidTabs(IEnumerable<TerminalTabSetting?> tabs) =>
        tabs.Where(tts => tts != null && tts.Name != null && IsTerminalSession(tts.Name))
            .Select(tts => tts!)
            .DistinctBy(tts => tts.Name, StringComparer.Ordinal)
            .ToList();

    // A closed tab's ids go, or a new tab that reuses its name would resume its conversations.
    internal static Dictionary<string, string> WithoutTab(IReadOnlyDictionary<string, string> sessionIds, string name) =>
        sessionIds.Where(entry => !entry.Key.StartsWith(name + "|", StringComparison.Ordinal))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
}
