using System.Text.RegularExpressions;

namespace NoisLogTray;

// The Ticket page's tabs: one ClaudeSessionHost per open ticket, kept here (not in the page) so a shell
// rebuild reattaches to running sessions; the open keys persist in settings.json.
internal sealed partial class TrayApp
{
    private readonly Dictionary<string, ClaudeSessionHost> _ticketSessions = new();
    private readonly HashSet<string> _shipPending = new(); // fresh worktrees whose first session runs `ship <KEY>`
    // Keys end up in paths, git refs and a cmd.exe line, so a hand-edited settings.json may only hold real ticket keys.
    private static readonly Regex TicketKeyPattern = new("^[A-Za-z][A-Za-z0-9]*-[0-9]+$", RegexOptions.Compiled);
    private readonly List<string> _openTickets = AppSettings.Load().OpenTicketTabs.Where(tk => tk != null && TicketKeyPattern.IsMatch(tk)).Distinct().ToList();
    private string? _selectedTicket;

    private string DevRepoPath => _service?.DevRepoPath ?? AppConfig.DefaultDevRepoPath;

    private string BackendWorktreePath(string ticketKey) => TicketWorktrees.BackendPath(DevRepoPath, ticketKey);

    // The ContainsKey check and first SetLaunchStage run before any await, so a double click cannot launch twice.
    // A failed worktree still Starts and queues the ticket (the work has begun); only the tab needs the worktree.
    private async Task StartTicketAsync(string ticketKey)
    {
        if (_ticketBoard == null || _launchStages.ContainsKey(ticketKey)) return;
        var repoPath = DevRepoPath;
        var worktreePath = TicketWorktrees.BackendPath(repoPath, ticketKey);
        var ready = false;
        if (!Directory.Exists(repoPath))
        {
            NotifyRepoNotFound(repoPath);
        }
        else if (Directory.Exists(worktreePath))
        {
            ready = true; // the tab's session picks up the worktree's last Claude session, if any
        }
        else
        {
            SetLaunchStage(ticketKey, StartStage.Fetching);
            // Created on the UI thread so reports come back here; a report after the finally is ignored.
            var progress = new Progress<StartStage>(stage =>
            {
                if (_launchStages.ContainsKey(ticketKey)) SetLaunchStage(ticketKey, stage);
            });
            try
            {
                ready = await Task.Run(() => WorktreeService.TryCreate(ticketKey, repoPath, worktreePath, progress));
                if (ready)
                    _shipPending.Add(ticketKey);
                else
                    Notify(Lang.T($"{ticketKey}: worktree failed (see log).", $"{ticketKey}: tạo worktree lỗi (xem log)."), ToolTipIcon.Warning);
            }
            catch (Exception e)
            {
                AppLogger.Error($"[tasks] Start {ticketKey} failed: {e}");
                Notify(Lang.T($"{ticketKey}: start failed (see log).", $"{ticketKey}: Start lỗi (xem log)."), ToolTipIcon.Warning);
            }
            finally
            {
                SetLaunchStage(ticketKey, null);
            }
        }

        _ticketBoard.Start(ticketKey);
        RefreshTicketViews();
        if (ready) OpenTicketTab(ticketKey);
        QueueTicketForToday(ticketKey);
    }

    // Open: the ticket's workspace tab (Claude session and changes); no board or queue change.
    // Not async, but keeps the event's Task type.
    private Task OpenTicketAsync(string ticketKey)
    {
        // Start may still be creating the worktree: opening the tab now would start Claude too early.
        if (_launchStages.ContainsKey(ticketKey)) return Task.CompletedTask;
        if (!Directory.Exists(DevRepoPath))
        {
            NotifyRepoNotFound(DevRepoPath);
            return Task.CompletedTask;
        }
        if (!Directory.Exists(BackendWorktreePath(ticketKey)))
        {
            NotifyNoWorktree(ticketKey);
            return Task.CompletedTask;
        }

        OpenTicketTab(ticketKey);
        ClearPrCommentAlert(ticketKey);
        return Task.CompletedTask;
    }

