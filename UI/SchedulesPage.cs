using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace NoisLogTray;

// The body of Settings > "Schedules...": the daily knowledge scan (Windows task DailyKnowledgeScan) with its runs beside the
// selected run's log, and the tray's own jobs. Reads run off the UI thread, when shown and every minute while it stays.
internal sealed class SchedulesPage : UserControl, IShellPage
{
    private static readonly int PageMargin = UiScale.S(20);
    private static readonly int CardGap = UiScale.S(12);
    private static readonly int CardPad = UiScale.S(16);
    private static readonly int ButtonH = UiScale.S(30);
    private static readonly int ButtonGap = UiScale.S(8);
    private static readonly int LineH = UiScale.S(22);
    private static readonly int PillW = UiScale.S(40);
    private static readonly int RunRowH = UiScale.S(30);
    private static readonly int RunListW = UiScale.S(320);
    private static readonly int JobRowH = UiScale.S(44);
    private static readonly int AppJobCount = Enum.GetValues<AppJob>().Length;
    private const int ShownRunCount = 10;
    private const TextFormatFlags RowTextFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
        | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

    private static readonly Font TitleFont = UiScale.Font("Segoe UI Semibold", 11F, FontStyle.Bold);
    private static readonly Font TextFont = UiScale.Font("Segoe UI", 9F);
    private static readonly Font NameFont = UiScale.Font("Segoe UI", 9F, FontStyle.Bold);
    private static readonly Font LogFont = UiScale.Font("Consolas", 9F);

