namespace NoisLogTray;

internal sealed partial class TrayApp
{
    // Settings > "Clean up worktrees...". Each delegate reads the UI-thread state first, then does the git work off-thread.
    private void OpenWorktreeCleanup()
    {
        var repoPath = DevRepoPath;
        using var dialog = new WorktreeCleanupDialog(
            async () =>
            {
                var (prTracking, startedKeys, runningKeys) = ReadCleanupState();
                using var busyWork = BusyWork.Shared.Begin(Lang.T("Reading worktrees...", "Đang đọc worktree..."));
                return await Task.Run(() => WorktreeCleanupService.LoadRows(repoPath, prTracking, startedKeys, runningKeys));
            },
            async ticketKey =>
            {
                var (prTracking, startedKeys, runningKeys) = ReadCleanupState();
                using var busyWork = BusyWork.Shared.Begin(Lang.T("Reading worktrees...", "Đang đọc worktree..."));
                return await Task.Run(() => WorktreeCleanupService.LoadRow(repoPath, ticketKey, prTracking, startedKeys, runningKeys,
                    WorktreeCleanupService.MinedKeys(repoPath), WorktreeCleanupService.MergedKeys(repoPath)));
            },
            async row =>
            {
                var (prTracking, startedKeys, runningKeys) = ReadCleanupState();
                var terminalRunning = runningKeys.Contains(row.TicketKey);
                var error = await RunCleanupAsync(Lang.T($"Removing the {row.TicketKey} worktree...", $"Đang xóa worktree {row.TicketKey}..."),
                    () => WorktreeCleanupService.RemoveChecked(repoPath, row.TicketKey, prTracking, startedKeys, terminalRunning, row.DirtyFiles));
                // Here too, not only on close: a Remove still running when the dialog closes finishes after that refresh.
                RefreshAfterCleanup();
                return error;
            },
            ticketKey => RunCleanupAsync(Lang.T($"Trimming the {ticketKey} docs...", $"Đang thu gọn docs {ticketKey}..."),
                () => WorktreeCleanupService.SlimDocs(repoPath, ticketKey)),
            ticketKey => RunCleanupAsync(Lang.T($"Mining knowledge from {ticketKey} (a few minutes)...", $"Đang mine knowledge {ticketKey} (vài phút)..."),
                () => KnowledgeMiner.Mine(repoPath, ticketKey)));
        dialog.ShowDialog(_shell);
        RefreshAfterCleanup();
    }

    // Closes the tabs whose worktree is gone and redraws the board and Dashboard.
    private void RefreshAfterCleanup()
    {
        PruneTicketTabs();
        var selectedTicket = _selectedTicket != null && _openTickets.Contains(_selectedTicket) ? _selectedTicket : null;
        _ticketPage?.SetTabs(_openTickets, selectedTicket);
        RefreshTicketViews();
    }

    private (IReadOnlyDictionary<string, TrackedPr> PrTracking, IReadOnlySet<string> StartedKeys, IReadOnlySet<string> RunningKeys) ReadCleanupState()
    {
        IReadOnlyDictionary<string, TrackedPr> prTracking = _ticketStore?.LoadPrTracking() ?? new Dictionary<string, TrackedPr>();
        IReadOnlySet<string> startedKeys = _ticketStore?.LoadStarted().Keys.ToHashSet() ?? new HashSet<string>();
        return (prTracking, startedKeys, RunningTicketKeys());
    }

    // Returns the action's error text (null on success); the work runs off the UI thread.
    private static async Task<string?> RunCleanupAsync(string label, Func<string?> action)
    {
        using var busyWork = BusyWork.Shared.Begin(label);
        AppLogger.Info($"[cleanup] {label}");
        string? error;
        try
        {
            error = await Task.Run(action);
        }
        catch (Exception exception)
        {
            // Shown on the row like any other failure, not left to the global handler.
            AppLogger.Error($"[cleanup] {label} {exception}");
            error = exception.Message;
        }
        if (error == null)
        {
            AppLogger.Info($"[cleanup] done: {label}");
        }
        else
        {
            AppLogger.Error($"[cleanup] failed: {label} {error}");
        }

        return error;
    }

    // Tickets whose terminal runs: a ticket tab's session, or any Terminal page tab sitting inside that ticket's worktree.
    private HashSet<string> RunningTicketKeys()
    {
        var runningKeys = _ticketSessions
            .Where(kv => kv.Value.IsRunning)
            .Select(kv => kv.Key)
            .ToHashSet();
        foreach (var terminalHost in _terminalHosts.Values.Where(csh => csh.IsRunning && csh.CurrentDirectory != null))
        {
            var worktreeKey = WorktreeCleanup.WorktreeKeyOf(terminalHost.CurrentDirectory!);
            if (worktreeKey != null) runningKeys.Add(worktreeKey);
        }

        return runningKeys;
    }
}