    // `ship` is dropped only once a session really started, so a failed start keeps it for the next try.
    private ClaudeSessionHost SessionFor(string ticketKey)
    {
        if (_ticketSessions.TryGetValue(ticketKey, out var host)) return host;
        host = new ClaudeSessionHost(() => BackendWorktreePath(ticketKey),
            () => _shipPending.Contains(ticketKey) ? $"ship {ticketKey}"
                : TicketLauncher.GetIdlePrompt(BackendWorktreePath(ticketKey), TicketLauncher.ClaudeProjectsRoot),
            fallBackToProfile: false, settingsFile: HookSettingsFor(ticketKey));
        host.Started += () => _shipPending.Remove(ticketKey);
        TrackClaudeSession(ticketKey, host);
        _ticketSessions[ticketKey] = host;
        return host;
    }

    private void OpenTicketTab(string ticketKey)
    {
        if (!_openTickets.Contains(ticketKey))
        {
            _openTickets.Add(ticketKey);
            SaveOpenTickets();
        }
        _selectedTicket = ticketKey;
        _ticketPage?.SetTabs(_openTickets, ticketKey);
        ShowShell(ShellPage.Ticket);
    }

    // A running session (maybe a long `ship`) is only stopped after a Yes. The view is disposed (detaching
    // from the host) by SetTabs before the host stops.
    private void CloseTicketTab(string ticketKey)
    {
        var host = _ticketSessions.GetValueOrDefault(ticketKey);
        if (host != null && host.IsRunning)
        {
            var answer = MessageBox.Show(_shell,
                Lang.T($"Claude is still running in {ticketKey}. Close the tab and stop it?",
                    $"Claude vẫn đang chạy trong {ticketKey}. Đóng tab và dừng nó?"),
                Lang.T("Close tab", "Đóng tab"), MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
        }
        _openTickets.Remove(ticketKey);
        SaveOpenTickets();
        _shipPending.Remove(ticketKey);
        _ticketPage?.SetTabs(_openTickets, null);
        _ticketSessions.Remove(ticketKey);
        host?.Dispose();
        ClearClaudeWait(ticketKey); // Dispose does not raise Exited
    }

    // A tab whose worktree is gone and whose session is not running has nothing left to show. Skipped while the
    // dev repo itself is missing (a mistyped path in Settings), so fixing the path brings the tabs back.
    private void PruneTicketTabs()
    {
        if (!Directory.Exists(DevRepoPath)) return;
        var pruned = false;
        foreach (var ticketKey in _openTickets.ToList())
        {
            var host = _ticketSessions.GetValueOrDefault(ticketKey);
            if (Directory.Exists(BackendWorktreePath(ticketKey)) || (host != null && host.IsRunning)) continue;
            _openTickets.Remove(ticketKey);
            _shipPending.Remove(ticketKey);
            _ticketSessions.Remove(ticketKey);
            host?.Dispose();
            ClearClaudeWait(ticketKey);
            pruned = true;
        }
        if (pruned) SaveOpenTickets();
    }

    // Read-modify-write like the theme/window keys; AppSettings.Save is best effort (a failed write only loses persistence).
    private void SaveOpenTickets()
    {
        var settings = AppSettings.Load();
        settings.OpenTicketTabs = _openTickets.ToList();
        AppSettings.Save(settings);
    }

    private void OpenInVsCode(string ticketKey)
    {
        var worktreePath = BackendWorktreePath(ticketKey);
        if (!Directory.Exists(worktreePath))
        {
            NotifyNoWorktree(ticketKey);
            return;
        }
        try
        {
            TicketLauncher.OpenVsCode(worktreePath);
        }
        catch (Exception e)
        {
            AppLogger.Error($"[ticket] Opening {ticketKey} in VS Code failed: {e}");
            Notify(Lang.T($"{ticketKey}: could not open VS Code (see log).", $"{ticketKey}: không mở được VS Code (xem log)."), ToolTipIcon.Warning);
        }
    }

    // Open sits on In progress rows, where Start is gone, so the notice names the folder rather than a button.
    private void NotifyNoWorktree(string ticketKey) =>
        Notify(Lang.T($"{ticketKey} has no worktree at {BackendWorktreePath(ticketKey)}.",
            $"{ticketKey} chưa có worktree tại {BackendWorktreePath(ticketKey)}."), ToolTipIcon.Info);
}
