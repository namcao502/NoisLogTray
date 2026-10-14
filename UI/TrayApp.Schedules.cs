using System.Globalization;

namespace NoisLogTray;

// Settings > "Schedules..." and the watch on the daily knowledge scan: a notice, a Settings badge and a dot on that button
// when a run ends badly, since the script's own toast never shows for a run Task Scheduler killed.
internal sealed partial class TrayApp
{
    private readonly System.Windows.Forms.Timer _knowledgeScanTimer = new() { Interval = (int)TimeSpan.FromMinutes(5).TotalMilliseconds };
    private bool _checkingKnowledgeScan;
    private DateTime? _knowledgeScanBadgeRunAt; // the unseen bad run behind the Settings badge
    private SchedulesDialog? _schedulesDialog; // while open
    private DateTime _ticketTimerArmedAt = DateTime.Now; // when _tasksTimer last started counting down

    private void StartKnowledgeScanWatch()
    {
        _knowledgeScanTimer.Tick += (_, _) => _ = CheckKnowledgeScanAsync();
        _knowledgeScanTimer.Start();
        _ = CheckKnowledgeScanAsync();
    }

    private async Task CheckKnowledgeScanAsync()
    {
        if (_checkingKnowledgeScan)
        {
            return;
        }

        _checkingKnowledgeScan = true;
        try
        {
            var devRepoPath = DevRepoPath;
            var snapshot = await Task.Run(() => KnowledgeScanService.Read(devRepoPath));
            OnKnowledgeScanRead(snapshot);
        }
        catch (Exception exception)
        {
            AppLogger.Error($"[schedules] Checking the knowledge scan failed: {exception}");
        }
        finally
        {
            _checkingKnowledgeScan = false;
        }
    }

    // Also raised by the Schedules dialog after each of its reads, so what it shows and the badge agree. Without the task's
    // state a run cannot be told apart from one still going, so a failed Task Scheduler read decides nothing.
    private void OnKnowledgeScanRead(KnowledgeScanSnapshot snapshot)
    {
        if (snapshot.TaskError != null)
        {
            return;
        }

        var newest = KnowledgeScan.NewestOutcome(snapshot.Runs, snapshot.Task, DateTime.Now);
        if (newest == null)
        {
            return;
        }

        var (startedAt, outcome) = newest.Value;
        var isWatchingSchedules = _schedulesDialog != null && Form.ActiveForm == _schedulesDialog;
        var settings = AppSettings.Load();
        var notice = KnowledgeScan.NoticeFor(startedAt, outcome, settings.KnowledgeScanJudgedRunAt, settings.KnowledgeScanSeenRunAt,
            isWatchingSchedules);
        if (notice == null)
        {
            return;
        }

        if (notice.JudgedRunAt != settings.KnowledgeScanJudgedRunAt || notice.SeenRunAt != settings.KnowledgeScanSeenRunAt)
        {
            settings.KnowledgeScanJudgedRunAt = notice.JudgedRunAt;
            settings.KnowledgeScanSeenRunAt = notice.SeenRunAt;
            AppSettings.Save(settings);
        }

        if (notice.Notify)
        {
            Log($"[schedules] Knowledge scan of {startedAt:yyyy-MM-dd HH:mm} ended {outcome}.");
            // Deferred: the notice's toast, bell row or balloon click is still on the stack, and ShowDialog runs a message loop.
            Notify(KnowledgeScanNoticeText(snapshot, startedAt, outcome), ToolTipIcon.Warning,
                open: () => _marshal.BeginInvoke(new Action(() =>
                {
                    ShowShell(ShellPage.Settings);
                    OpenSchedules();
                })));
        }

        _knowledgeScanBadgeRunAt = notice.ShowBadge ? startedAt : null;
        ShowSchedulesBadge();
    }

    // Settings > "Schedules...", modal like the worktree cleanup. Opening it, or coming back to it, counts as seeing the bad
    // run behind the badge.
    private void OpenSchedules()
    {
        if (_schedulesDialog != null)
        {
            _schedulesDialog.Activate();
            return;
        }
        // Another modal (the worktree cleanup, a question) holds the window; the notice stays in the bell.
        if (_shell == null || _shell.IsDisposed || !_shell.Enabled)
        {
            return;
        }

        var page = new SchedulesPage(() => DevRepoPath, AppJobStatuses);
        page.KnowledgeScanRead += OnKnowledgeScanRead;
        page.StatusRaised += (message, kind) => Notify(message, kind == NoticeKind.Error ? ToolTipIcon.Warning : ToolTipIcon.Info, kind);
        var dialog = new SchedulesDialog(page);
        dialog.Activated += (_, _) => AcknowledgeKnowledgeScan();
        _schedulesDialog = dialog;
        try
        {
            // Before the first read: it may come back before the dialog is active, and must not count the run as unseen.
            AcknowledgeKnowledgeScan();
            dialog.ShowDialog(_shell);
        }
        finally
        {
            _schedulesDialog = null;
            dialog.Dispose();
        }
    }

