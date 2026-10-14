namespace NoisLogTray;

// The queue's scheduled runs: the daily fire at LOG_TIME, the catch-up at startup and after a TSC sign-in, and the drain both
// use. Each scheduled run is recorded in JobRuns for the Schedules dialog.
internal sealed partial class TrayApp
{
    // If the app was not running at 18:00 (asleep, off, or launched later), a queue
    // that is already due would otherwise wait until the next 18:00. Drain once on
    // startup when something is loggable now: a past-dated entry (loggable anytime),
    // or a today entry once it is 18:00 or later (HRM rejects future stop times).
    private void CatchUpIfDue()
    {
        if (_service == null) return;

        var now = Hcm.Now();
        var today = DateOnly.FromDateTime(now.DateTime);
        var due = false;
        foreach (var entry in TicketQueue.Read())
        {
            if (!DateOnly.TryParseExact(entry.Date, "yyyy-MM-dd", out var date)) continue;
            if (date < today || (date == today && now.Hour >= 18)) { due = true; break; }
        }

        if (!due)
        {
            JobRuns.Record(AppJob.CatchUp, new JobRun(DateTimeOffset.Now, DateTimeOffset.Now, true, Lang.T("Nothing due", "Không có gì tới hạn")));
            return;
        }

        Log("[scheduler] Catch-up: queue has due entries; draining now.");
        _ = CatchUpDrainAsync();
    }

    private async Task CatchUpDrainAsync()
    {
        var startedAt = DateTimeOffset.Now;
        var (succeeded, summary) = await DrainAsync(fromUser: false);
        JobRuns.Record(AppJob.CatchUp, new JobRun(startedAt, DateTimeOffset.Now, succeeded, summary));
    }

    // The daily scheduled fire (at the configured time), recorded for the Schedules dialog.
    private async Task OnScheduledFireAsync()
    {
        var startedAt = DateTimeOffset.Now;
        (bool Succeeded, string Summary) outcome = (false, Lang.T("Failed (see log)", "Lỗi (xem log)"));
        try
        {
            outcome = await RunScheduledLogAsync();
        }
        finally
        {
            JobRuns.Record(AppJob.AutoLog, new JobRun(startedAt, DateTimeOffset.Now, outcome.Succeeded, outcome.Summary));
        }
    }

    // If something is queued, drain it as usual. If nothing is queued and it's a weekday, open the window as a
    // reminder to log manually; on weekends stay silent.
    private async Task<(bool Succeeded, string Summary)> RunScheduledLogAsync()
    {
        if (_service == null)
        {
            return (false, Lang.T("Not set up: no config", "Chưa thiết lập cấu hình"));
        }

        if (TicketQueue.Read().Count != 0)
        {
            return await DrainAsync(fromUser: false);
        }

        if (!IsWeekday(Hcm.Now()))
        {
            Log("[scheduler] Nothing queued (weekend); no reminder.");
            return (true, Lang.T("Nothing queued (weekend)", "Hàng đợi trống (cuối tuần)"));
        }

        // Read-only HRM lookup, no TSC write. GetOffDatesAsync never throws - a failed
        // call returns empty and the reminder still opens, since nagging is the safe
        // direction when a day off cannot be told from a missed working day.
        var today = Hcm.Today();
        var offDates = await _service.GetOffDatesAsync(today, today, Log);
        if (offDates.Contains(today))
        {
            Log("[scheduler] Today is an approved day off; no reminder.");
            return (true, Lang.T("Nothing queued (day off)", "Hàng đợi trống (ngày nghỉ)"));
        }

        Log("[scheduler] Nothing queued on a weekday; opening the window as a reminder.");
        RunOnUi(() =>
        {
            ShowShell(ShellPage.Work);
            Notify(Lang.T("Reminder: nothing is queued for today - log your work before you leave.",
                "Nhắc nhở: hôm nay chưa có gì trong hàng đợi - hãy log công việc trước khi về."), ToolTipIcon.Info, open: ShowWorkPage);
        });
        return (true, Lang.T("Nothing queued: reminder shown", "Hàng đợi trống: đã nhắc"));
    }

    private static bool IsWeekday(DateTimeOffset t) =>
        t.DayOfWeek != DayOfWeek.Saturday && t.DayOfWeek != DayOfWeek.Sunday;

