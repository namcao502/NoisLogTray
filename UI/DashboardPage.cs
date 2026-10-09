using System.Drawing;
using System.Globalization;

namespace NoisLogTray;

// Today at a glance: the queue and today's TSC/HRM coverage, tickets in progress, what needs
// attention, and new tickets. Draws local data and raises events; TrayApp does the work.
internal sealed partial class DashboardPage : UserControl, IShellPage
{
    // Logical (100%) px, like ShellLayout's card bounds; scaled where applied.
    private const int BuiltW = 600;
    private const int BuiltH = 640;
    private const int MinH = 14 + ShellLayout.DashboardTodayHeight + 3 * ShellLayout.DashboardCardMinHeight + 3 * 12 + 20;
    private static readonly int CardPad = UiScale.S(16);
    private static readonly int BtnH = UiScale.S(30);
    private static readonly int ToolbarY = UiScale.S(7);    // card-header button, centred on the section title
    private static readonly int LineY = UiScale.S(44);      // first Today line under the header
    private static readonly int LineH = UiScale.S(28);
    private static readonly int StatusX = UiScale.S(78);    // TSC/HRM status text, after the name and the dot
    private static readonly int ListTop = UiScale.S(40);
    private const double FullDayHours = 8.0;
    private DateOnly _dataDay;
    private DateOnly _coverageDay;

    private static readonly Color Red = Color.FromArgb(230, 76, 76);
    private static readonly Color Orange = Color.FromArgb(255, 140, 50);

    private static readonly Font DateFont = UiScale.Font("Segoe UI Semibold", 11F, FontStyle.Bold);
    private static readonly Font LabelFont = UiScale.Font("Segoe UI", 9F);
    private static readonly Font NameFont = UiScale.Font("Segoe UI", 9F, FontStyle.Bold);
    private static readonly Font SectionFont = UiScale.Font("Segoe UI", 8.5F, FontStyle.Bold);

    internal event Action? LogQueueRequested;
    internal event Action? CheckRequested;
    internal event Func<string, Task>? StartRequested;
    internal event Func<string, Task>? OpenRequested;
    internal event Func<string, Task>? ReadyToTestRequested;
    internal event Action<string>? QueueTicketRequested;
    internal event Action<string>? MarkDoneRequested;
    internal event Action<string>? CommentsSeen;
    internal event Action<ShellPage>? PageRequested;

    private readonly LoggingService? _service;
    private readonly TicketStore? _store;
    private readonly string _jiraBaseUrl;
    private readonly Dictionary<string, StartStage> _busyTickets = new();
    private readonly System.Windows.Forms.Timer _minuteTimer = new() { Interval = 60_000 };
    private readonly List<(Label Label, string English, string Vietnamese)> _sectionLabels = new();

    private DashboardData? _data;
    private DayCoverage? _coverage;
    private bool _coverageReading;
    private bool _coverageFailed;
    private bool _coverageLoaded; // read once per window show; OnHidden resets it
    private bool _reloadPending = true; // a Reload while hidden only marks this; the next show rebuilds once
    private bool _rereadPending;
    private bool _rereadPendingSniff;
    private bool _draining;
    private bool _checking;
    private DateTime? _checkedAt;
    private bool _listsRenderPosted;
    private int _inProgressRenderedWidth;
    private int _attentionRenderedWidth;
    private int _newTicketsRenderedWidth;

    // Placed by LayoutCards on every resize.
    private Card? _todayCard;
    private Card? _inProgressCard;
    private Card? _attentionCard;
    private Card? _newTicketsCard;

    private readonly Label _todayDate = new();
    private readonly MacButton _logQueueBtn = MacButton.Primary(string.Empty);
    private readonly Label _queueLine = new();
    private readonly Label _tscName = new();
    private readonly Panel _tscDot = new();
    private readonly Label _tscStatus = new();
    private readonly Label _hrmName = new();
    private readonly Panel _hrmDot = new();
    private readonly Label _hrmStatus = new();
    private readonly LinkLabel _recheckLink = new();
    private readonly LinkLabel _viewTasksLink = new();
    private readonly Label _checkedLabel = new();
    private readonly MacButton _checkBtn = MacButton.Secondary(string.Empty);
    private readonly FlowLayoutPanel _inProgressList = new();
    private readonly FlowLayoutPanel _attentionList = new();
    private readonly FlowLayoutPanel _newTicketsList = new();

