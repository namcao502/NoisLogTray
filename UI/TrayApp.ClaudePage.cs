namespace NoisLogTray;

// The Claude page's folder (picked from its terminal bar, default the dev repo) and the conversation it resumes
// there; both persist in settings.json.
internal sealed partial class TrayApp
{
    private string? _claudeDirectory = AppSettings.Load().ClaudeDirectory;
    private readonly Dictionary<string, string> _claudeSessionIds = AppSettings.Load().ClaudeSessionIds
        .DistinctBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

    // A picked folder that is gone falls back to the dev repo, not to the user profile.
    private string ClaudeDirectory => _claudeDirectory != null && Directory.Exists(_claudeDirectory) ? _claudeDirectory : DevRepoPath;

    private ClaudeSessionHost CreateClaudeHost() => new(() => ClaudeDirectory,
        () => TicketLauncher.GetResumeArgument(ClaudeDirectory, _claudeSessionIds.GetValueOrDefault(ClaudeDirectory), TicketLauncher.ClaudeProjectsRoot),
        settingsFile: HookSettingsFor(ClaudeAttention.GeneralSessionName));

    private IReadOnlyList<string> ClaudeFolderChoices() =>
        ClaudeFolders.MenuEntries(DevRepoPath, AppSettings.Load().RecentClaudeDirectories);

    // Runs before the page restarts the session, so ClaudeDirectory already returns the new folder.
    private void OnClaudeFolderChosen(string directory)
    {
        var isDevRepo = string.Equals(Path.TrimEndingDirectorySeparator(directory), Path.TrimEndingDirectorySeparator(DevRepoPath),
            StringComparison.OrdinalIgnoreCase);
        _claudeDirectory = isDevRepo ? null : directory;
        var settings = AppSettings.Load();
        settings.ClaudeDirectory = _claudeDirectory;
        settings.RecentClaudeDirectories = ClaudeFolders.WithRecent(settings.RecentClaudeDirectories, directory);
        AppSettings.Save(settings);
        AppLogger.Info($"[claude] Claude page folder: {directory}");
    }

    // From the general session's hooks: /clear starts a new session id, which the next hook brings in.
    private void RememberClaudeSessionId(string? sessionId)
    {
        var directory = _claudeHost?.CurrentDirectory;
        if (directory == null || sessionId == null || !Guid.TryParse(sessionId, out _)) return;
        if (_claudeSessionIds.TryGetValue(directory, out var knownSessionId) && knownSessionId == sessionId) return;
        _claudeSessionIds[directory] = sessionId;
        var settings = AppSettings.Load();
        settings.ClaudeSessionIds = new Dictionary<string, string>(_claudeSessionIds);
        AppSettings.Save(settings);
    }
}
