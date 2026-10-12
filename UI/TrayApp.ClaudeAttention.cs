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

    // A (re)starting session, even one whose Start then fails, or an ended one waits for nobody.
    private void TrackClaudeSession(string sessionName, ClaudeSessionHost host)
    {
        host.Starting += () => ClearClaudeWait(sessionName);
        host.Exited += () => RunOnUi(() => ClearClaudeWait(sessionName));
    }

    private void OnClaudeHook(string sessionName, ClaudeHookEvent hookEvent)
    {
        var host = sessionName == ClaudeAttention.GeneralSessionName ? _claudeHost : _ticketSessions.GetValueOrDefault(sessionName);
        if (host == null || !host.IsRunning) return; // a closed tab's or an ended session's late hook
        ClaudeWaitReason? current = _claudeWaits.TryGetValue(sessionName, out var reason) ? reason : null;
        var next = ClaudeAttention.Next(current, hookEvent);
        if (next == current) return;
        AppLogger.Info($"[hooks] {sessionName}: {current?.ToString() ?? "none"} -> {next?.ToString() ?? "none"} ({hookEvent.EventName})");
        if (next == null) _claudeWaits.Remove(sessionName);
        else _claudeWaits[sessionName] = next.Value;
        RefreshClaudeAttention();
        if (next != null && !IsWatching(sessionName))
        {
            var label = sessionName == ClaudeAttention.GeneralSessionName ? Lang.T("Claude page", "Trang Claude") : sessionName;
            Notify(ClaudeAttention.NoticeText(label, next.Value), ToolTipIcon.Info);
        }
    }

    private void ClearClaudeWait(string sessionName)
    {
        if (!_claudeWaits.Remove(sessionName, out var reason)) return;
        AppLogger.Info($"[hooks] {sessionName}: {reason} -> none (session started, ended or closed)");
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
        _shell.SetBadge(ShellPage.Claude, _claudeWaits.ContainsKey(ClaudeAttention.GeneralSessionName) ? 1 : 0);
        _shell.SetBadge(ShellPage.Ticket, waitingTickets.Count);
        _ticketPage?.SetWaiting(waitingTickets);
    }
}
