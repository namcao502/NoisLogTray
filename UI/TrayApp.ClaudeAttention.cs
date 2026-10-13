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
        host.Starting += () => ClearClaudeWait(sessionName);
        host.Exited += () => RunOnUi(() => ClearClaudeWait(sessionName));
    }

    private void OnClaudeHook(string sessionName, ClaudeHookEvent hookEvent)
    {
        var host = sessionName == ClaudeAttention.GeneralSessionName ? _claudeHost : _ticketSessions.GetValueOrDefault(sessionName);
        if (host == null || !host.IsRunning) return; // a closed tab's or an ended session's late hook
        if (sessionName == ClaudeAttention.GeneralSessionName) RememberClaudeSessionId(hookEvent);
        ClaudeWaitReason? current = _claudeWaits.TryGetValue(sessionName, out var reason) ? reason : null;
        var next = ClaudeAttention.Next(current, hookEvent);
        // A turn that finishes while the user looks at that session needs no dot.
        if (next != null && ClaudeAttention.ClearsWhenSeen(next.Value) && IsWatching(sessionName)) next = null;
        if (next == current) return;
        AppLogger.Info($"[hooks] {sessionName}: {current?.ToString() ?? "none"} -> {next?.ToString() ?? "none"} ({hookEvent.EventName})");
        if (next == null) _claudeWaits.Remove(sessionName);
        else _claudeWaits[sessionName] = next.Value;
        RefreshClaudeAttention();
        if (next != null && !IsWatching(sessionName))
        {
            var isGeneral = sessionName == ClaudeAttention.GeneralSessionName;
            var label = isGeneral ? "Terminal" : sessionName;
            Notify(ClaudeAttention.NoticeText(label, next.Value), ToolTipIcon.Info,
                open: isGeneral ? () => ShowShell(ShellPage.Claude) : () => ShowTicketTab(sessionName));
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
        if (sessionName == ClaudeAttention.GeneralSessionName) return _shell.CurrentPage == ShellPage.Claude;
        return _shell.CurrentPage == ShellPage.Ticket && _selectedTicket == sessionName;
    }

    // Also run once the shell is built, so a rebuilt window shows the waits at once.
    private void RefreshClaudeAttention()
    {
        if (_shell == null || _shell.IsDisposed) return;
        var waitingTickets = _claudeWaits.Keys.Where(sn => sn != ClaudeAttention.GeneralSessionName).ToList();
        var generalWaiting = _claudeWaits.ContainsKey(ClaudeAttention.GeneralSessionName);
        _shell.SetBadge(ShellPage.Claude, generalWaiting ? 1 : 0);
        _shell.SetBadge(ShellPage.Ticket, waitingTickets.Count);
        _ticketPage?.SetWaiting(waitingTickets);
    }
}
