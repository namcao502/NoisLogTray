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
            ready = await CreateWorktreeAsync(ticketKey);
            if (ready)
            {
                _shipPending.Add(ticketKey);
            }
        }

        _ticketBoard.Start(ticketKey);
        RefreshTicketViews();
        if (ready) OpenTicketTab(ticketKey);
        QueueTicketsForToday(new[] { ticketKey });
    }

    // True when the worktree is ready (a main that would not merge still counts). The first SetLaunchStage runs before
    // any await, so callers that checked _launchStages stay single-flight.
    private async Task<bool> CreateWorktreeAsync(string ticketKey)
    {
        var repoPath = DevRepoPath;
        var worktreePath = TicketWorktrees.BackendPath(repoPath, ticketKey);
        SetLaunchStage(ticketKey, StartStage.Fetching);
        using var busyWork = BusyWork.Shared.Begin(Lang.T($"Creating the {ticketKey} worktree...", $"Đang tạo worktree {ticketKey}..."));
        // Created on the UI thread so reports come back here; a report after the finally is ignored.
        var progress = new Progress<StartStage>(stage =>
        {
            if (_launchStages.ContainsKey(ticketKey))
            {
                SetLaunchStage(ticketKey, stage);
            }
        });
        try
        {
            var createResult = await Task.Run(() => WorktreeService.Create(ticketKey, repoPath, worktreePath, progress));
            if (createResult == WorktreeCreateResult.Failed)
            {
                Notify(Lang.T($"{ticketKey}: worktree failed (see log).", $"{ticketKey}: tạo worktree lỗi (xem log)."), ToolTipIcon.Warning);
                return false;
            }
            if (createResult == WorktreeCreateResult.MainNotMerged)
            {
                Notify(Lang.T($"{ticketKey}: could not merge main (see log); the worktree is on its old branch.",
                    $"{ticketKey}: chưa merge được main (xem log), worktree đang ở nhánh cũ."), ToolTipIcon.Warning);
            }
            return true;
        }
        catch (Exception exception)
        {
            // Start and Open both create worktrees here.
            AppLogger.Error($"[tasks] creating the {ticketKey} worktree failed: {exception}");
            Notify(Lang.T($"{ticketKey}: worktree failed (see log).", $"{ticketKey}: tạo worktree lỗi (xem log)."), ToolTipIcon.Warning);
            return false;
        }
        finally
        {
            SetLaunchStage(ticketKey, null);
        }
    }

    // Open: the ticket's workspace tab (Claude session and changes); no board or queue change.
    // A missing worktree is recreated after a confirm; it is not added to _shipPending, so the tab resumes the old transcript.
    private async Task OpenTicketAsync(string ticketKey)
    {
        // Start may still be creating the worktree: opening the tab now would start Claude too early.
        if (_launchStages.ContainsKey(ticketKey))
        {
            return;
        }
        if (!Directory.Exists(DevRepoPath))
        {
            NotifyRepoNotFound(DevRepoPath);
            return;
        }
        if (!Directory.Exists(BackendWorktreePath(ticketKey)))
        {
            var recreateConfirmed = ConfirmDialog.Ask(_shell, Lang.T($"Recreate the {ticketKey} worktree?", $"Tạo lại worktree {ticketKey}?"),
                Lang.T($"{ticketKey} has no worktree at {BackendWorktreePath(ticketKey)}. Check its branch out again and merge origin/main into it?",
                    $"{ticketKey} chưa có worktree tại {BackendWorktreePath(ticketKey)}. Checkout lại nhánh của nó và merge origin/main vào?"),
                Lang.T("Recreate", "Tạo lại"));
            // The dialog pumps messages, so a Start click may have begun (or even finished) creating it meanwhile.
            if (!recreateConfirmed || _launchStages.ContainsKey(ticketKey))
            {
                return;
            }
            if (!Directory.Exists(BackendWorktreePath(ticketKey)) && !await CreateWorktreeAsync(ticketKey))
            {
                return;
            }
        }

        OpenTicketTab(ticketKey);
        ClearPrCommentAlert(ticketKey);
    }

    // `ship` is dropped only once claude really runs (its first hook, OnClaudeHook), so a claude that fails inside the
    // PowerShell keeps it for the next try. Without hooks the terminal's start is the best sign there is.
    private ClaudeSessionHost SessionFor(string ticketKey)
    {
        if (_ticketSessions.TryGetValue(ticketKey, out var host)) return host;
        host = new ClaudeSessionHost(() => BackendWorktreePath(ticketKey),
            () => _shipPending.Contains(ticketKey) ? $"ship {ticketKey}"
                : TicketLauncher.GetIdlePrompt(BackendWorktreePath(ticketKey), TicketLauncher.ClaudeProjectsRoot),
            fallBackToProfile: false, settingsFile: HookSettingsFor(ticketKey));
        host.Started += () =>
        {
            if (!host.HasHooks) _shipPending.Remove(ticketKey);
        };
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

    // A notice's target: the ticket's tab while it is still open, else just the Ticket page (a click never starts Claude).
    private void ShowTicketTab(string ticketKey)
    {
        if (_openTickets.Contains(ticketKey)) OpenTicketTab(ticketKey);
        else ShowShell(ShellPage.Ticket);
    }

    // A running session (maybe a long `ship`) is only stopped after a Yes. The view is disposed (detaching
    // from the host) by SetTabs before the host stops.
    private void CloseTicketTab(string ticketKey)
    {
        var host = _ticketSessions.GetValueOrDefault(ticketKey);
        if (host != null && host.IsRunning)
        {
            var closeTab = ConfirmDialog.Ask(_shell, Lang.T($"Close {ticketKey}?", $"Đóng {ticketKey}?"),
                Lang.T($"The terminal is still running in {ticketKey}. Close the tab and stop it?",
                    $"Terminal vẫn đang chạy trong {ticketKey}. Đóng tab và dừng nó?"),
                Lang.T("Close tab", "Đóng tab"), destructive: true);
            if (!closeTab) return;
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