    internal DashboardPage(LoggingService? service, TicketStore? store, string jiraBaseUrl)
    {
        _service = service;
        _store = store;
        _jiraBaseUrl = jiraBaseUrl;
        BuildLayout();
        _minuteTimer.Tick += (_, _) =>
        {
            // A page switch calls no hook, so a hidden page just skips the tick.
            if (!Visible) return;
            // Past midnight: yesterday's queue counts and coverage no longer apply. Coverage keeps its
            // own day because Reload runs from many places and would hide the rollover.
            var today = Hcm.Today();
            if (today != _dataDay) Reload();
            if (_coverageLoaded && !_coverageReading && _coverageDay != today) ReloadCoverage(allowSniff: false);
            RenderToday();
            RenderCoverage();
        };
        Theme.Changed += OnThemeChanged;
        Lang.Changed += OnLangChanged;
        ReflowListsOnResize();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnThemeChanged;
            Lang.Changed -= OnLangChanged;
            _minuteTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    internal bool CoverageLoaded => _coverageLoaded;

    public void OnPageShown()
    {
        if (_reloadPending || _dataDay != Hcm.Today()) Reload();
        else RenderToday();
        if (!_coverageLoaded)
        {
            _coverageLoaded = true;
            ReloadCoverage(allowSniff: false);
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
        _coverageLoaded = false;
    }

    private void BuildLayout()
    {
        BackColor = Theme.WindowBg;

        // Cards are built at the bounds the built Size lays them out at, so the anchors set
        // below record the right edge distances.
        var (today, inProgress, attention, newTickets) = ShellLayout.DashboardCardBounds(BuiltW, BuiltH);
        var todayCard = BuildTodayCard(UiScale.S(today));
        var inProgressCard = BuildListCard(UiScale.S(inProgress), "IN PROGRESS", "ĐANG LÀM", _inProgressList);
        var attentionCard = BuildListCard(UiScale.S(attention), "NEEDS ATTENTION", "CẦN CHÚ Ý", _attentionList);
        var newTicketsCard = BuildListCard(UiScale.S(newTickets), "NEW TICKETS", "TICKET MỚI", _newTicketsList);
        BuildAttentionHeader(attentionCard);
        BuildNewTicketsHeader(newTicketsCard);

        Controls.Add(todayCard);
        Controls.Add(inProgressCard);
        Controls.Add(attentionCard);
        Controls.Add(newTicketsCard);
        _todayCard = todayCard;
        _inProgressCard = inProgressCard;
        _attentionCard = attentionCard;
        _newTicketsCard = newTicketsCard;

        ApplyTexts();
        ApplyColors();

        // Sized before anchoring: WinForms records each anchor's edge distances when Anchor is set.
        Size = UiScale.S(new Size(BuiltW, BuiltH));
        MinimumSize = UiScale.S(new Size(BuiltW, MinH));
        AnchorForResize();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        LayoutCards();
    }

    private void LayoutCards()
    {
        if (_todayCard == null || _inProgressCard == null || _attentionCard == null || _newTicketsCard == null) return;

        // ShellLayout works in logical px: convert the page size in, scale the bounds back out.
        var (today, inProgress, attention, newTickets) = ShellLayout.DashboardCardBounds(
            UiScale.Logical(ClientSize.Width), UiScale.Logical(ClientSize.Height));
        SuspendLayout();
        _todayCard.Bounds = UiScale.S(today);
        _inProgressCard.Bounds = UiScale.S(inProgress);
        _attentionCard.Bounds = UiScale.S(attention);
        _newTicketsCard.Bounds = UiScale.S(newTickets);
        ResumeLayout();
    }

    private void AnchorForResize()
    {
        const AnchorStyles stretch = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        const AnchorStyles right = AnchorStyles.Top | AnchorStyles.Right;

        _todayDate.Anchor = stretch;
        _logQueueBtn.Anchor = right;
        _queueLine.Anchor = stretch;
        _tscStatus.Anchor = stretch;
        _hrmStatus.Anchor = stretch;
        _recheckLink.Anchor = right;
        _viewTasksLink.Anchor = right;
        _checkedLabel.Anchor = right;
        _checkBtn.Anchor = right;
        foreach (var list in new[] { _inProgressList, _attentionList, _newTicketsList })
            list.Anchor = stretch | AnchorStyles.Bottom;
    }

    private Card BuildTodayCard(Rectangle bounds)
    {
        var card = new Card { Bounds = bounds };
        var innerW = bounds.Width - 2 * CardPad;

        _todayDate.AutoSize = false;
        _todayDate.AutoEllipsis = true;
        _todayDate.Bounds = new Rectangle(CardPad, UiScale.S(10), innerW, UiScale.S(26)); // width set by ApplyTexts
        _todayDate.TextAlign = ContentAlignment.MiddleLeft;
        _todayDate.Font = DateFont;
        _todayDate.BackColor = Color.Transparent;

        _logQueueBtn.Click += (_, _) => LogQueueRequested?.Invoke();

        _queueLine.AutoSize = false;
        _queueLine.AutoEllipsis = true;
        _queueLine.Bounds = new Rectangle(CardPad, LineY, innerW, LineH);
        _queueLine.TextAlign = ContentAlignment.MiddleLeft;
        _queueLine.Font = LabelFont;
        _queueLine.BackColor = Color.Transparent;
        _queueLine.UseMnemonic = false;

        AddStatusLine(card, _tscName, _tscDot, _tscStatus, "TSC", LineY + LineH, bounds.Width - CardPad - StatusX);
        AddStatusLine(card, _hrmName, _hrmDot, _hrmStatus, "HRM", LineY + 2 * LineH, bounds.Width - CardPad - StatusX);

        _recheckLink.AutoSize = true;
        _recheckLink.Font = LabelFont;
        _recheckLink.LinkColor = Theme.Accent;
        _recheckLink.ActiveLinkColor = Theme.AccentHover;
        _recheckLink.LinkBehavior = LinkBehavior.HoverUnderline;
        _recheckLink.BackColor = Color.Transparent;
        _recheckLink.LinkClicked += (_, _) => ReloadCoverage(allowSniff: true);

        card.Controls.Add(_todayDate);
        card.Controls.Add(_logQueueBtn);
        card.Controls.Add(_queueLine);
        card.Controls.Add(_recheckLink);
        return card;
    }

    private static void AddStatusLine(Card card, Label name, Panel dot, Label status, string text, int y, int statusW)
    {
        name.Text = text;
        name.AutoSize = false;
        name.Bounds = new Rectangle(CardPad, y, UiScale.S(44), LineH);
        name.TextAlign = ContentAlignment.MiddleLeft;
        name.Font = NameFont;
        name.BackColor = Color.Transparent;

        var dotSize = UiScale.S(10);
        dot.Bounds = new Rectangle(StatusX - UiScale.S(18), y + (LineH - dotSize) / 2, dotSize, dotSize);

        status.AutoSize = false;
        status.AutoEllipsis = true;
        status.Bounds = new Rectangle(StatusX, y, statusW, LineH);
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.Font = LabelFont;
        status.BackColor = Color.Transparent;
        status.UseMnemonic = false;

        card.Controls.Add(name);
        card.Controls.Add(dot);
        card.Controls.Add(status);
    }

    private Card BuildListCard(Rectangle bounds, string english, string vietnamese, FlowLayoutPanel list)
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
        list.Bounds = new Rectangle(listInset, ListTop, bounds.Width - 2 * listInset, bounds.Height - ListTop - listInset);
        list.FlowDirection = FlowDirection.TopDown;
        list.WrapContents = false;
        list.AutoScroll = true;
        list.Padding = new Padding(0);
        card.Controls.Add(list);
        return card;
    }

    private void BuildAttentionHeader(Card card)
    {
        _viewTasksLink.AutoSize = true;
        _viewTasksLink.Font = LabelFont;
        _viewTasksLink.LinkColor = Theme.Accent;
        _viewTasksLink.ActiveLinkColor = Theme.AccentHover;
        _viewTasksLink.LinkBehavior = LinkBehavior.HoverUnderline;
        _viewTasksLink.BackColor = Color.Transparent;
        _viewTasksLink.Visible = _store != null; // no Tasks page to open when Tasks is off
        _viewTasksLink.LinkClicked += (_, _) => PageRequested?.Invoke(ShellPage.Tasks);
        card.Controls.Add(_viewTasksLink);
    }

    private void BuildNewTicketsHeader(Card card)
    {
        _checkedLabel.AutoSize = false;
        _checkedLabel.AutoEllipsis = true;
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
        _inProgressList.Resize += OnListResize;
        _attentionList.Resize += OnListResize;
        _newTicketsList.Resize += OnListResize;
    }

    private void OnListResize(object? sender, EventArgs e)
    {
        var changed = _inProgressList.ClientSize.Width != _inProgressRenderedWidth
            || _attentionList.ClientSize.Width != _attentionRenderedWidth
            || _newTicketsList.ClientSize.Width != _newTicketsRenderedWidth;
        if (!changed || !IsHandleCreated || _listsRenderPosted) return;
        _listsRenderPosted = true;
        BeginInvoke(() =>
        {
            _listsRenderPosted = false;
            if (IsDisposed) return;
            RenderLists();
        });
    }

    // Fixed texts for the current language; header controls whose width follows the text are re-placed.
    private void ApplyTexts()
    {
        foreach (var (label, english, vietnamese) in _sectionLabels) label.Text = Lang.T(english, vietnamese);

        _logQueueBtn.Text = Lang.T("Log queue now", "Log queue ngay");
        var logQueueW = ButtonWidth(_logQueueBtn);
        _logQueueBtn.Bounds = new Rectangle(_logQueueBtn.Parent!.ClientSize.Width - CardPad - logQueueW, ToolbarY, logQueueW, BtnH);
        _todayDate.Width = Math.Max(UiScale.S(40), _logQueueBtn.Left - UiScale.S(8) - CardPad);

        _recheckLink.Text = Lang.T("Re-check", "Kiểm tra lại");
        PlaceRightLink(_recheckLink, LineY + 2 * LineH, LineH);
        _hrmStatus.Width = Math.Max(UiScale.S(40), _recheckLink.Left - UiScale.S(8) - StatusX);
        _viewTasksLink.Text = Lang.T("View Tasks →", "Xem Tasks →");
        PlaceRightLink(_viewTasksLink, UiScale.S(10), UiScale.S(24));

        _checkBtn.Text = Lang.T("Check", "Kiểm tra");
        var checkCard = _checkBtn.Parent!;
        var checkW = ButtonWidth(_checkBtn);
        _checkBtn.Bounds = new Rectangle(checkCard.ClientSize.Width - CardPad - checkW, ToolbarY, checkW, BtnH);
        var checkedLabelW = UiScale.S(220);
        var checkedLabelH = UiScale.S(18);
        _checkedLabel.Bounds = new Rectangle(_checkBtn.Left - UiScale.S(8) - checkedLabelW, ToolbarY + (BtnH - checkedLabelH) / 2, checkedLabelW, checkedLabelH);
        _checkedLabel.Text = CheckedText();
    }

    private static void PlaceRightLink(LinkLabel link, int y, int lineH)
    {
        var card = link.Parent!;
        var size = link.PreferredSize;
        link.Location = new Point(card.ClientSize.Width - CardPad - size.Width, y + (lineH - size.Height) / 2);
    }

    private static int ButtonWidth(MacButton button)
        => ButtonWidths.ToolbarWidth(button.Font);

    private void ApplyColors()
    {
        BackColor = Theme.WindowBg;
        foreach (var (label, _, _) in _sectionLabels) label.ForeColor = Theme.TextSecondary;
        _todayDate.ForeColor = Theme.TextPrimary;
        _queueLine.ForeColor = Theme.TextPrimary;
        _tscName.ForeColor = Theme.TextSecondary;
        _hrmName.ForeColor = Theme.TextSecondary;
        _checkedLabel.ForeColor = Theme.TextSecondary;
        foreach (var list in new[] { _inProgressList, _attentionList, _newTicketsList }) list.BackColor = Theme.CardSurface;
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

    internal void SetChecking(bool checking, DateTime? checkedAt)
    {
        if (IsDisposed) return;
        _checking = checking;
        if (checkedAt.HasValue) _checkedAt = checkedAt;
        _checkBtn.Enabled = _store != null && !checking;
        _checkedLabel.Text = CheckedText();
    }

    private string CheckedText()
    {
        if (_store == null) return string.Empty;
        if (_checking) return Lang.T("Checking...", "Đang kiểm tra...");
        return _checkedAt.HasValue
            ? Lang.T($"Last check {_checkedAt.Value:HH:mm} · every 5 min", $"Check lần cuối {_checkedAt.Value:HH:mm} · 5 phút/lần")
            : Lang.T("Every 5 min", "5 phút/lần");
    }

    internal void SetDraining(bool draining)
    {
        if (IsDisposed) return;
        _draining = draining;
        UpdateLogQueueButton();
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
    }

    // Local data only (store + queue); a failed read keeps the last data on screen.
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
    }

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

        _queueLine.Text = Lang.T(
            $"{count} {(count == 1 ? "ticket" : "tickets")} queued for today -- auto-log at {LogTimeText()} ({left})",
            $"{count} ticket trong queue -- tự log lúc {LogTimeText()} ({left})");
    }

