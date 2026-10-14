namespace NoisLogTray;

// The Jira and PR check, every TicketCheckInterval.Minutes and on "Check now"; each check is recorded in JobRuns.
internal sealed partial class TrayApp
{
    // Poll Jira for newly assigned / finished tickets. Started on the UI thread, so the
    // continuations (store access, form updates) stay there.
    private async Task CheckTicketsAsync()
    {
        if (_checkingTickets || _service == null || _ticketPoller == null) return;
        _checkingTickets = true;
        _tasksPage?.SetChecking(true, _lastTicketCheck);
        _dashboardPage?.SetChecking(true, _lastTicketCheck);
        var startedAt = DateTimeOffset.Now;
        var isCheckSucceeded = false;
        var checkSummary = Lang.T("Jira check failed (see log)", "Kiểm tra Jira lỗi (xem log)");
        using var busyWork = BusyWork.Shared.Begin(Lang.T("Checking Jira and PRs...", "Đang kiểm tra Jira và PR..."));
        try
        {
            try
            {
                var result = await _ticketPoller.PollAsync(_service.Jira);
                if (result == null)
                {
                    // Once per failure streak, so a long offline day does not nag at every check.
                    if (++_ticketPollFailures == 3)
                        Notify(Lang.T($"Jira ticket check failed {_ticketPollFailures} times in a row.",
                            $"Kiểm tra ticket Jira lỗi {_ticketPollFailures} lần liên tiếp."), ToolTipIcon.Warning);
                }
                else
                {
                    _ticketPollFailures = 0;
                    _lastTicketCheck = DateTime.Now;
                    if (result.NewTickets.Count != 0)
                        Notify(Lang.T("New ticket(s): ", "Ticket mới: ") + string.Join(", ", result.NewTickets.Select(jt => jt.Key)), ToolTipIcon.Info, open: ShowWorkPage);
                    if (result.FinishedTickets.Count != 0)
                        Notify(Lang.T("Done in Jira (move them to Done in Tasks): ", "Đã Done trên Jira (chuyển sang Done trong Tasks): ")
                            + string.Join(", ", result.FinishedTickets.Select(jt => jt.Key)), ToolTipIcon.Info, open: ShowWorkPage);
                    if (result.ReopenedTickets.Count != 0)
                        Notify(Lang.T("Sent back for more work, moved to In progress: ", "Bị trả về để làm tiếp, đã chuyển sang Đang làm: ")
                            + string.Join(", ", result.ReopenedTickets.Select(jt => jt.Key)), ToolTipIcon.Info, open: ShowWorkPage);
                    foreach (var ticket in result.NewTickets) await _teams.PostNewTicketAsync(ticket);

                    isCheckSucceeded = true;
                    var changeTexts = new List<string>();
                    if (result.NewTickets.Count != 0)
                    {
                        changeTexts.Add(Lang.T($"{result.NewTickets.Count} new", $"{result.NewTickets.Count} mới"));
                    }
                    if (result.FinishedTickets.Count != 0)
                    {
                        changeTexts.Add(Lang.T($"{result.FinishedTickets.Count} done in Jira", $"{result.FinishedTickets.Count} đã Done trên Jira"));
                    }
                    if (result.ReopenedTickets.Count != 0)
                    {
                        changeTexts.Add(Lang.T($"{result.ReopenedTickets.Count} sent back", $"{result.ReopenedTickets.Count} bị trả về"));
                    }
                    checkSummary = changeTexts.Count != 0 ? string.Join(", ", changeTexts) : Lang.T("No changes", "Không có thay đổi");
                }
            }
            catch (Exception e)
            {
                AppLogger.Error($"[tickets] Check failed: {e}");
            }

            // Azure DevOps does not depend on Jira, so PRs are checked even after a failed poll.
            try
            {
                await CheckPrsAsync();
            }
            catch (Exception e)
            {
                AppLogger.Error($"[ado] PR check failed: {e}");
                isCheckSucceeded = false;
                checkSummary += Lang.T("; PR check failed (see log)", "; kiểm tra PR lỗi (xem log)");
            }
        }
        finally
        {
            _checkingTickets = false;
            JobRuns.Record(AppJob.TicketCheck, new JobRun(startedAt, DateTimeOffset.Now, isCheckSucceeded, checkSummary));
            try
            {
                _tasksPage?.SetChecking(false, _lastTicketCheck);
                _dashboardPage?.SetChecking(false, _lastTicketCheck);
                RefreshTicketViews();
            }
            catch (Exception e)
            {
                AppLogger.Error($"[tickets] Refreshing the Tasks page failed: {e}");
            }
        }
    }

