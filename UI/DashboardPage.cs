using System.Drawing;
using System.Globalization;

namespace NoisLogTray;

// Today at a glance: four figures (the queue, today's TSC and HRM, the week), what to do next, tickets in progress and
// new tickets. Draws local data and raises events; TrayApp does the work.
internal sealed partial class DashboardPage : UserControl, IShellPage
{
    // Logical (100%) px, like ShellLayout's bounds; scaled where applied.
    private const int BuiltW = 600;
    private const int BuiltH = ShellLayout.DashboardMinHeight;
    private static readonly int CardPad = UiScale.S(16);
    private static readonly int BtnH = UiScale.S(30);
    private static readonly int ToolbarY = UiScale.S(7);    // card-header button, centred on the section title
    private static readonly int ListTop = UiScale.S(40);
    private static readonly int TableHeaderH = TaskTableHeader.TitleFont.Height + UiScale.S(10);
    private const double FullDayHours = 8.0;
    private DateOnly _dataDay;
    private DateOnly _weekDay;

    private static readonly Color Red = Color.FromArgb(230, 76, 76);
    private static readonly Color Orange = Color.FromArgb(255, 140, 50);

    private static readonly Font DateFont = UiScale.Font("Segoe UI Semibold", 11F, FontStyle.Bold);
    private static readonly Font LabelFont = UiScale.Font("Segoe UI", 9F);
    private static readonly Font SectionFont = UiScale.Font("Segoe UI", 8.5F, FontStyle.Bold);

    internal event Action? LogQueueRequested;
    internal event Action? CheckRequested;
    internal event Func<string, Task>? StartRequested;
    internal event Func<string, Task>? OpenRequested;
    internal event Func<string, Task>? ReadyToTestRequested;
    internal event Action<IReadOnlyList<string>>? QueueTicketsRequested;
    internal event Action<string>? MarkDoneRequested;
    internal event Action<string>? CommentsSeen;
    internal event Action<ShellPage>? PageRequested;
    internal event Action<DateOnly>? LogDayRequested;
    internal event Action<string>? ClaudeSessionRequested;
    internal event Action? CleanupRequested;

    private readonly LoggingService? _service;
    private readonly TicketStore? _store;
    private readonly string _jiraBaseUrl;
    private readonly Func<string> _devRepoPath;
    private readonly Func<IReadOnlyCollection<string>> _openTicketKeys;
    private readonly Dictionary<string, StartStage> _busyTickets = new();
    private readonly System.Windows.Forms.Timer _minuteTimer = new() { Interval = 60_000 };
    private readonly List<(Label Label, string English, string Vietnamese)> _sectionLabels = new();

    private DashboardData? _data;
    private bool _isTodayWritten;
    private IReadOnlyList<string> _cleanupKeys = Array.Empty<string>();
    private IReadOnlyList<ClaudeWait> _claudeWaits = Array.Empty<ClaudeWait>();
    private IReadOnlyList<DayCoverage>? _week;
    private bool _weekReading;
    private bool _weekFailed;
    private bool _weekLoaded; // read once per window show; OnHidden resets it
    private bool _reloadPending = true; // a Reload while hidden only marks this; the next show rebuilds once
    private bool _todosPending;
    private bool _rereadPending;
    private bool _rereadPendingSniff;
    private bool _draining;
    private bool _checking;
    private DateTime? _checkedAt;
    private bool _listsRenderPosted;
    private int _todoRenderedWidth;
    private int _inProgressRenderedWidth;
    private int _newTicketsRenderedWidth;
    private string? _renderedTodoSignature;

    // Placed by LayoutCards on every resize.
    private readonly StatTile _queueTile = new();
    private readonly StatTile _tscTile = new();
    private readonly StatTile _hrmTile = new();
    private readonly StatTile _weekTile = new();
    private Card? _todoCard;
    private Card? _inProgressCard;
    private Card? _newTicketsCard;