    private void AcknowledgeKnowledgeScan()
    {
        if (_knowledgeScanBadgeRunAt == null || _schedulesDialog == null)
        {
            return;
        }

        var settings = AppSettings.Load();
        settings.KnowledgeScanSeenRunAt = _knowledgeScanBadgeRunAt;
        AppSettings.Save(settings);
        _knowledgeScanBadgeRunAt = null;
        ShowSchedulesBadge();
    }

    private void ShowSchedulesBadge()
    {
        if (_shell != null && !_shell.IsDisposed)
        {
            _shell.SetBadge(ShellPage.Settings, _knowledgeScanBadgeRunAt != null ? 1 : 0);
        }
        _settingsPage?.SetSchedulesAttention(_knowledgeScanBadgeRunAt != null);
    }

    // FinishedWithProblems gets no notice (the script toasts those itself), so only these three reach here.
    private static string KnowledgeScanNoticeText(KnowledgeScanSnapshot snapshot, DateTime startedAt, ScanRunOutcome outcome)
    {
        var startedText = SchedulesPage.When(startedAt);
        if (outcome == ScanRunOutcome.StartFailed)
        {
            var resultText = $"0x{snapshot.Task?.LastTaskResult ?? 0:X}";
            return Lang.T($"The knowledge scan started {startedText} ended before writing to its log (result {resultText}).",
                $"Lần quét KB lúc {startedText} kết thúc trước khi ghi được gì vào log (kết quả {resultText}).");
        }

        var run = snapshot.Runs.LastOrDefault(ksr => ksr.StartedAt == startedAt);
        var lastLineText = run != null ? run.EndedAt.ToString("HH:mm", CultureInfo.InvariantCulture) : "?";
        if (outcome == ScanRunOutcome.StoppedByTaskScheduler)
        {
            return Lang.T($"The knowledge scan started {startedText} was stopped by Task Scheduler (time limit or End); its log stops at {lastLineText}.",
                $"Lần quét KB lúc {startedText} bị Task Scheduler dừng (hết giới hạn thời gian hoặc End); log dừng lúc {lastLineText}.");
        }

        return Lang.T($"The knowledge scan started {startedText} did not finish; its log stops at {lastLineText}.",
            $"Lần quét KB lúc {startedText} không chạy xong; log dừng lúc {lastLineText}.");
    }

    // For the Schedules dialog, on the UI thread.
    private IReadOnlyList<AppJobStatus> AppJobStatuses()
    {
        var runsByJob = JobRuns.Load();
        var logTimeText = (_service?.LogTime ?? AppConfig.DefaultLogTime).ToString("h:mm tt", CultureInfo.InvariantCulture);
        var isTicketCheckOn = _service != null && _ticketPoller != null;
        DateTime? nextAutoLog = _service != null ? _scheduler.NextFire.LocalDateTime : null;
        DateTime? nextTicketCheck = isTicketCheckOn ? _ticketTimerArmedAt.AddMinutes(TicketCheckInterval.Minutes) : null;
        var ticketCheckSchedule = isTicketCheckOn
            ? Lang.T($"Every {TicketCheckInterval.Minutes} min", $"Mỗi {TicketCheckInterval.Minutes} phút")
            : Lang.T("Off this session", "Tắt trong phiên này");
        return
        [
            new AppJobStatus(AppJob.AutoLog, Lang.T($"Every day at {logTimeText}", $"Mỗi ngày lúc {logTimeText}"), nextAutoLog,
                runsByJob.GetValueOrDefault(AppJob.AutoLog, new List<JobRun>())),
            new AppJobStatus(AppJob.TicketCheck, ticketCheckSchedule, nextTicketCheck, runsByJob.GetValueOrDefault(AppJob.TicketCheck, new List<JobRun>())),
            new AppJobStatus(AppJob.CatchUp, Lang.T("At startup and after a TSC sign-in", "Khi mở app và sau khi đăng nhập lại TSC"), null,
                runsByJob.GetValueOrDefault(AppJob.CatchUp, new List<JobRun>())),
        ];
    }
}