    private readonly Func<string> _devRepoPath;
    private readonly Func<IReadOnlyList<AppJobStatus>> _appJobStatuses;
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 60_000 };
    private readonly ThemedToolTip _tips = new();
    private readonly Card _scanCard = new();
    private readonly Label _scanTitle = new();
    private readonly SingleLineLabel _taskStatus = new();
    private readonly SingleLineLabel _runStatus = new();
    private readonly Label _timeLimitCaption = new();
    private readonly Dictionary<int, MacButton> _timeLimitPills = KnowledgeScan.TimeLimitChoicesHours
        .ToDictionary(h => h, h => MacButton.Secondary(h.ToString(CultureInfo.InvariantCulture)));
    private readonly Panel _runList = new();
    private readonly SingleLineLabel _runSummary = new();
    private readonly RoundedHost _logHost = new();
    private readonly TextBox _logBox = new();
    private readonly Card _jobsCard = new();
    private readonly Label _jobsTitle = new();
    private readonly Panel _jobList = new();

    private KnowledgeScanSnapshot? _snapshot;
    private IReadOnlyList<AppJobStatus> _jobs = [];
    private DateTime? _selectedRunStart; // null = the newest run
    private bool _reading;
    private bool _reloadPending;
    private bool _acting;
    private bool _renderPosted;
    private string _renderedRunsSignature = "";
    private string _renderedJobsSignature = "";

    internal event Action<KnowledgeScanSnapshot>? KnowledgeScanRead;
    internal event Action<string, NoticeKind>? StatusRaised;

    internal MacButton RunNowButton { get; } = MacButton.Primary("");
    internal MacButton OpenLogButton { get; } = MacButton.Secondary("");
    internal MacButton RefreshButton { get; } = MacButton.Secondary("");

    internal SchedulesPage(Func<string> devRepoPath, Func<IReadOnlyList<AppJobStatus>> appJobStatuses)
    {
        _devRepoPath = devRepoPath;
        _appJobStatuses = appJobStatuses;
        BackColor = Theme.WindowBg;
        MinimumSize = UiScale.S(new Size(640, 520));

        foreach (var label in new[] { _scanTitle, _jobsTitle, _taskStatus, _runStatus, _timeLimitCaption, _runSummary })
        {
            label.AutoSize = false;
            label.BackColor = Color.Transparent;
            label.UseMnemonic = false;
            label.Font = TextFont;
            label.TextAlign = ContentAlignment.MiddleLeft;
        }
        _scanTitle.Font = TitleFont;
        _jobsTitle.Font = TitleFont;
        ThemedToolTip.Track(_taskStatus);
        ThemedToolTip.Track(_runStatus);
        ThemedToolTip.Track(_runSummary);

        // Each wide enough for its longer language, so a switch never clips a label.
        foreach (var (button, labels) in new (MacButton Button, string[] Labels)[]
        {
            (RunNowButton, ["Run now", "Chạy ngay"]),
            (OpenLogButton, ["Open log", "Mở log"]),
            (RefreshButton, ["Refresh", "Làm mới"]),
        })
        {
            var labelW = labels.Max(l => TextRenderer.MeasureText(l, button.Font).Width);
            button.Size = new Size(Math.Max(UiScale.S(90), labelW + UiScale.S(28)), ButtonH);
        }
        RunNowButton.Click += async (_, _) => await RunNowAsync();
        OpenLogButton.Click += (_, _) => OpenLog();
        RefreshButton.Click += async (_, _) => await ReloadAsync();
        foreach (var (hours, pill) in _timeLimitPills)
        {
            pill.Click += async (_, _) => await SetTimeLimitAsync(hours);
        }

        // AppendText goes through the typed-text limit, which a long run's log would pass.
        _logBox.MaxLength = 0;
        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.WordWrap = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.BorderStyle = BorderStyle.None;
        _logBox.Font = LogFont;
        _logHost.Controls.Add(_logBox);
        ThemedScrollBar.Attach(_logBox);
        _runList.AutoScroll = true;
        _runList.BackColor = Color.Transparent;
        _jobList.BackColor = Color.Transparent;

        _scanCard.Controls.AddRange([_scanTitle, RunNowButton, OpenLogButton, RefreshButton, _taskStatus, _runStatus, _timeLimitCaption,
            .. _timeLimitPills.Values, _runList, _runSummary, _logHost]);
        ThemedScrollBar.Attach(_runList);
        _jobsCard.Controls.AddRange([_jobsTitle, _jobList]);
        Controls.AddRange([_scanCard, _jobsCard]);

        _refreshTimer.Tick += (_, _) =>
        {
            // A page switch calls no hook, so a hidden page just skips the tick.
            if (Visible)
            {
                _ = ReloadAsync();
            }
        };
        ApplyTheme();
        ApplyLanguage();
        Theme.Changed += ApplyTheme;
        Lang.Changed += ApplyLanguage;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= ApplyTheme;
            Lang.Changed -= ApplyLanguage;
            _refreshTimer.Dispose();
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }

    // For tests: SchedulesDialog shows (OnPageShown) and hides (OnHidden) the page with itself.
    internal bool RefreshTimerEnabled => _refreshTimer.Enabled;

    public void OnPageShown()
    {
        _refreshTimer.Start();
        _ = ReloadAsync();
    }

    public void OnActivatedRefresh()
    {
        _ = ReloadAsync();
    }

    public void OnHidden()
    {
        _refreshTimer.Stop();
    }

    internal string StatusText()
    {
        return _taskStatus.Text + Environment.NewLine + _runStatus.Text;
    }

    internal string LogText()
    {
        return _logBox.Text;
    }

    internal IReadOnlyList<Control> RunRows()
    {
        return _runList.Controls.Cast<Control>().ToList();
    }

    internal IReadOnlyList<Control> JobRows()
    {
        return _jobList.Controls.Cast<Control>().ToList();
    }

    internal MacButton TimeLimitPill(int hours)
    {
        return _timeLimitPills[hours];
    }

    // The end of every read; xUnit has no message loop, so the tests call it directly.
    internal void ShowData(KnowledgeScanSnapshot snapshot, IReadOnlyList<AppJobStatus> jobs)
    {
        _snapshot = snapshot;
        _jobs = jobs;
        _taskStatus.Text = TaskStatusLine(snapshot);
        _runStatus.Text = RunStatusLine(snapshot);
        RenderRuns();
        ShowSelectedRun();
        RenderJobs();
        UpdateActionState();
    }

    internal void SelectRun(DateTime runStartedAt)
    {
        _selectedRunStart = runStartedAt;
        foreach (Control row in _runList.Controls)
        {
            row.Invalidate();
        }
        ShowSelectedRun();
    }

    // "Today 09:00", else "Oct 14 09:00" ("14/10 09:00" in Vietnamese).
    internal static string When(DateTime time)
    {
        var dayText = time.Date == DateTime.Today ? Lang.T("Today", "Hôm nay") : Lang.ShortDate(time);
        return $"{dayText} {time.ToString("HH:mm", CultureInfo.InvariantCulture)}";
    }

    // "45s", "58m", "3h", "1h 05m": the same in both languages, and short enough for a run row.
    internal static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMinutes(1))
        {
            return $"{Math.Max(0, duration.Seconds)}s";
        }

        if (duration < TimeSpan.FromHours(1))
        {
            return $"{duration.Minutes}m";
        }

        if (duration.Minutes == 0)
        {
            return $"{(int)duration.TotalHours}h";
        }

        return $"{(int)duration.TotalHours}h {duration.Minutes:00}m";
    }

    internal static string OutcomeText(ScanRunOutcome outcome)
    {
        return outcome switch
        {
            ScanRunOutcome.Running => Lang.T("Running", "Đang chạy"),
            ScanRunOutcome.Succeeded => Lang.T("Finished", "Xong"),
            ScanRunOutcome.FinishedWithProblems => Lang.T("With problems", "Có lỗi"),
            ScanRunOutcome.StoppedByTaskScheduler => Lang.T("Stopped", "Bị dừng"),
            ScanRunOutcome.StartFailed => Lang.T("No log", "Không có log"),
            _ => Lang.T("Unfinished", "Dở dang"),
        };
    }

    internal static string JobName(AppJob job)
    {
        return job switch
        {
            AppJob.AutoLog => Lang.T("Auto-log to TSC + HRM", "Tự log TSC + HRM"),
            AppJob.TicketCheck => Lang.T("Jira and PR check", "Kiểm tra Jira và PR"),
            _ => Lang.T("Catch-up log", "Log bù"),
        };
    }

    // A run still going is timed up to now, not up to its last log line.
    private static TimeSpan RunDuration(KnowledgeScanRun run, ScanRunOutcome outcome)
    {
        return (outcome == ScanRunOutcome.Running ? DateTime.Now : run.EndedAt) - run.StartedAt;
    }

    private async Task ReloadAsync()
    {
        if (_reading)
        {
            // A read asked for mid-read (after Run now or a pill) follows the one already going.
            _reloadPending = true;
            return;
        }

        _reading = true;
        UpdateActionState();
        try
        {
            var devRepoPath = _devRepoPath();
            KnowledgeScanSnapshot snapshot;
            using (BusyWork.Shared.Begin(Lang.T("Reading the schedules...", "Đang đọc lịch chạy...")))
            {
                snapshot = await Task.Run(() => KnowledgeScanService.Read(devRepoPath));
            }

            if (IsDisposed)
            {
                return;
            }

            ShowData(snapshot, _appJobStatuses());
            KnowledgeScanRead?.Invoke(snapshot);
        }
        catch (Exception exception)
        {
            // Started fire-and-forget: an escaping exception would go unobserved and leave the page stale without a word.
            AppLogger.Error($"[schedules] Reading the schedules failed: {exception}");
            if (!IsDisposed)
            {
                _runStatus.Text = Lang.T("Could not read the schedules (see log).", "Không đọc được lịch chạy (xem log).");
            }
        }
        finally
        {
            _reading = false;
            if (!IsDisposed)
            {
                UpdateActionState();
            }
        }

        if (_reloadPending && !IsDisposed)
        {
            _reloadPending = false;
            await ReloadAsync();
        }
    }

    private async Task RunNowAsync()
    {
        var isConfirmed = ConfirmDialog.Ask(FindForm(), Lang.T("Run the knowledge scan now?", "Chạy quét KB ngay?"),
            Lang.T("It mines every ticket that reached Done since the last complete run and refreshes the codebase maps, with claude running headless. It can take an hour or more.",
                "Scan sẽ mine mọi ticket lên Done từ lần chạy xong gần nhất và làm mới codebase map, claude chạy ở chế độ nền. Có thể mất hơn một giờ."),
            Lang.T("Run now", "Chạy ngay"), confirmByDefault: true);
        if (!isConfirmed)
        {
            return;
        }

        await ActAsync(Lang.T("Starting the knowledge scan...", "Đang khởi động quét KB..."), KnowledgeScanService.RunNow,
            Lang.T("Could not start the knowledge scan", "Không khởi động được quét KB"));
    }

    private async Task SetTimeLimitAsync(int hours)
    {
        if (_snapshot?.Task?.ExecutionTimeLimit == TimeSpan.FromHours(hours))
        {
            return;
        }

        await ActAsync(Lang.T($"Setting the scan's time limit to {hours}h...", $"Đang đặt giới hạn scan {hours} giờ..."),
            () => KnowledgeScanService.SetTimeLimit(hours),
            Lang.T("Could not change the scan's time limit", "Không đổi được giới hạn thời gian của scan"));
    }

    // One Task Scheduler change off the UI thread, then a fresh read; a failure becomes a notice.
    private async Task ActAsync(string busyLabel, Action action, string failureText)
    {
        if (_acting)
        {
            return;
        }

        _acting = true;
        UpdateActionState();
        try
        {
            using (BusyWork.Shared.Begin(busyLabel))
            {
                await Task.Run(action);
            }
        }
        catch (Exception exception)
        {
            // Late-bound COM fails in many exception types; every one of them is reported the same way.
            AppLogger.Error($"[schedules] {failureText}: {exception}");
            StatusRaised?.Invoke($"{failureText}: {exception.Message}", NoticeKind.Error);
        }
        finally
        {
            _acting = false;
        }

        if (!IsDisposed)
        {
            await ReloadAsync();
        }
    }

    private void OpenLog()
    {
        if (_snapshot == null)
        {
            return;
        }

        TicketLauncher.TryOpenVsCode(_snapshot.LogPath, SelectedRun()?.FirstLineNumber);
    }

    private static string TaskStatusLine(KnowledgeScanSnapshot snapshot)
    {
        var task = snapshot.Task;
        if (snapshot.TaskError != null)
        {
            return Lang.T($"Could not read Task Scheduler: {snapshot.TaskError}", $"Không đọc được Task Scheduler: {snapshot.TaskError}");
        }

        if (task == null)
        {
            return Lang.T($"There is no Windows task named {KnowledgeScan.TaskName}.", $"Không có task Windows nào tên {KnowledgeScan.TaskName}.");
        }

        var scheduleText = Lang.T("custom schedule", "lịch tuỳ chỉnh");
        if (task.DailyStartTime != null)
        {
            var startText = task.DailyStartTime.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
            scheduleText = task.DailyIntervalDays == 1
                ? Lang.T($"every day at {startText}", $"mỗi ngày lúc {startText}")
                : Lang.T($"every {task.DailyIntervalDays} days at {startText}", $"mỗi {task.DailyIntervalDays} ngày lúc {startText}");
        }

        var limitText = task.ExecutionTimeLimit != null
            ? Lang.T($"stops after {Duration(task.ExecutionTimeLimit.Value)}", $"dừng sau {Duration(task.ExecutionTimeLimit.Value)}")
            : Lang.T("no time limit", "không giới hạn thời gian");
        var nextText = task.NextRunTime != null
            ? Lang.T($"next run {When(task.NextRunTime.Value)}", $"lần tới {When(task.NextRunTime.Value)}")
            : Lang.T("no next run", "không có lần chạy tới");
        var stateText = "";
        if (task.State == ScheduledTaskState.Running)
        {
            stateText = Lang.T(" Running now.", " Đang chạy.");
        }
        else if (task.State == ScheduledTaskState.Disabled)
        {
            stateText = Lang.T(" Disabled.", " Đang tắt.");
        }

        return Lang.T($"Windows task {KnowledgeScan.TaskName}: {scheduleText}, {limitText}, {nextText}.",
            $"Task Windows {KnowledgeScan.TaskName}: {scheduleText}, {limitText}, {nextText}.") + stateText;
    }

    private static string RunStatusLine(KnowledgeScanSnapshot snapshot)
    {
        if (snapshot.IsLogMissing)
        {
            return Lang.T($"No log yet at {snapshot.LogPath}.", $"Chưa có log tại {snapshot.LogPath}.");
        }

        if (snapshot.LogError != null)
        {
            return $"Log: {snapshot.LogError}";
        }

        var newest = KnowledgeScan.NewestOutcome(snapshot.Runs, snapshot.Task, DateTime.Now);
        if (newest == null)
        {
            return Lang.T("No runs in the log yet.", "Log chưa có lần chạy nào.");
        }

        var (startedAt, outcome) = newest.Value;
        if (outcome == ScanRunOutcome.StartFailed)
        {
            var resultText = $"0x{snapshot.Task?.LastTaskResult ?? 0:X}";
            return Lang.T($"Started {When(startedAt)} but wrote nothing to the log (result {resultText}).",
                $"Đã chạy lúc {When(startedAt)} nhưng không ghi gì vào log (kết quả {resultText}).");
        }

        // The limit now set may not be the one that stopped it, so the cause is named without a number.
        var outcomeText = outcome == ScanRunOutcome.StoppedByTaskScheduler
            ? Lang.T("stopped by Task Scheduler (time limit or End)", "bị Task Scheduler dừng (hết giới hạn thời gian hoặc End)")
            : OutcomeText(outcome);
        var durationText = Duration(RunDuration(snapshot.Runs[^1], outcome));
        return Lang.T($"Last run {When(startedAt)}, {durationText}: {outcomeText}.",
            $"Lần chạy cuối {When(startedAt)}, {durationText}: {outcomeText}.");
    }

    // Newest first, built at the list's fixed width; rebuilt only when what the rows show changed, so the minute refresh
    // keeps the scroll and the focus.
    private void RenderRuns()
    {
        var runs = _snapshot?.Runs ?? [];
        var task = _snapshot?.Task;
        var now = DateTime.Now;
        var shownRuns = Enumerable.Reverse(runs).Take(ShownRunCount)
            .Select(ksr => (Run: ksr, Outcome: KnowledgeScan.OutcomeOf(ksr, runs, task, now)))
            .ToList();
        var runsSignature = $"{Lang.Vietnamese}|" + string.Join("|", shownRuns.Select(ro => $"{ro.Run.StartedAt:o},{ro.Run.EndedAt:o},{ro.Outcome}"));
        if (runsSignature == _renderedRunsSignature)
        {
            _runList.Invalidate(true);
            return;
        }

        _renderedRunsSignature = runsSignature;
        DisposeRows(_runList);
        var rowWidth = Math.Max(0, RunListW - SystemInformation.VerticalScrollBarWidth);
        for (var runIndex = 0; runIndex < shownRuns.Count; runIndex++)
        {
            var (run, outcome) = shownRuns[runIndex];
            var isStriped = runIndex % 2 == 1;
            var row = new ClickableRow
            {
                Bounds = new Rectangle(0, runIndex * RunRowH + _runList.AutoScrollPosition.Y, rowWidth, RunRowH),
                Cursor = Cursors.Hand,
                AccessibleName = $"{When(run.StartedAt)} {OutcomeText(outcome)}",
            };
            row.Paint += (_, e) => PaintRunRow(e.Graphics, row, run, outcome, isStriped);
            row.Click += (_, _) => SelectRun(run.StartedAt);
            _runList.Controls.Add(row);
        }
    }

    private void PaintRunRow(Graphics graphics, ClickableRow row, KnowledgeScanRun run, ScanRunOutcome outcome, bool isStriped)
    {
        var isSelected = IsSelected(run);
        var background = Theme.CardSurface;
        if (isSelected)
        {
            background = Theme.Hover;
        }
        else if (isStriped)
        {
            background = Theme.RowStripe(Theme.CardSurface);
        }
        graphics.Clear(background);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (isSelected)
        {
            using var accentBrush = new SolidBrush(Theme.Accent);
            graphics.FillRectangle(accentBrush, 0, 0, UiScale.S(3), row.Height);
        }

        var whenBounds = new Rectangle(UiScale.S(10), 0, UiScale.S(96), row.Height);
        TextRenderer.DrawText(graphics, When(run.StartedAt), TextFont, whenBounds, Theme.TextPrimary, RowTextFlags);
        var durationBounds = new Rectangle(whenBounds.Right, 0, UiScale.S(64), row.Height);
        TextRenderer.DrawText(graphics, Duration(RunDuration(run, outcome)), TextFont, durationBounds, Theme.TextSecondary, RowTextFlags);
        var dotSize = UiScale.S(8);
        using (var dotBrush = new SolidBrush(OutcomeColor(outcome)))
        {
            graphics.FillEllipse(dotBrush, durationBounds.Right, (row.Height - dotSize) / 2, dotSize, dotSize);
        }
        var outcomeLeft = durationBounds.Right + dotSize + UiScale.S(6);
        var outcomeBounds = new Rectangle(outcomeLeft, 0, Math.Max(0, row.Width - outcomeLeft - UiScale.S(6)), row.Height);
        TextRenderer.DrawText(graphics, OutcomeText(outcome), TextFont, outcomeBounds, Theme.TextPrimary, RowTextFlags);
    }

    private static Color OutcomeColor(ScanRunOutcome outcome)
    {
        return outcome switch
        {
            ScanRunOutcome.Running => Theme.Accent,
            ScanRunOutcome.Succeeded => Theme.ActionStart,
            ScanRunOutcome.FinishedWithProblems => TaskRow.Orange,
            _ => Theme.Badge,
        };
    }

    // By start time: a kept row holds the run of the read that built it, while each read parses new run objects.
    internal bool IsSelected(KnowledgeScanRun run)
    {
        return run.StartedAt == SelectedRun()?.StartedAt;
    }

    // The clicked run while the log still holds it, else the newest.
    private KnowledgeScanRun? SelectedRun()
    {
        var runs = _snapshot?.Runs ?? [];
        if (runs.Count == 0)
        {
            return null;
        }

        return runs.LastOrDefault(ksr => ksr.StartedAt == _selectedRunStart) ?? runs[^1];
    }

    private void ShowSelectedRun()
    {
        var run = SelectedRun();
        if (run == null || _snapshot == null)
        {
            _runSummary.Text = "";
            _logBox.Text = "";
            return;
        }

        var outcome = KnowledgeScan.OutcomeOf(run, _snapshot.Runs, _snapshot.Task, DateTime.Now);
        var countTexts = new List<string>
        {
            Lang.T($"{run.FoundTicketCount} reached Done", $"{run.FoundTicketCount} ticket lên Done"),
            Lang.T($"{run.MinedTicketCount} mined", $"mine {run.MinedTicketCount}"),
            Lang.T($"{run.RefreshedMapCount} map(s) refreshed", $"làm mới {run.RefreshedMapCount} map"),
        };
        if (run.ProblemLines.Count != 0)
        {
            countTexts.Add(Lang.T($"{run.ProblemLines.Count} problem(s)", $"{run.ProblemLines.Count} lỗi"));
        }
        _runSummary.Text = $"{When(run.StartedAt)}, {Duration(RunDuration(run, outcome))}: {string.Join(", ", countTexts)}";

        // A run that only grew (still running) gets its new lines appended, so the box follows them instead of jumping to the top.
        var logText = string.Join(Environment.NewLine, run.Lines);
        if (logText == _logBox.Text)
        {
            return;
        }

        if (_logBox.TextLength != 0 && logText.StartsWith(_logBox.Text, StringComparison.Ordinal))
        {
            _logBox.AppendText(logText[_logBox.TextLength..]);
        }
        else
        {
            _logBox.Text = logText;
        }
    }

    private void RenderJobs()
    {
        var jobsSignature = $"{Lang.Vietnamese}|{_jobList.Width}|" + string.Join("|", _jobs.Select(ajs =>
            $"{ajs.Job},{ajs.ScheduleText},{ajs.NextRunAt:o},{ajs.Runs.Count},{(ajs.Runs.Count != 0 ? ajs.Runs[^1].StartedAt : default):o}"));
        if (jobsSignature == _renderedJobsSignature)
        {
            _jobList.Invalidate(true);
            return;
        }

        _renderedJobsSignature = jobsSignature;
        DisposeRows(_jobList);
        for (var jobIndex = 0; jobIndex < _jobs.Count; jobIndex++)
        {
            var jobStatus = _jobs[jobIndex];
            var isStriped = jobIndex % 2 == 1;
            var row = new Panel
            {
                Bounds = new Rectangle(0, jobIndex * JobRowH, _jobList.Width, JobRowH),
                AccessibleName = JobName(jobStatus.Job),
            };
            row.Paint += (_, e) => PaintJobRow(e.Graphics, row, jobStatus, isStriped);
            _tips.SetUntilDisposed(row, RecentRunsText(jobStatus));
            _jobList.Controls.Add(row);
        }
    }

    // Line 1: the job and its next run; line 2: its schedule, then the last run's result.
    private static void PaintJobRow(Graphics graphics, Panel row, AppJobStatus jobStatus, bool isStriped)
    {
        graphics.Clear(isStriped ? Theme.RowStripe(Theme.CardSurface) : Theme.CardSurface);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var pad = UiScale.S(10);
        var lineH = row.Height / 2;
        var nextText = jobStatus.NextRunAt != null
            ? Lang.T($"Next {When(jobStatus.NextRunAt.Value)}", $"Lần tới {When(jobStatus.NextRunAt.Value)}")
            : "";
        var nextW = TextRenderer.MeasureText(nextText, TextFont).Width;
        var firstLineTop = UiScale.S(2);
        TextRenderer.DrawText(graphics, JobName(jobStatus.Job), NameFont,
            new Rectangle(pad, firstLineTop, Math.Max(0, row.Width - 3 * pad - nextW), lineH), Theme.TextPrimary, RowTextFlags);
        TextRenderer.DrawText(graphics, nextText, TextFont, new Rectangle(row.Width - pad - nextW, firstLineTop, nextW, lineH),
            Theme.TextSecondary, RowTextFlags);

        var secondLineTop = lineH - UiScale.S(2);
        var scheduleW = row.Width * 2 / 5;
        TextRenderer.DrawText(graphics, jobStatus.ScheduleText, TextFont, new Rectangle(pad, secondLineTop, Math.Max(0, scheduleW - pad), lineH),
            Theme.TextSecondary, RowTextFlags);
        var lastRun = jobStatus.Runs.Count != 0 ? jobStatus.Runs[^1] : null;
        var dotColor = TaskRow.Gray;
        var lastRunText = Lang.T("No runs yet", "Chưa chạy lần nào");
        if (lastRun != null)
        {
            dotColor = lastRun.Succeeded ? Theme.ActionStart : Theme.Badge;
            lastRunText = $"{When(lastRun.StartedAt.LocalDateTime)}: {lastRun.Summary}";
        }
        var dotSize = UiScale.S(8);
        using (var dotBrush = new SolidBrush(dotColor))
        {
            graphics.FillEllipse(dotBrush, scheduleW, secondLineTop + (lineH - dotSize) / 2, dotSize, dotSize);
        }
        var lastRunLeft = scheduleW + dotSize + UiScale.S(6);
        TextRenderer.DrawText(graphics, lastRunText, TextFont, new Rectangle(lastRunLeft, secondLineTop, Math.Max(0, row.Width - pad - lastRunLeft), lineH),
            Theme.TextPrimary, RowTextFlags);
    }

    // The tooltip: the job's recorded runs, newest first.
    private static string RecentRunsText(AppJobStatus jobStatus)
    {
        if (jobStatus.Runs.Count == 0)
        {
            return Lang.T("No runs yet", "Chưa chạy lần nào");
        }

        return string.Join(Environment.NewLine, Enumerable.Reverse(jobStatus.Runs)
            .Select(jr => $"{When(jr.StartedAt.LocalDateTime)} ({Duration(jr.EndedAt - jr.StartedAt)}): {jr.Summary}"));
    }

    private static void DisposeRows(Panel list)
    {
        var oldRows = list.Controls.Cast<Control>().ToList();
        list.Controls.Clear();
        foreach (var oldRow in oldRows)
        {
            oldRow.Dispose();
        }
    }

    private void UpdateActionState()
    {
        var task = _snapshot?.Task;
        var canAct = !_acting && task != null;
        RunNowButton.Enabled = canAct && task != null && task.State != ScheduledTaskState.Running && task.State != ScheduledTaskState.Disabled;
        OpenLogButton.Enabled = _snapshot != null && !_snapshot.IsLogMissing && _snapshot.LogError == null;
        RefreshButton.Enabled = !_reading;
        foreach (var (hours, pill) in _timeLimitPills)
        {
            pill.Enabled = canAct;
            pill.Selected = task != null && task.ExecutionTimeLimit == TimeSpan.FromHours(hours);
        }
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var cardW = Math.Max(0, ClientSize.Width - 2 * PageMargin);
        var jobsCardH = CardPad + LineH + UiScale.S(8) + AppJobCount * JobRowH + CardPad;
        var scanCardH = Math.Max(0, ClientSize.Height - 2 * PageMargin - CardGap - jobsCardH);
        _scanCard.SetBounds(PageMargin, PageMargin, cardW, scanCardH);
        _jobsCard.SetBounds(PageMargin, _scanCard.Bottom + CardGap, cardW, jobsCardH);

        var innerW = Math.Max(0, cardW - 2 * CardPad);
        var buttonsLeft = CardPad + innerW;
        foreach (var button in new[] { RunNowButton, OpenLogButton, RefreshButton })
        {
            buttonsLeft -= button.Width;
            button.SetBounds(buttonsLeft, CardPad, button.Width, ButtonH);
            buttonsLeft -= ButtonGap;
        }
        _scanTitle.SetBounds(CardPad, CardPad, Math.Max(0, buttonsLeft - CardPad), ButtonH);
        var y = CardPad + ButtonH + UiScale.S(8);
        _taskStatus.SetBounds(CardPad, y, innerW, LineH);
        _runStatus.SetBounds(CardPad, _taskStatus.Bottom, innerW, LineH);
        y = _runStatus.Bottom + UiScale.S(6);
        var captionW = TextRenderer.MeasureText(_timeLimitCaption.Text, TextFont).Width + UiScale.S(8);
        _timeLimitCaption.SetBounds(CardPad, y, captionW, ButtonH);
        var pillLeft = _timeLimitCaption.Right + ButtonGap;
        foreach (var hours in KnowledgeScan.TimeLimitChoicesHours)
        {
            _timeLimitPills[hours].SetBounds(pillLeft, y, PillW, ButtonH);
            pillLeft += PillW + UiScale.S(6);
        }
        y += ButtonH + UiScale.S(12);

        var bodyH = Math.Max(0, scanCardH - CardPad - y);
        _runList.SetBounds(CardPad, y, RunListW, bodyH);
        var logLeft = _runList.Right + UiScale.S(12);
        var logW = Math.Max(0, CardPad + innerW - logLeft);
        _runSummary.SetBounds(logLeft, y, logW, LineH);
        _logHost.SetBounds(logLeft, y + LineH + UiScale.S(4), logW, Math.Max(0, bodyH - LineH - UiScale.S(4)));
        _logBox.SetBounds(UiScale.S(10), UiScale.S(8), Math.Max(0, _logHost.Width - UiScale.S(20)), Math.Max(0, _logHost.Height - UiScale.S(16)));

        _jobsTitle.SetBounds(CardPad, CardPad, innerW, LineH);
        _jobList.SetBounds(CardPad, CardPad + LineH + UiScale.S(8), innerW, AppJobCount * JobRowH);

        // Job rows are built at the list's width: rebuild once it changed, posted since the layout is still running.
        if (IsHandleCreated && !_renderPosted && !_renderedJobsSignature.Contains($"|{_jobList.Width}|", StringComparison.Ordinal))
        {
            _renderPosted = true;
            BeginInvoke(new Action(() =>
            {
                _renderPosted = false;
                if (!IsDisposed)
                {
                    RenderJobs();
                }
            }));
        }
    }

    private void ApplyTheme()
    {
        BackColor = Theme.WindowBg;
        _scanTitle.ForeColor = Theme.TextPrimary;
        _jobsTitle.ForeColor = Theme.TextPrimary;
        _taskStatus.ForeColor = Theme.TextSecondary;
        _runStatus.ForeColor = Theme.TextSecondary;
        _timeLimitCaption.ForeColor = Theme.TextSecondary;
        _runSummary.ForeColor = Theme.TextSecondary;
        _logBox.BackColor = Theme.InputBg;
        _logBox.ForeColor = Theme.TextPrimary;
        Invalidate(true);
    }

    private void ApplyLanguage()
    {
        _scanTitle.Text = Lang.T("Daily knowledge scan", "Quét knowledge base hằng ngày");
        _jobsTitle.Text = Lang.T("In-app jobs", "Việc chạy trong app");
        _timeLimitCaption.Text = Lang.T("Stop a run after (hours)", "Dừng lần chạy sau (giờ)");
        RunNowButton.Text = Lang.T("Run now", "Chạy ngay");
        OpenLogButton.Text = Lang.T("Open log", "Mở log");
        RefreshButton.Text = Lang.T("Refresh", "Làm mới");
        if (_snapshot != null)
        {
            ShowData(_snapshot, _appJobStatuses());
        }
        PerformLayout();
    }
}