    // The outcome is what the Schedules dialog records for the auto-log and catch-up runs; a drain the user asked for ignores it.
    private async Task<(bool Succeeded, string Summary)> DrainAsync(bool fromUser)
    {
        if (_service == null)
        {
            if (fromUser) Notify(Lang.T("Config not loaded; cannot log.", "Chưa có cấu hình; không log được."), ToolTipIcon.Warning, open: EditCredentials);
            return (false, Lang.T("Not set up: no config", "Chưa thiết lập cấu hình"));
        }
        if (Interlocked.CompareExchange(ref _draining, 1, 0) != 0)
        {
            // A drain is already running; its snapshot may predate work just requested,
            // so flag one more pass to run after it finishes.
            Interlocked.Exchange(ref _redrainPending, 1);
            if (fromUser) Notify(Lang.T("A logging run is already in progress.", "Đang có một lượt log chạy."), ToolTipIcon.Info, open: ShowWorkPage);
            return (true, Lang.T("Joined the log run already going", "Gộp vào lượt log đang chạy"));
        }
        RunOnUi(() => { if (_dashboardPage != null && !_dashboardPage.IsDisposed) _dashboardPage.SetDraining(true); });

        var hadToday = false;
        (bool Succeeded, string Summary) outcome;
        var busyWork = BusyWork.Shared.Begin(Lang.T("Logging the queue...", "Đang log hàng đợi..."));
        try
        {
            var todayApiDate = Hcm.ApiDate(Hcm.Today());
            hadToday = TicketQueue.Read().Count(qe => qe.Date == todayApiDate) != 0;
            using var cts = new CancellationTokenSource(DrainTimeout);
            var r = await _service.DrainQueueAsync(Log, cts.Token);
            UpdateTooltip();
            RunOnUi(() => RefreshAfterDrain(hadToday));

            if (r.Total == 0)
            {
                if (fromUser) Notify(Lang.T("Queue is empty.", "Hàng đợi trống."), ToolTipIcon.Info, open: ShowWorkPage);
                outcome = (true, Lang.T("Queue empty", "Hàng đợi trống"));
            }
            else if (r.Kept > 0)
            {
                Notify(Lang.T($"Auto-log: {r.Logged} logged, {r.Kept} kept. Check TSC sign-in (Re-authenticate) - it retries automatically.",
                    $"Tự log: đã log {r.Logged}, giữ lại {r.Kept}. Kiểm tra đăng nhập TSC (Đăng nhập lại) - app sẽ tự thử lại."),
                    ToolTipIcon.Warning, open: ShowWorkPage);
                outcome = (false, Lang.T($"{r.Logged} logged, {r.Kept} kept for retry", $"Đã log {r.Logged}, giữ lại {r.Kept} để thử lại"));
            }
            else
            {
                Notify(Lang.T($"Auto-log: {r.Logged} logged.", $"Tự log: đã log {r.Logged}."), ToolTipIcon.Info, open: ShowWorkPage);
                outcome = (true, Lang.T($"{r.Logged} logged", $"Đã log {r.Logged}"));
            }
        }
        catch (OperationCanceledException)
        {
            UpdateTooltip();
            RunOnUi(() => RefreshAfterDrain(hadToday));
            Log($"[drain] Timed out after {DrainTimeout.TotalMinutes:0} min; kept unlogged entries for retry.");
            Notify(Lang.T("Auto-log timed out; kept entries for retry. Check TSC sign-in if this repeats.",
                "Tự log quá thời gian; đã giữ lại để thử lại. Nếu lặp lại, hãy kiểm tra đăng nhập TSC."),
                ToolTipIcon.Warning, open: ShowWorkPage);
            outcome = (false, Lang.T($"Timed out after {DrainTimeout.TotalMinutes:0} min; entries kept for retry",
                $"Quá {DrainTimeout.TotalMinutes:0} phút; đã giữ lại để thử lại"));
        }
        catch (Exception e)
        {
            // The drain may have removed some entries before failing.
            UpdateTooltip();
            RunOnUi(() => RefreshAfterDrain(hadToday));
            Log($"[drain] Error: {e.Message}");
            Notify(Lang.T($"Auto-log error: {e.Message}", $"Tự log lỗi: {e.Message}"), ToolTipIcon.Error, open: ShowWorkPage);
            outcome = (false, Lang.T($"Error: {e.Message}", $"Lỗi: {e.Message}"));
        }
        finally
        {
            busyWork.Dispose();
            Interlocked.Exchange(ref _draining, 0);
            RunOnUi(() => { if (_dashboardPage != null && !_dashboardPage.IsDisposed) _dashboardPage.SetDraining(false); });
        }

        // If work was requested while the drain above was running, run one more pass to
        // pick up entries that arrived after that drain read its snapshot.
        if (Interlocked.Exchange(ref _redrainPending, 0) == 1)
            await DrainAsync(fromUser: false);
        return outcome;
    }

    // UI thread only. Coverage re-reads without a sniff: the drain's Graph token is still cached.
    private void RefreshAfterDrain(bool hadToday)
    {
        if (_logPage != null && !_logPage.IsDisposed) { _logPage.RefreshQueuedView(); _logPage.ReloadWeekIfLoaded(); }
        if (_dashboardPage == null || _dashboardPage.IsDisposed) return;
        _dashboardPage.Reload();
        if (hadToday && _dashboardPage.CoverageLoaded) _dashboardPage.ReloadCoverage(allowSniff: false);
    }
}