    internal async void ReloadCoverage(bool allowSniff)
    {
        if (IsDisposed) return;
        if (_coverageReading)
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

        _coverageReading = true;
        _coverageDay = Hcm.Today();
        _coverageFailed = false;
        RenderCoverage();
        try
        {
            var coverage = await _service.CheckWeekAsync(new[] { Hcm.Today() }, AppLogger.Info, allowSniff: allowSniff);
            if (IsDisposed) return;
            _coverage = coverage.Count != 0 ? coverage[0] : null;
            _coverageFailed = coverage.Count == 0;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[dashboard] Reading today's coverage failed: {ex.Message}");
            if (IsDisposed) return;
            _coverage = null;
            _coverageFailed = true;
        }
        finally
        {
            _coverageReading = false;
        }

        RenderCoverage();
        if (!_rereadPending) return;
        var sniff = _rereadPendingSniff;
        _rereadPending = false;
        _rereadPendingSniff = false;
        ReloadCoverage(sniff);
    }

    private void RenderCoverage()
    {
        if (_service == null)
        {
            var noConfig = Lang.T("config not loaded", "chưa có cấu hình");
            SetStatus(_tscDot, _tscStatus, WeekPage.Gray, noConfig);
            SetStatus(_hrmDot, _hrmStatus, WeekPage.Gray, noConfig);
            return;
        }
        if (_coverageReading)
        {
            var reading = Lang.T("Reading...", "Đang đọc...");
            SetStatus(_tscDot, _tscStatus, WeekPage.Gray, reading);
            SetStatus(_hrmDot, _hrmStatus, WeekPage.Gray, reading);
            return;
        }
        if (_coverageFailed || _coverage == null)
        {
            var text = _coverageFailed ? Lang.T("not read", "không đọc được") : "-";
            SetStatus(_tscDot, _tscStatus, WeekPage.Gray, text);
            SetStatus(_hrmDot, _hrmStatus, WeekPage.Gray, text);
            return;
        }

        var (tscColor, tscText) = TodayTscStatus(_coverage);
        SetStatus(_tscDot, _tscStatus, tscColor, tscText);

        var (hrmColor, hrmText) = WeekPage.HrmStatus(_coverage, future: false);
        if (!_coverage.IsOff && _coverage.HrmHours != null)
            hrmText += $" / {FullDayHours:0}h";
        SetStatus(_hrmDot, _hrmStatus, hrmColor, hrmText);
    }

