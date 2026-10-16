namespace NoisLogTray;

// The Claude hook bridge: which sessions wait for the user (a turn finished, a permission prompt, a question),
// shown as the sidebar badges and tab dots, plus a notice when the window is not already showing that session.
internal sealed partial class TrayApp
{
    private ClaudeHookServer? _hookServer; // null: no hooks, sessions run as before
    private readonly Dictionary<string, ClaudeWaitReason> _claudeWaits = new(); // session name -> why it waits for the user

    private void StartHookServer()
    {
        _hookServer = ClaudeHookServer.TryStart();
        if (_hookServer != null) _hookServer.HookReceived += (sessionName, hookEvent) => RunOnUi(() => OnClaudeHook(sessionName, hookEvent));
    }

    private Func<string?> HookSettingsFor(string sessionName) => () => _hookServer?.WriteSettingsFile(sessionName);

    // A (re)starting terminal, even one whose Start then fails, or an ended one waits for nobody. A claude that exits
    // inside a running terminal clears through its SessionEnd hook.
    private void TrackClaudeSession(string sessionName, ClaudeSessionHost host)
    {
        host.Starting += () =>
        {
            _claudeEndedInShell.Remove(sessionName);
            ClearClaudeWait(sessionName);
        };
        host.Exited += () => RunOnUi(() =>
        {
            _claudeEndedInShell.Remove(sessionName);
            ClearClaudeWait(sessionName);
        });
    }

    private void OnClaudeHook(string sessionName, ClaudeHookEvent hookEvent)
    {
        var isTerminal = TerminalTabs.IsTerminalSession(sessionName);
        var host = isTerminal ? _terminalHosts.GetValueOrDefault(sessionName) : _ticketSessions.GetValueOrDefault(sessionName);
        if (host == null || !host.IsRunning) return; // a closed tab's or an ended session's late hook
        _shipPending.Remove(sessionName); // claude runs in this tab, so its `ship` went through
        if (TeamsHandOff.EndsClaude(hookEvent)) _claudeEndedInShell.Add(sessionName);
        else _claudeEndedInShell.Remove(sessionName);
        if (isTerminal) RememberClaudeSessionId(sessionName, hookEvent);
        ClaudeWaitReason? current = _claudeWaits.TryGetValue(sessionName, out var reason) ? reason : null;
        var next = ClaudeAttention.Next(current, hookEvent);
        if (!isTerminal)
        {
            OnCommentFlowHook(sessionName, hookEvent, hookEvent.EventName == "Stop" && next == ClaudeWaitReason.Finished);
        }

        // A turn that finishes while the user looks at that session needs no dot.
        if (next != null && ClaudeAttention.ClearsWhenSeen(next.Value) && IsWatching(sessionName)) next = null;
        if (next == current) return;
        AppLogger.Info($"[hooks] {sessionName}: {current?.ToString() ?? "none"} -> {next?.ToString() ?? "none"} ({hookEvent.EventName})");
        if (next == null) _claudeWaits.Remove(sessionName);
        else _claudeWaits[sessionName] = next.Value;
        RefreshClaudeAttention();
        if (next != null && !IsWatching(sessionName))
        {
            Notify(ClaudeAttention.NoticeText(ClaudeSessionLabel(sessionName), next.Value), ToolTipIcon.Info,
                open: isTerminal ? () => ShowTerminalTab(sessionName) : () => ShowTicketTab(sessionName));
        }
    }

    private void ClearClaudeWait(string sessionName)
    {
        if (!_claudeWaits.Remove(sessionName, out var reason)) return;
        AppLogger.Info($"[hooks] {sessionName}: {reason} -> none (session started, ended or closed)");
        RefreshClaudeAttention();
    }

    // Run when the user may now be looking at another session (page switch, tab switch, window activated).
    private void AcknowledgeSeenWaits()
    {
        var seen = _claudeWaits.Where(cw => ClaudeAttention.ClearsWhenSeen(cw.Value) && IsWatching(cw.Key)).Select(cw => cw.Key).ToList();
        if (seen.Count == 0) return;
        foreach (var sessionName in seen)
        {
            _claudeWaits.Remove(sessionName);
            AppLogger.Info($"[hooks] {sessionName}: Finished -> none (seen)");
        }
        RefreshClaudeAttention();
    }

    // The window is in front and showing this session, so the dot/badge is enough.
    private bool IsWatching(string sessionName)
    {
        if (_shell == null || _shell.IsDisposed || !_shell.IsForeground) return false;
        if (TerminalTabs.IsTerminalSession(sessionName)) return _shell.CurrentPage == ShellPage.Claude && _selectedTerminal == sessionName;
        return _shell.CurrentPage == ShellPage.Ticket && _selectedTicket == sessionName;
    }

    // Also run once the shell is built, so a rebuilt window shows the waits at once.
    private void RefreshClaudeAttention()
    {
        if (_shell == null || _shell.IsDisposed) return;
        var waitingTerminals = _claudeWaits.Keys.Where(TerminalTabs.IsTerminalSession).ToList();
        var waitingTickets = _claudeWaits.Keys.Where(sn => !TerminalTabs.IsTerminalSession(sn)).ToList();
        _shell.SetBadge(ShellPage.Claude, waitingTerminals.Count);
        _shell.SetBadge(ShellPage.Ticket, waitingTickets.Count);
        _claudePage?.SetWaiting(waitingTerminals);
        _ticketPage?.SetWaiting(waitingTickets);
        _dashboardPage?.SetClaudeWaits(_claudeWaits.Select(cw => new ClaudeWait(cw.Key, ClaudeSessionLabel(cw.Key), cw.Value)).ToList());
    }

    // As the notices word it: "Terminal <folder>" for a Terminal tab, else the ticket key.
    private string ClaudeSessionLabel(string sessionName)
    {
        if (!TerminalTabs.IsTerminalSession(sessionName)) return sessionName;
        var host = _terminalHosts.GetValueOrDefault(sessionName);
        return $"Terminal {TerminalTabs.Label(host?.CurrentDirectory ?? TerminalDirectory(sessionName))}";
    }
}
