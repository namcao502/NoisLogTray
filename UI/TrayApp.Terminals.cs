namespace NoisLogTray;

// The Terminal page's tabs (name = hooks' session name, folder), each with a ClaudeSessionHost that outlives a rebuilt
// window and the conversation it resumes in its folder; all of it persists in settings.json.
internal sealed partial class TrayApp
{
    private readonly List<TerminalTabSetting> _terminalTabs = LoadTerminalTabs();
    private readonly Dictionary<string, ClaudeSessionHost> _terminalHosts = new();
    private readonly Dictionary<string, string> _claudeSessionIds = LoadClaudeSessionIds();
    private string? _selectedTerminal = AppSettings.Load().SelectedTerminal;

    // Never saved (the first run after tabs came in): one tab in the folder the single Terminal page used.
    private static List<TerminalTabSetting> LoadTerminalTabs()
    {
        var settings = AppSettings.Load();
        if (settings.TerminalTabs != null) return TerminalTabs.ValidTabs(settings.TerminalTabs);
        var tabs = new List<TerminalTabSetting> { new() { Name = TerminalTabs.FirstName, Directory = settings.ClaudeDirectory } };
        settings.TerminalTabs = tabs.ToList();
        AppSettings.Save(settings);
        return tabs;
    }

    private static Dictionary<string, string> LoadClaudeSessionIds()
    {
        var settings = AppSettings.Load();
        var sessionIds = TerminalTabs.MigrateSessionIds(settings.ClaudeSessionIds
            .DistinctBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase));
        if (!sessionIds.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(settings.ClaudeSessionIds.Keys))
        {
            settings.ClaudeSessionIds = new Dictionary<string, string>(sessionIds);
            AppSettings.Save(settings);
        }
        return sessionIds;
    }

    // A picked folder that is gone falls back to the dev repo, not to the user profile.
    private string TerminalDirectory(string name)
    {
        var directory = _terminalTabs.Find(tts => tts.Name == name)?.Directory;
        return directory != null && Directory.Exists(directory) ? directory : DevRepoPath;
    }

    // Created the first time the page shows that terminal; it lives until its tab closes or the app quits.
    private ClaudeSessionHost TerminalHostFor(string name)
    {
        if (_terminalHosts.TryGetValue(name, out var existingHost)) return existingHost;
        var host = new ClaudeSessionHost(() => TerminalDirectory(name),
            () =>
            {
                var directory = TerminalDirectory(name);
                var sessionId = _claudeSessionIds.GetValueOrDefault(TerminalTabs.SessionKey(name, directory));
                return TicketLauncher.GetResumeArgument(directory, sessionId, TicketLauncher.ClaudeProjectsRoot);
            },
            settingsFile: HookSettingsFor(name));
        TrackClaudeSession(name, host);
        _terminalHosts[name] = host;
        return host;
    }

    private IReadOnlyList<string> ClaudeFolderChoices() =>
        ClaudeFolders.MenuEntries(DevRepoPath, AppSettings.Load().RecentClaudeDirectories);

    // Also run when the page is built; a selection that is gone falls to the first tab.
    private void ShowTerminalTabs()
    {
        if (_selectedTerminal == null || !_terminalTabs.Exists(tts => tts.Name == _selectedTerminal))
            _selectedTerminal = _terminalTabs.Count != 0 ? _terminalTabs[0].Name : null;
        _claudePage?.SetTabs(_terminalTabs.Select(tts => (tts.Name, TerminalDirectory(tts.Name))).ToList(), _selectedTerminal);
    }

    // A notice's target: that terminal's tab, if it is still open.
    private void ShowTerminalTab(string name)
    {
        if (_terminalTabs.Exists(tts => tts.Name == name))
        {
            _selectedTerminal = name;
            ShowTerminalTabs();
        }
        ShowShell(ShellPage.Claude);
    }

    private void AddTerminal()
    {
        var name = TerminalTabs.NextName(_terminalTabs.Select(tts => tts.Name));
        _terminalTabs.Add(new TerminalTabSetting { Name = name });
        _selectedTerminal = name;
        SaveTerminals(AppSettings.Load());
        ShowTerminalTabs();
        AppLogger.Info($"[claude] terminal {name} opened in {TerminalDirectory(name)}");
    }

    // A running terminal (maybe Claude mid-task) is only closed after a Yes. The view is disposed (detaching from the
    // host) by SetTabs before the host stops.
    private void CloseTerminal(string name)
    {
        if (!_terminalTabs.Exists(tts => tts.Name == name)) return;
        var host = _terminalHosts.GetValueOrDefault(name);
        if (host != null && host.IsRunning)
        {
            var label = TerminalTabs.Label(TerminalDirectory(name));
            var closeTab = ConfirmDialog.Ask(_shell, Lang.T($"Close {label}?", $"Đóng {label}?"),
                Lang.T($"The terminal in {label} is still running. Close the tab and stop it?",
                    $"Terminal ở {label} vẫn đang chạy. Đóng tab và dừng nó?"),
                Lang.T("Close tab", "Đóng tab"), destructive: true);
            if (!closeTab) return;
        }
        _terminalTabs.RemoveAll(tts => tts.Name == name);
        if (_selectedTerminal == name) _selectedTerminal = null;
        ShowTerminalTabs();
        ClearClaudeWait(name); // Dispose does not raise Exited
        _terminalHosts.Remove(name);
        host?.Dispose();
        var remainingSessionIds = TerminalTabs.WithoutTab(_claudeSessionIds, name);
        _claudeSessionIds.Clear();
        foreach (var (sessionKey, sessionId) in remainingSessionIds) _claudeSessionIds[sessionKey] = sessionId;
        SaveTerminals(AppSettings.Load());
        AppLogger.Info($"[claude] terminal {name} closed");
    }

    // Runs before the page restarts the terminal, so TerminalDirectory already returns the new folder.
    private void OnTerminalFolderChosen(string name, string directory)
    {
        var tabIndex = _terminalTabs.FindIndex(tts => tts.Name == name);
        if (tabIndex == -1) return;
        var isDevRepo = string.Equals(Path.TrimEndingDirectorySeparator(directory), Path.TrimEndingDirectorySeparator(DevRepoPath),
            StringComparison.OrdinalIgnoreCase);
        _terminalTabs[tabIndex] = new TerminalTabSetting { Name = name, Directory = isDevRepo ? null : directory };
        var settings = AppSettings.Load();
        settings.RecentClaudeDirectories = ClaudeFolders.WithRecent(settings.RecentClaudeDirectories, directory);
        SaveTerminals(settings);
        ShowTerminalTabs(); // the tab's label is its folder
        AppLogger.Info($"[claude] terminal {name} folder: {directory}");
    }

    // Only stores and clears a seen dot: SetTabs raises this itself, so it must not call SetTabs.
    private void OnTerminalSelectionChanged(string? name)
    {
        _selectedTerminal = name;
        var settings = AppSettings.Load();
        if (settings.SelectedTerminal != name)
        {
            settings.SelectedTerminal = name;
            AppSettings.Save(settings);
        }
        AcknowledgeSeenWaits();
    }

    // From a terminal's hooks: /clear starts a new session id, which the next hook brings in.
    private void RememberClaudeSessionId(string name, ClaudeHookEvent hookEvent)
    {
        var directory = _terminalHosts.GetValueOrDefault(name)?.CurrentDirectory;
        var sessionId = hookEvent.SessionId;
        if (directory == null || sessionId == null || !Guid.TryParse(sessionId, out _)) return;
        if (!TicketLauncher.IsTranscriptOfFolder(hookEvent.TranscriptPath, directory)) return;
        var sessionKey = TerminalTabs.SessionKey(name, directory);
        if (_claudeSessionIds.TryGetValue(sessionKey, out var knownSessionId) && knownSessionId == sessionId) return;
        _claudeSessionIds[sessionKey] = sessionId;
        SaveTerminals(AppSettings.Load());
    }

    // Read-modify-write like the theme/window keys; AppSettings.Save is best effort (a failed write only loses persistence).
    private void SaveTerminals(AppSettings settings)
    {
        // A tab setting is never changed in place (a new folder replaces it), so the list may share them.
        settings.TerminalTabs = _terminalTabs.ToList();
        settings.SelectedTerminal = _selectedTerminal;
        settings.ClaudeSessionIds = new Dictionary<string, string>(_claudeSessionIds);
        AppSettings.Save(settings);
    }
}