    private readonly Label _todayDate = new();
    private readonly MacButton _recheckBtn = MacButton.Secondary(string.Empty);
    private readonly MacButton _logQueueBtn = MacButton.Primary(string.Empty);
    private readonly Label _todoTitle = new();
    private readonly MacButton _allTicketsBtn = MacButton.Secondary(string.Empty);
    private readonly Label _checkedLabel = new();
    private readonly MacButton _checkBtn = MacButton.Secondary(string.Empty);
    private readonly FlowLayoutPanel _todoList = new();
    private readonly FlowLayoutPanel _inProgressList = new();
    private readonly FlowLayoutPanel _newTicketsList = new();
    // Each table's column titles, laid out by RenderLists at the list's row width.
    private readonly TaskTableHeader _inProgressHeader = new();
    private readonly TaskTableHeader _newTicketsHeader = new();

    internal DashboardPage(LoggingService? service, TicketStore? store, string jiraBaseUrl, Func<string> devRepoPath,
        Func<IReadOnlyCollection<string>> openTicketKeys)
    {
        _service = service;
        _store = store;
        _jiraBaseUrl = jiraBaseUrl;
        _devRepoPath = devRepoPath;
        _openTicketKeys = openTicketKeys;
        BuildLayout();
        _minuteTimer.Tick += (_, _) =>
        {
            // A page switch calls no hook, so a hidden page just skips the tick.
            if (!Visible) return;
            // Past midnight: yesterday's queue counts and coverage no longer apply. The week keeps its own day because
            // Reload runs from many places and would hide the rollover.
            var today = Hcm.Today();
            if (today != _dataDay) Reload();
            if (_weekLoaded && !_weekReading && _weekDay != today) ReloadCoverage(allowSniff: false);
            RenderToday();
            RenderCoverage();
            RenderTodos(); // the log time passing changes what is due
        };
        Theme.Changed += OnThemeChanged;
        Lang.Changed += OnLangChanged;
        TicketCheckInterval.Changed += OnCheckIntervalChanged;
        ReflowListsOnResize();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnThemeChanged;
            Lang.Changed -= OnLangChanged;
            TicketCheckInterval.Changed -= OnCheckIntervalChanged;
            _minuteTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    internal bool CoverageLoaded => _weekLoaded;

    public void OnPageShown()
    {
        if (_reloadPending || _dataDay != Hcm.Today()) Reload();
        else
        {
            // The clock may have passed the log time while another page was shown.
            RenderToday();
            RenderCoverage();
            RenderTodos(force: _todosPending);
        }
        // Once per window show, sniffing a Graph token when none is cached; the week card's first read shares it.
        if (!_weekLoaded)
        {
            _weekLoaded = true;
            ReloadCoverage(allowSniff: true);
        }
        _minuteTimer.Start();
    }

    public void OnActivatedRefresh()
    {
        if (_reloadPending || _dataDay != Hcm.Today()) Reload();
        else RenderToday();
    }

    // Always reset: a read still running when the window reopens is coalesced by _rereadPending.
    public void OnHidden()
    {
        _minuteTimer.Stop();
        _weekLoaded = false;
    }

    private void BuildLayout()
    {
        BackColor = Theme.WindowBg;

        // Cards are built at the bounds the built Size lays them out at, so the anchors set below record the right edge
        // distances. The header and the tiles are placed by LayoutCards alone, with no anchors.
        var (_, tiles, todo, inProgress, newTickets) = ShellLayout.DashboardBounds(BuiltW, BuiltH);
        _todayDate.AutoSize = false;
        ThemedToolTip.Track(_todayDate);
        _todayDate.TextAlign = ContentAlignment.MiddleLeft;
        _todayDate.Font = DateFont;
        _todayDate.BackColor = Color.Transparent;
        _recheckBtn.Click += (_, _) => ReloadCoverage(allowSniff: true);
        _logQueueBtn.Click += (_, _) => LogQueueRequested?.Invoke();
        Controls.Add(_todayDate);
        Controls.Add(_recheckBtn);
        Controls.Add(_logQueueBtn);
        var statTiles = new[] { _queueTile, _tscTile, _hrmTile, _weekTile };
        for (var tileIndex = 0; tileIndex < statTiles.Length; tileIndex++)
        {
            statTiles[tileIndex].Bounds = UiScale.S(tiles[tileIndex]);
            Controls.Add(statTiles[tileIndex]);
        }

        var todoCard = BuildTodoCard(UiScale.S(todo));
        var inProgressCard = BuildListCard(UiScale.S(inProgress), "IN PROGRESS", "ĐANG LÀM", _inProgressList, _inProgressHeader);
        var newTicketsCard = BuildListCard(UiScale.S(newTickets), "NEW TICKETS", "TICKET MỚI", _newTicketsList, _newTicketsHeader);
        BuildInProgressHeader(inProgressCard);
        BuildNewTicketsHeader(newTicketsCard);

        Controls.Add(todoCard);
        Controls.Add(inProgressCard);
        Controls.Add(newTicketsCard);

        ApplyTexts();
        ApplyColors();

        // Only now LayoutCards runs: the Size below lays everything out once, at the built bounds.
        _todoCard = todoCard;
        _inProgressCard = inProgressCard;
        _newTicketsCard = newTicketsCard;
        // Sized before anchoring: WinForms records each anchor's edge distances when Anchor is set.
        Size = UiScale.S(new Size(BuiltW, BuiltH));
        MinimumSize = UiScale.S(new Size(BuiltW, BuiltH));
        AnchorForResize();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        LayoutCards();
    }

    private void LayoutCards()
    {
        if (_todoCard == null || _inProgressCard == null || _newTicketsCard == null) return;

        // ShellLayout works in logical px: convert the page size in, scale the bounds back out.
        var (header, tiles, todo, inProgress, newTickets) = ShellLayout.DashboardBounds(
            UiScale.Logical(ClientSize.Width), UiScale.Logical(ClientSize.Height));
        SuspendLayout();
        var headerBounds = UiScale.S(header);
        var buttonWidth = ButtonWidths.ToolbarWidth(_logQueueBtn.Font);
        var buttonY = headerBounds.Y + (headerBounds.Height - BtnH) / 2;
        _logQueueBtn.Bounds = new Rectangle(headerBounds.Right - buttonWidth, buttonY, buttonWidth, BtnH);
        _recheckBtn.Bounds = new Rectangle(_logQueueBtn.Left - UiScale.S(8) - buttonWidth, buttonY, buttonWidth, BtnH);
        _todayDate.Bounds = new Rectangle(headerBounds.X, headerBounds.Y, Math.Max(UiScale.S(40), _recheckBtn.Left - UiScale.S(8) - headerBounds.X),
            headerBounds.Height);
        var statTiles = new[] { _queueTile, _tscTile, _hrmTile, _weekTile };
        for (var tileIndex = 0; tileIndex < statTiles.Length; tileIndex++) statTiles[tileIndex].Bounds = UiScale.S(tiles[tileIndex]);
        _todoCard.Bounds = UiScale.S(todo);
        _inProgressCard.Bounds = UiScale.S(inProgress);
        _newTicketsCard.Bounds = UiScale.S(newTickets);
        ResumeLayout();
    }

    private void AnchorForResize()
    {
        const AnchorStyles stretch = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        const AnchorStyles right = AnchorStyles.Top | AnchorStyles.Right;

        _todoTitle.Anchor = stretch;
        _allTicketsBtn.Anchor = right;
        _checkedLabel.Anchor = right;
        _checkBtn.Anchor = right;
        foreach (var list in new[] { _todoList, _inProgressList, _newTicketsList })
            list.Anchor = stretch | AnchorStyles.Bottom;
        foreach (var header in new[] { _inProgressHeader, _newTicketsHeader })
            header.Anchor = stretch;
    }

    private Card BuildTodoCard(Rectangle bounds)
    {
        var card = new Card { Bounds = bounds };
        _todoTitle.AutoSize = false;
        _todoTitle.Bounds = new Rectangle(CardPad, UiScale.S(14), bounds.Width - 2 * CardPad, SectionFont.Height + UiScale.S(2));
        _todoTitle.Font = SectionFont;
        _todoTitle.BackColor = Color.Transparent;
        card.Controls.Add(_todoTitle);

        var listInset = UiScale.S(8);
        _todoList.Bounds = new Rectangle(listInset, ListTop, bounds.Width - 2 * listInset, bounds.Height - ListTop - listInset);
        _todoList.FlowDirection = FlowDirection.TopDown;
        _todoList.WrapContents = false;
        _todoList.AutoScroll = true;
        _todoList.Padding = new Padding(0);
        card.Controls.Add(_todoList);
        ThemedScrollBar.Attach(_todoList);
        return card;
    }

    private Card BuildListCard(Rectangle bounds, string english, string vietnamese, FlowLayoutPanel list, TaskTableHeader header)
    {
        var card = new Card { Bounds = bounds };
        var title = new Label
        {
            AutoSize = true,
            Location = new Point(CardPad, UiScale.S(14)),
            Font = SectionFont,
            BackColor = Color.Transparent,
        };
        _sectionLabels.Add((title, english, vietnamese));
        card.Controls.Add(title);

        var listInset = UiScale.S(8);
        header.Bounds = new Rectangle(listInset, ListTop, bounds.Width - 2 * listInset, TableHeaderH);
        card.Controls.Add(header);
        var listTop = ListTop + TableHeaderH;
        list.Bounds = new Rectangle(listInset, listTop, bounds.Width - 2 * listInset, bounds.Height - listTop - listInset);
        list.FlowDirection = FlowDirection.TopDown;
        list.WrapContents = false;
        list.AutoScroll = true;
        list.Padding = new Padding(0);
        card.Controls.Add(list);
        ThemedScrollBar.Attach(list);
        return card;
    }

    private void BuildInProgressHeader(Card card)
    {
        _allTicketsBtn.Visible = _store != null; // no board to open when Tasks is off
        _allTicketsBtn.Click += (_, _) => PageRequested?.Invoke(ShellPage.Work);
        card.Controls.Add(_allTicketsBtn);
    }

    private void BuildNewTicketsHeader(Card card)
    {
        _checkedLabel.AutoSize = false;
        ThemedToolTip.Track(_checkedLabel);
        _checkedLabel.TextAlign = ContentAlignment.MiddleRight;
        _checkedLabel.Font = LabelFont;
        _checkedLabel.BackColor = Color.Transparent;

        _checkBtn.Enabled = _store != null;
        _checkBtn.Click += (_, _) => CheckRequested?.Invoke();

        card.Controls.Add(_checkedLabel);
        card.Controls.Add(_checkBtn);
    }

    // Rows are built at the list's width; posted, like TasksPage, once per resize burst.
    private void ReflowListsOnResize()
    {
        _todoList.Resize += OnListResize;
        _inProgressList.Resize += OnListResize;
        _newTicketsList.Resize += OnListResize;
    }

    private void OnListResize(object? sender, EventArgs e)
    {
        var changed = _todoList.ClientSize.Width != _todoRenderedWidth
            || _inProgressList.ClientSize.Width != _inProgressRenderedWidth
            || _newTicketsList.ClientSize.Width != _newTicketsRenderedWidth;
        if (!changed || !IsHandleCreated || _listsRenderPosted) return;
        _listsRenderPosted = true;
        BeginInvoke(() =>
        {
            _listsRenderPosted = false;
            if (IsDisposed) return;
            RenderLists();
            RenderTodos(force: true);
        });
    }

    // Fixed texts for the current language; header controls whose width follows the text are re-placed.
    private void ApplyTexts()
    {
        foreach (var (label, english, vietnamese) in _sectionLabels) label.Text = Lang.T(english, vietnamese);
        _queueTile.Title = Lang.T("QUEUE", "HÀNG ĐỢI");
        _tscTile.Title = Lang.T("TSC TODAY", "TSC HÔM NAY");
        _hrmTile.Title = Lang.T("HRM TODAY", "HRM HÔM NAY");
        _weekTile.Title = Lang.T("THIS WEEK", "TUẦN NÀY");

        _logQueueBtn.Text = Lang.T("Log queue now", "Log queue ngay");
        _recheckBtn.Text = Lang.T("Re-check", "Kiểm tra lại");
        LayoutCards();

        _allTicketsBtn.Text = Lang.T("All tickets", "Tất cả ticket");
        var allTicketsW = ButtonWidth(_allTicketsBtn);
        _allTicketsBtn.Bounds = new Rectangle(_allTicketsBtn.Parent!.ClientSize.Width - CardPad - allTicketsW, ToolbarY, allTicketsW, BtnH);

        _checkBtn.Text = Lang.T("Check", "Kiểm tra");
        var checkCard = _checkBtn.Parent!;
        var checkW = ButtonWidth(_checkBtn);
        _checkBtn.Bounds = new Rectangle(checkCard.ClientSize.Width - CardPad - checkW, ToolbarY, checkW, BtnH);
        var checkedLabelW = UiScale.S(220);
        var checkedLabelH = UiScale.S(18);
        _checkedLabel.Bounds = new Rectangle(_checkBtn.Left - UiScale.S(8) - checkedLabelW, ToolbarY + (BtnH - checkedLabelH) / 2, checkedLabelW, checkedLabelH);
        _checkedLabel.Text = CheckedText(TicketCheckInterval.Minutes, _checking, _checkedAt, _store != null);
    }

    private static int ButtonWidth(MacButton button)
        => ButtonWidths.ToolbarWidth(button.Font);

    private void ApplyColors()
    {
        BackColor = Theme.WindowBg;
        foreach (var (label, _, _) in _sectionLabels) label.ForeColor = Theme.TextSecondary;
        _todoTitle.ForeColor = Theme.TextSecondary;
        _todayDate.ForeColor = Theme.TextPrimary;
        _checkedLabel.ForeColor = Theme.TextSecondary;
        foreach (var list in new[] { _todoList, _inProgressList, _newTicketsList }) list.BackColor = Theme.CardSurface;
    }

    private void OnThemeChanged()
    {
        ApplyColors();
        Render();
        Invalidate(true);
    }

    private void OnLangChanged()
    {
        ApplyTexts();
        Render();
    }

    private void OnCheckIntervalChanged() => _checkedLabel.Text = CheckedText(TicketCheckInterval.Minutes, _checking, _checkedAt, _store != null);

    internal void SetChecking(bool checking, DateTime? checkedAt)
    {
        if (IsDisposed) return;
        _checking = checking;
        if (checkedAt.HasValue) _checkedAt = checkedAt;
        _checkBtn.Enabled = _store != null && !checking;
        _checkedLabel.Text = CheckedText(TicketCheckInterval.Minutes, _checking, _checkedAt, _store != null);
    }

    // The New card's header line: the running check, or the last one and how often it runs.
    internal static string CheckedText(int minutes, bool checking, DateTime? checkedAt, bool isTasksOn)
    {
        if (!isTasksOn) return string.Empty;
        if (checking) return Lang.T("Checking...", "Đang kiểm tra...");
        return checkedAt.HasValue
            ? Lang.T($"Last check {checkedAt.Value:HH:mm} · every {minutes} min", $"Check lần cuối {checkedAt.Value:HH:mm} · {minutes} phút/lần")
            : Lang.T($"Every {minutes} min", $"{minutes} phút/lần");
    }

    internal void SetDraining(bool draining)
    {
        if (IsDisposed) return;
        _draining = draining;
        UpdateLogQueueButton();
        RenderTodos();
    }

    private void UpdateLogQueueButton()
    {
        var queueCount = _data == null ? 0 : _data.QueueCount;
        _logQueueBtn.Enabled = queueCount != 0 && !_draining;
    }

    // UI thread only; TrayApp owns the busy state and re-seeds a rebuilt page.
    internal void SetTicketBusy(string ticketKey, StartStage? stage)
    {
        if (IsDisposed) return;
        if (stage.HasValue) _busyTickets[ticketKey] = stage.Value;
        else _busyTickets.Remove(ticketKey);
        RenderLists();
        RenderTodos(force: true);
    }

    // The sessions waiting for the user, from the Claude hook bridge.
    internal void SetClaudeWaits(IReadOnlyList<ClaudeWait> waits)
    {
        if (IsDisposed) return;
        _claudeWaits = waits;
        RenderTodos();
    }

    // Local data only (store, queue, worktree folders); a failed read keeps the last data on screen.
    internal void Reload()
    {
        if (IsDisposed) return;
        // Rebuilding every row is the expensive part; skip it while hidden and do it once on show.
        if (!Visible)
        {
            _reloadPending = true;
            return;
        }
        _reloadPending = false;
        try
        {
            _data = BuildData();
            _dataDay = Hcm.Today();
            _isTodayWritten = TscWrittenDates.Contains(_dataDay);
            var openTicketKeys = _openTicketKeys().ToHashSet();
            var devRepoPath = _devRepoPath();
            // A ticket with an open tab may still be in use; the cleanup dialog would only keep it.
            _cleanupKeys = _data.ShippedKeys
                .Where(key => !openTicketKeys.Contains(key) && Directory.Exists(TicketWorktrees.BackendPath(devRepoPath, key)))
                .ToList();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[dashboard] Reload failed: {ex.Message}");
        }
        Render();
    }

    private DashboardData BuildData()
    {
        var queue = TicketQueue.Read();
        var logTime = CurrentLogTime();
        if (_store == null)
        {
            return DashboardDataBuilder.Build(
                new Dictionary<string, JiraTicket>(), new Dictionary<string, JiraTicket>(), new Dictionary<string, JiraTicket>(),
                new Dictionary<string, TrackedPr>(), new Dictionary<string, PrCommentAlert>(),
                queue, Hcm.Now(), logTime);
        }
        return DashboardDataBuilder.Build(_store.LoadPending(), _store.LoadStarted(), _store.LoadDone(),
            _store.LoadPrTracking(), _store.LoadPrCommentAlerts(), queue, Hcm.Now(), logTime);
    }

    private TimeOnly CurrentLogTime() => _service?.LogTime ?? AppConfig.DefaultLogTime;

    private string LogTimeText() => CurrentLogTime().ToString("h:mm tt", CultureInfo.InvariantCulture);

    // Recomputed from the clock, not the snapshot's UntilLogTime, so the minute timer stays current.
    private TimeSpan? UntilLogTime()
    {
        var until = CurrentLogTime().ToTimeSpan() - Hcm.Now().TimeOfDay;
        return until > TimeSpan.Zero ? until : null;
    }

    private void Render()
    {
        RenderToday();
        RenderCoverage();
        RenderLists();
        RenderTodos(force: true);
    }

    // The date and the queue tile.
    private void RenderToday()
    {
        _todayDate.Text = Lang.LongDate(Hcm.Today().ToDateTime(TimeOnly.MinValue));
        UpdateLogQueueButton();

        var count = _data == null ? 0 : _data.TodayQueuedCount;
        var until = UntilLogTime();
        string left;
        if (until.HasValue)
        {
            var totalMinutes = (int)Math.Ceiling(until.Value.TotalMinutes);
            var hours = totalMinutes / 60;
            var minutes = totalMinutes % 60;
            left = hours != 0
                ? Lang.T($"in {hours}h {minutes}m", $"còn {hours}g {minutes}p")
                : Lang.T($"in {minutes}m", $"còn {minutes}p");
        }
        else
            left = Lang.T("past log time", "đã qua giờ log");

        _queueTile.SetValue(count != 0 ? Theme.Accent : Theme.TextSecondary,
            Lang.T($"{count} {(count == 1 ? "ticket" : "tickets")}", $"{count} ticket"),
            Lang.T($"auto-log {LogTimeText()} · {left}", $"tự log {LogTimeText()} · {left}"));
    }

    internal async void ReloadCoverage(bool allowSniff)
    {
        if (IsDisposed) return;
        if (_weekReading)
        {
            _rereadPending = true;
            _rereadPendingSniff |= allowSniff;
            return;
        }
        if (_service == null)
        {
            RenderCoverage();
            return;
        }

        _weekReading = true;
        _weekDay = Hcm.Today();
        _weekFailed = false;
        RenderCoverage();
        using var busyWork = BusyWork.Shared.Begin(Lang.T("Reading this week's logs...", "Đang đọc log tuần này..."));
        try
        {
            var week = await _service.CheckWeekAsync(WeekDates(_weekDay), AppLogger.Info, allowSniff: allowSniff);
            if (IsDisposed) return;
            _week = week.Count != 0 ? week : null;
            _weekFailed = week.Count == 0;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[dashboard] Reading this week's coverage failed: {ex.Message}");
            if (IsDisposed) return;
            _week = null;
            _weekFailed = true;
        }
        finally
        {
            _weekReading = false;
        }

        RenderCoverage();
        RenderTodos();
        if (!_rereadPending) return;
        var sniff = _rereadPendingSniff;
        _rereadPending = false;
        _rereadPendingSniff = false;
        ReloadCoverage(sniff);
    }

    // Monday to today; a weekend day only when it is today.
    private static List<DateOnly> WeekDates(DateOnly today)
    {
        var monday = Hcm.MondayOf(today);
        return Enumerable.Range(0, today.DayNumber - monday.DayNumber + 1)
            .Select(monday.AddDays)
            .Where(date => date == today || (date.DayOfWeek != DayOfWeek.Saturday && date.DayOfWeek != DayOfWeek.Sunday))
            .ToList();
    }

    // The TSC, HRM and week tiles.
    private void RenderCoverage()
    {
        string? noData = null;
        if (_service == null) noData = Lang.T("config not loaded", "chưa có cấu hình");
        // A re-read keeps the last week on screen rather than flashing "Reading..." over it.
        else if (_weekReading && _week == null) noData = Lang.T("Reading...", "Đang đọc...");
        else if (_weekFailed) noData = Lang.T("not read", "không đọc được");
        var today = _week?.FirstOrDefault(dc => dc.Date == Hcm.Today());
        if (noData != null || _week == null || today == null)
        {
            foreach (var tile in new[] { _tscTile, _hrmTile, _weekTile }) tile.SetValue(WeekCard.Gray, noData ?? "-", string.Empty);
            return;
        }

        var (tscColor, tscText) = TodayTscStatus(today);
        var (hrmColor, hrmText) = WeekCard.HrmStatus(today, future: false);
        var tscDetail = tscColor == WeekCard.Green && !today.IsOff ? Lang.T("written", "đã ghi") : string.Empty;
        var hrmDetail = string.Empty;
        if (!today.IsOff && today.HrmHours != null) hrmText += $" / {FullDayHours:0}h";
        // A weekend has nothing to log, and before HRM takes today's hours a short day is only waiting: neither is a miss.
        if (today.Date.DayOfWeek == DayOfWeek.Saturday || today.Date.DayOfWeek == DayOfWeek.Sunday)
        {
            tscColor = WeekCard.Gray;
            hrmColor = WeekCard.Gray;
            tscDetail = Lang.T("weekend", "cuối tuần");
            hrmDetail = tscDetail;
        }
        else if (!today.IsOff && today.HrmHours != null && today.HrmHours < FullDayHours
            && DashboardTodos.WeekStatus(new[] { today }, Hcm.Now(), CurrentLogTime()).Due.Count == 0)
        {
            hrmColor = WeekCard.Gray;
            hrmDetail = Lang.T($"logs at {LogTimeText()}", $"log lúc {LogTimeText()}");
        }
        _tscTile.SetValue(tscColor, tscText, tscDetail);
        _hrmTile.SetValue(hrmColor, hrmText, hrmDetail);

        var (due, unlogged) = DashboardTodos.WeekStatus(_week, Hcm.Now(), CurrentLogTime());
        if (due.Count == 0)
        {
            _weekTile.SetValue(WeekCard.Gray, "–", Lang.T("nothing due yet", "chưa có ngày cần log"));
            return;
        }
        var logged = due.Count - unlogged.Count;
        var missingDays = string.Join(", ", unlogged.Select(dc => dc.Date.ToString("ddd", Lang.Culture)));
        _weekTile.SetValue(unlogged.Count == 0 ? WeekCard.Green : WeekCard.Amber,
            Lang.T($"{logged}/{due.Count} days logged", $"{logged}/{due.Count} ngày đủ"),
            unlogged.Count == 0 ? Lang.T("all logged", "đã đủ") : Lang.T($"missing {missingDays}", $"thiếu {missingDays}"));
    }

    // WeekCard's TSC rule with two exceptions: a background read without a token, and an empty
    // cell that today's queued entry has not reached yet.
    private (Color Color, string Text) TodayTscStatus(DayCoverage coverage)
    {
        if (coverage.TscState == TscReadState.NoToken)
            return (WeekCard.Gray, Lang.T("not read (click Re-check)", "chưa đọc (bấm Kiểm tra lại)"));

        var queuedToday = _data != null && _data.TodayQueuedCount != 0;
        var waitingForLog = coverage.TscState == TscReadState.Read
            && string.IsNullOrWhiteSpace(coverage.TscTicket)
            && !coverage.IsOff
            && UntilLogTime().HasValue
            && queuedToday;
        if (waitingForLog)
            return (WeekCard.Gray, Lang.T($"waiting to log at {LogTimeText()}", $"chờ log lúc {LogTimeText()}"));

        return WeekCard.TscStatus(coverage, future: false);
    }

    private void RenderLists()
    {
        _inProgressRenderedWidth = _inProgressList.ClientSize.Width;
        _newTicketsRenderedWidth = _newTicketsList.ClientSize.Width;
        // Before the early returns: an empty or switched-off list still shows its titles.
        LayoutTableHeader(_inProgressHeader, _inProgressList, InProgressColumns);
        LayoutTableHeader(_newTicketsHeader, _newTicketsList, NewTicketColumns);

        if (_store == null)
        {
            var off = Lang.T("Tasks is off", "Tasks đang tắt");
            FillList(_inProgressList, Array.Empty<Control>(), off);
            FillList(_newTicketsList, Array.Empty<Control>(), off);
            return;
        }
        if (_data == null) return;

        // Every other row striped, counted per list.
        FillList(_inProgressList, _data.InProgress.Select((ip, index) => InProgressRow(ip.Ticket, ip.Prs, RowWidth(_inProgressList), index % 2 == 1)).ToList(),
            Lang.T("No tickets in progress", "Không có ticket đang làm"));
        FillList(_newTicketsList, _data.NewTickets.Select((jt, index) => NewTicketRow(jt, RowWidth(_newTicketsList), index % 2 == 1)).ToList(),
            Lang.T("No new tickets", "Không có ticket mới"));
    }

    private static int RowWidth(FlowLayoutPanel list) => list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - UiScale.S(2);

    private static void LayoutTableHeader(TaskTableHeader header, FlowLayoutPanel list, IReadOnlyList<TaskTableColumn> columns)
    {
        var rowWidth = Math.Max(0, RowWidth(list));
        header.Width = rowWidth;
        header.SetLayout(TicketTableRow.LayoutFor(rowWidth, columns));
    }

    // Keeps the scroll position; an empty list shows emptyText instead.
    private static void FillList(FlowLayoutPanel list, IReadOnlyList<Control> rows, string emptyText)
    {
        var scroll = list.AutoScrollPosition;
        list.SuspendLayout();
        var stale = list.Controls.Cast<Control>().ToArray();
        list.Controls.Clear();
        foreach (var control in stale) control.Dispose();

        if (rows.Count == 0)
        {
            list.Controls.Add(new Label
            {
                Text = emptyText,
                AutoSize = false,
                Size = new Size(Math.Max(UiScale.S(40), RowWidth(list)), UiScale.S(28)),
                Margin = new Padding(0),
                Padding = new Padding(UiScale.S(8), 0, 0, 0),
                TextAlign = ContentAlignment.MiddleLeft,
                Font = LabelFont,
                ForeColor = Theme.TextSecondary,
                BackColor = Color.Transparent,
            });
        }
        foreach (var row in rows) list.Controls.Add(row);
        list.ResumeLayout();
        list.AutoScrollPosition = new Point(-scroll.X, -scroll.Y);
    }

    // Deferred: the host's work ends in a Reload/SetTicketBusy that disposes the clicked button.
    private void RaiseTicketRequest(Func<string, Task>? request, string ticketKey, string actionName)
    {
        if (request == null || _busyTickets.ContainsKey(ticketKey)) return;
        BeginInvoke(new Action(async () =>
        {
            try
            {
                await request.Invoke(ticketKey);
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[dashboard] {actionName} {ticketKey} failed: {ex.Message}");
            }
        }));
    }

    private void RaiseTicketAction(Action<string>? action, string ticketKey)
    {
        if (action == null || _busyTickets.ContainsKey(ticketKey)) return;
        BeginInvoke(new Action(() => action.Invoke(ticketKey)));
    }

    // Deferred like the ticket requests: the action may rebuild the row that holds the clicked button.
    private void RaiseDeferred(Action action) => BeginInvoke(action);
}