    private async Task CheckPrsAsync()
    {
        // Captured: a Settings save may swap them mid-check.
        var ado = _ado;
        var teams = _teams;
        if (ado == null || _ticketStore == null) return;
        // The first check only records current PR state, so pre-existing PRs do not flood notices.
        var baselineOnly = !_ticketStore.IsPrBaselineDone();
        var prs = await PrUpdateChecker.CheckAsync(ado, _ticketStore, baselineOnly);
        // Only a complete pass is a usable baseline; otherwise the next pass would announce everything it missed.
        if (baselineOnly && prs.IsComplete) _ticketStore.MarkPrBaselineDone();

        var commentAlertsToAdd = new List<(string TicketKey, int NewCommentCount, string PrUrl)>();
        foreach (var (ticketKey, notification) in prs.Notifications)
        {
            var branch = $"{DashboardDataBuilder.RepoName(notification.Pr.Repository)} {notification.Pr.Branch}"; // e.g. "FE Portal dev"
            var url = notification.Pr.Url;
            if (notification.IsMerged)
            {
                Notify(Lang.T($"{ticketKey}: PR ({branch}) merged.", $"{ticketKey}: PR ({branch}) đã merge."), ToolTipIcon.Info,
                    open: () => ShowShell(ShellPage.Dashboard));
                await teams.PostPrMergedAsync(ticketKey, branch, url);
            }
            if (notification.NewCommentCount != 0)
            {
                var newCommentCount = notification.NewCommentCount;
                commentAlertsToAdd.Add((ticketKey, newCommentCount, url));
                Notify(Lang.T($"{ticketKey}: {newCommentCount} new PR comment(s) ({branch}).", $"{ticketKey}: {newCommentCount} comment PR mới ({branch})."), ToolTipIcon.Info,
                    open: () => TaskRow.OpenUrl(url));
                await teams.PostNewPrCommentAsync(ticketKey, branch, url, newCommentCount);
            }
        }
        MergePrCommentAlerts(commentAlertsToAdd);
        foreach (var ticketKey in prs.DevMergedNeedingReadyToTest)
            Notify(Lang.T($"{ticketKey} merged into development - move it to Ready to Test once CI deployed.",
                $"{ticketKey} đã merge vào development - chuyển sang Ready to Test khi CI deploy xong."), ToolTipIcon.Info,
                open: () => ShowShell(ShellPage.Dashboard));
    }

    // Synchronous on purpose (no await): a "seen"/Open handled during the Teams awaits above is not overwritten.
    private void MergePrCommentAlerts(List<(string TicketKey, int NewCommentCount, string PrUrl)> commentAlertsToAdd)
    {
        var ticketStore = _ticketStore;
        if (ticketStore == null) return;
        try
        {
            var loadedAlerts = ticketStore.LoadPrCommentAlerts();
            var alerts = loadedAlerts;
            var atHcm = Hcm.Now();
            foreach (var (ticketKey, newCommentCount, prUrl) in commentAlertsToAdd)
                alerts = PrCommentAlerts.Add(alerts, ticketKey, newCommentCount, prUrl, atHcm);
            var boardKeys = ticketStore.LoadPending().Keys
                .Concat(ticketStore.LoadStarted().Keys)
                .Concat(ticketStore.LoadDone().Keys)
                .ToHashSet();
            alerts = PrCommentAlerts.KeepOnly(alerts, boardKeys);
            if (!PrCommentAlerts.AreSame(loadedAlerts, alerts)) ticketStore.SavePrCommentAlerts(alerts);
        }
        catch (Exception e)
        {
            AppLogger.Error($"[ado] Saving PR comment alerts failed: {e.Message}");
        }
    }
}