    // WeekPage's TSC rule with two exceptions: a background read without a token, and an empty
    // cell that today's queued entry has not reached yet.
    private (Color Color, string Text) TodayTscStatus(DayCoverage coverage)
    {
        if (coverage.TscState == TscReadState.NoToken)
            return (WeekPage.Gray, Lang.T("not read (click Re-check)", "chưa đọc (bấm Kiểm tra lại)"));

        var queuedToday = _data != null && _data.TodayQueuedCount != 0;
        var waitingForLog = coverage.TscState == TscReadState.Read
            && string.IsNullOrWhiteSpace(coverage.TscTicket)
            && !coverage.IsOff
            && UntilLogTime().HasValue
            && queuedToday;
        if (waitingForLog)
            return (WeekPage.Gray, Lang.T($"waiting to log at {LogTimeText()}", $"chờ log lúc {LogTimeText()}"));

        return WeekPage.TscStatus(coverage, future: false);
    }

    private static void SetStatus(Panel dot, Label status, Color color, string text)
    {
        dot.BackColor = color;
        status.ForeColor = color;
        status.Text = text;
    }

    private void RenderLists()
    {
        _inProgressRenderedWidth = _inProgressList.ClientSize.Width;
        _attentionRenderedWidth = _attentionList.ClientSize.Width;
        _newTicketsRenderedWidth = _newTicketsList.ClientSize.Width;

        if (_store == null)
        {
            var off = Lang.T("Tasks is off", "Tasks đang tắt");
            FillList(_inProgressList, Array.Empty<DashboardRow>(), off);
            FillList(_attentionList, Array.Empty<DashboardRow>(), off);
            FillList(_newTicketsList, Array.Empty<DashboardRow>(), off);
            return;
        }
        if (_data == null) return;

        FillList(_inProgressList, _data.InProgress.Select(ip => InProgressRow(ip.Ticket, ip.PrSummary, RowWidth(_inProgressList))).ToList(),
            Lang.T("No tickets in progress", "Không có ticket đang làm"));
        FillList(_attentionList, _data.Attention.Select(ai => AttentionRow(ai, RowWidth(_attentionList))).ToList(),
            Lang.T("Nothing needs attention", "Không có gì cần xử lý"));
        FillList(_newTicketsList, _data.NewTickets.Select(jt => NewTicketRow(jt, RowWidth(_newTicketsList))).ToList(),
            Lang.T("No new tickets", "Không có ticket mới"));
    }

    private static int RowWidth(FlowLayoutPanel list) => list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - UiScale.S(2);

    // Keeps the scroll position; an empty list shows emptyText instead.
    private static void FillList(FlowLayoutPanel list, IReadOnlyList<DashboardRow> rows, string emptyText)
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
}
