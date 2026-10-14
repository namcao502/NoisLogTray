using System.Drawing;
using System.Globalization;

namespace NoisLogTray;

// The log column of the Work page: three cards -- "New entry" (date/ticket + actions), "Will log" (preview + queue)
// and the week (WeekCard). The board beside it puts tickets into New entry (AddTicketToInput).
internal sealed class LogPage : UserControl, IShellPage
{
    private static readonly Font TicketsFont = UiScale.Font("Segoe UI", 9.5F);
    private static readonly Font HoursHintFont = UiScale.Font("Segoe UI", 8.5F, FontStyle.Bold);
    private static readonly Font SectionFont = UiScale.Font("Segoe UI", 8.5F, FontStyle.Bold);
    private static readonly Font WillLogFont = UiScale.Font("Segoe UI", 8.5F, FontStyle.Bold);

    // Per-ticket accent colours, tuned per theme so the key text stays readable:
    // bright shades on dark, deeper shades on light. Same index = same hue.
    private static readonly Color[] TicketDark =
    {
        Color.FromArgb(88, 166, 255),   // blue
        Color.FromArgb(63, 185, 80),    // green
        Color.FromArgb(255, 157, 92),   // orange
        Color.FromArgb(188, 140, 255),  // violet
        Color.FromArgb(247, 120, 186),  // pink
        Color.FromArgb(57, 197, 207),   // teal
        Color.FromArgb(255, 123, 114),  // red
        Color.FromArgb(227, 179, 65),   // amber
    };

    private static readonly Color[] TicketLight =
    {
        Color.FromArgb(37, 99, 235),    // blue
        Color.FromArgb(21, 128, 61),    // green
        Color.FromArgb(194, 65, 12),    // orange
        Color.FromArgb(124, 58, 237),   // violet
        Color.FromArgb(190, 24, 93),    // pink
        Color.FromArgb(14, 116, 144),   // teal
        Color.FromArgb(220, 38, 38),    // red
        Color.FromArgb(180, 83, 9),     // amber
    };

    // Logical (100%) px, since ShellLayout's column widths are built from it; scale before use. As narrow as the week card's
    // header and the New entry row allow, so the board beside it gets the rest.
    internal const int CardW = 460;

    // One height for every button and input; widths vary by role (the primary button is
    // widest, card-header toolbar buttons share ToolbarBtnW). Neighbours sit 8px apart.
    private static readonly int BtnH = UiScale.S(30);
    private static readonly int ToolbarBtnW = UiScale.S(112);
    private static readonly int BtnGap = UiScale.S(8);
    private static readonly int ToolbarY = UiScale.S(7);   // card-header toolbar, centred on the section title
    private static readonly int CardBodyY = UiScale.S(44); // first content row under a card header
    private static readonly int CardPad = UiScale.S(16);   // a card's inner left/right padding
    private static readonly int InnerW = UiScale.S(CardW - 32);
    // Fixed height: a date header + 3 (editable, tallest) ticket rows + list padding;
    // longer lists scroll internally.
    private const int WillLogHostHLogical = 20 + 3 * 26 + 8;
    private static readonly int WillLogHostH = UiScale.S(WillLogHostHLogical);

    // Built card heights in logical px, like the rest of the ShellLayout bookkeeping.
    private const int NewEntryCardH = 96 + 30 + 16;
    private const int WillLogCardH = 44 + WillLogHostHLogical + 12;

    private readonly LoggingService? _service;

    private readonly Panel _body = new();
    private readonly List<Label> _sectionLabels = new();
    private readonly List<Panel> _dividers = new();

    private readonly RoundedDatePicker _date = new();
    private readonly ThemedToolTip _tips = new();
    private readonly TextBox _tickets = new();
    private readonly MacButton _queueBtn = MacButton.Primary("");
    private readonly MacButton _logNowBtn = MacButton.Secondary("");
    private readonly MacButton _moreBtn = MacButton.Secondary("");

    // Rarely used actions live in dropdowns so "Add to queue" stays the one obvious button.
    private readonly ContextMenuStrip _logNowMenu = ThemedMenuRenderer.CreateMenu();
    private readonly ToolStripMenuItem _logBothItem = new();
    private readonly ToolStripMenuItem _logTscItem = new();
    private readonly ToolStripMenuItem _logHrmItem = new();
    private readonly ContextMenuStrip _moreMenu = ThemedMenuRenderer.CreateMenu();
    private readonly ToolStripMenuItem _logOffItem = new();
    private readonly ToolStripMenuItem _checkItem = new();
    private readonly ToolStripMenuItem _reauthItem = new();
    private readonly MacButton _clearBtn = MacButton.Secondary("");
    private readonly FlowLayoutPanel _willLogList = new();
    private readonly System.Windows.Forms.Timer _verifyTimer = new() { Interval = 500 };
    private readonly Dictionary<string, (VState State, string? Title)> _verify = new();
    private readonly MacButton _clearQueueBtn = MacButton.Secondary("");
    private readonly MacButton _logAllBtn = MacButton.Secondary("");
    private readonly Label _hoursHint = new();

    // Per typed-ticket HRM minutes while composing an entry. null = the default even
    // split (see TimeSlots.EvenSplit); reset whenever the ticket text changes.
    private List<int>? _typedMinutes;

    private bool _busy;
    private IDisposable? _busyWork; // the running action, in the header spinner

    // Section labels with both texts, so ApplyLanguage can re-label them.
    private readonly List<(Label Label, string English, string Vietnamese)> _sectionLabelTexts = new();

    private enum VState { Verifying, Valid, NotFound, Error }

    internal event Action? QueueChanged;

    // Raised after a successful TSC re-auth so the tray can retry any due queue entries.
    internal event Action? ReauthSucceeded;

    // Raised by "Log queue now" so the tray drains the whole queue through its guarded path.
    internal event Action? DrainRequested;

    // The running action's sticky toast text, or null when it ends; the shell shows it.
    internal event Action<string?>? BusyChanged;

    // Raised with each action's result so the tray shows it as a popup (ok = success).
    internal event Action<string, NoticeKind>? StatusRaised;

    // Raised with the typed tickets on every edit of the ticket box, so the board marks them "In entry".
    internal event Action<IReadOnlyList<string>>? EntryTicketsChanged;

    internal LogPage(LoggingService? service, string? configError)
    {
        _service = service;
        BuildLayout();
        ApplyLanguage();
        Theme.Changed += ApplyTheme;
        Lang.Changed += ApplyLanguage;
        RefreshQueuedView(); // also renders the Will log (falls back to the queue)
        if (configError != null) AppendLog($"[config] {configError}");
        ReflowRowsOnResize();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= ApplyTheme;
            Lang.Changed -= ApplyLanguage;
            _busyWork?.Dispose();
            _verifyTimer.Stop(); // OnHidden's Clear() can arm it right before the shell is disposed
            _verifyTimer.Dispose();
            _tips.Dispose();
            _logNowMenu.Dispose();
            _moreMenu.Dispose();
        }
        base.Dispose(disposing);
    }

    // Placed by LayoutCards on every resize; the heights are the built ones, in logical px.
    private Card? _newEntryCard;
    private Card? _willLogCard;
    private WeekCard? _weekCard;
    private int _newEntryH;
    private int _willLogH;
    private int _weekH;
    private int _cardLeft = 20;

    // A Queue rebuild deferred while an hours box has focus, so a half-typed value survives.
    private bool _willLogReflowPending;

    private void BuildLayout()
    {
        BackColor = Theme.WindowBg;
        _body.Dock = DockStyle.Fill;
        _body.BackColor = Theme.WindowBg;
        _body.AutoScroll = true; // a column shorter than the built cards scrolls
        _body.AutoScrollMargin = UiScale.S(new Size(0, 14)); // the top margin, kept under the last card too

        // Summed in logical px and scaled once, exactly as LayoutCards scales ShellLayout's bounds.
        const int left = 20;
        var y = 14;

        // Input + primary action first (where focus lands), the queue next, the week last.
        var newEntry = BuildNewEntryCard();
        newEntry.Location = UiScale.S(new Point(left, y));
        _body.Controls.Add(newEntry);
        _newEntryCard = newEntry;
        _newEntryH = NewEntryCardH;
        y += NewEntryCardH + 12;

        var willLog = BuildWillLogCard();
        willLog.Location = UiScale.S(new Point(left, y));
        _body.Controls.Add(willLog);
        _willLogCard = willLog;
        _willLogH = WillLogCardH;
        y += WillLogCardH + 12;

        var week = new WeekCard(_service);
        week.Location = UiScale.S(new Point(left, y));
        week.DayRequested += PrepareForDate;
        _body.Controls.Add(week);
        _weekCard = week;
        _weekH = WeekCard.BuiltHeight;
        var builtH = y + WeekCard.BuiltHeight + 20;

        // Cards and heights are stored above so the OnSizeChanged this Size triggers reproduces the built
        // bounds (LayoutCards takes the overflow off again); anchoring (below) must come after it.
        // No MinimumSize: the Work page sizes the column, and a short one scrolls.
        Size = new Size(UiScale.S(CardW + 40) + ScrollBarOverflow, UiScale.S(builtH));
        Controls.Add(_body);
        ThemedScrollBar.Attach(_body);

        AnchorForResize();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        LayoutCards();
    }

    // How far the vertical scroll bar reaches past the 20px right margin (plus 1px for rounding): usually 0.
    private int ScrollBarOverflow => Math.Max(0, SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi) + 1 - UiScale.S(20));

    // Child bounds are relative to the scrolled view, so a card is placed at its layout bounds plus the scroll.
    private static Rectangle Scrolled(Rectangle bounds, Point scroll) =>
        new(bounds.X + scroll.X, bounds.Y + scroll.Y, bounds.Width, bounds.Height);

    // The built height of the column in logical px: the Work page gives it this much when it stacks it over the board.
    internal static int NaturalHeight => ShellLayout.LogNaturalHeight(NewEntryCardH, WillLogCardH, WeekCard.BuiltHeight);

    // Beside the board, its right margin is the gap and a short column scrolls itself. Stacked over it, the column
    // has its natural height and a 20px left margin, and the page scrolls instead.
    internal void SetStacked(bool stacked)
    {
        var cardLeft = stacked ? 20 : 0;
        if (_cardLeft == cardLeft && _body.AutoScroll != stacked) return;
        _cardLeft = cardLeft;
        if (stacked) _body.AutoScrollPosition = Point.Empty;
        _body.AutoScroll = !stacked;
        LayoutCards();
    }

    // The cards stack in one column; the Queue takes any extra height. Only Bounds change, so nothing is rebuilt and
    // typed text stays.
    private void LayoutCards()
    {
        if (_newEntryCard == null || _willLogCard == null || _weekCard == null
            || _newEntryH == 0 || _willLogH == 0 || _weekH == 0) return;

        // ShellLayout works in logical px: convert the page size in, scale the bounds back out.
        // The column may scroll, so its cards stay clear of the scroll bar.
        var (newEntry, queue, week) = ShellLayout.LogCardBounds(UiScale.Logical(ClientSize.Width - ScrollBarOverflow),
            UiScale.Logical(ClientSize.Height), _cardLeft, _newEntryH, _willLogH, _weekH);
        var scroll = _body.AutoScrollPosition;
        _body.SuspendLayout();
        _newEntryCard.Bounds = Scrolled(UiScale.S(newEntry), scroll);
        _willLogCard.Bounds = Scrolled(UiScale.S(queue), scroll);
        _weekCard.Bounds = Scrolled(UiScale.S(week), scroll);
        _body.ResumeLayout();
    }

    // Set after _body is docked at its final size: WinForms records each anchor's edge
    // distances when Anchor is assigned, so the built layout is what a resize preserves.
    // Cards are placed by LayoutCards; inside them inputs stretch with the width,
    // right-hand buttons stay right, and the Queue list takes extra height.
    private void AnchorForResize()
    {
        const AnchorStyles stretch = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        const AnchorStyles right = AnchorStyles.Top | AnchorStyles.Right;

        _tickets.Parent!.Anchor = stretch;
        _tickets.Anchor = stretch;
        _clearBtn.Anchor = right;
        _queueBtn.Anchor = stretch;
        _logNowBtn.Anchor = right;
        _moreBtn.Anchor = right;

        _hoursHint.Anchor = right;
        _clearQueueBtn.Anchor = right;
        _logAllBtn.Anchor = right;
        _willLogList.Parent!.Anchor = stretch | AnchorStyles.Bottom;
    }

    // Rows are built at the list's width, so a width change rebuilds them. Hooked after
    // the first render so construction-time layout passes don't re-render early.
    // The rebuild is posted: run inside the window's layout pass, the list keeps the old
    // wider scroll extent after a shrink and shows a stray horizontal scrollbar.
    private void ReflowRowsOnResize()
    {
        var willLogWidth = _willLogList.Width;
        _willLogList.Resize += (_, _) =>
        {
            if (_willLogList.Width == willLogWidth || !IsHandleCreated) return;
            willLogWidth = _willLogList.Width;
            BeginInvoke(() =>
            {
                if (_willLogList.ContainsFocus) _willLogReflowPending = true;
                else UpdateWillLog();
            });
        };
        _willLogList.Leave += (_, _) =>
        {
            if (!_willLogReflowPending) return;
            _willLogReflowPending = false;
            UpdateWillLog();
        };
    }

    private Card BuildWillLogCard()
    {
        var card = new Card { Size = UiScale.S(new Size(CardW, WillLogCardH)) };
        card.Controls.Add(SectionLabel("QUEUE", "HÀNG ĐỢI", 16, 14));

        // Running total / validation hint, right-aligned in the header (typed view only).
        _hoursHint.AutoSize = false;
        var hintW = UiScale.S(170);
        var hintH = UiScale.S(18);
        _hoursHint.Size = new Size(hintW, hintH);
        _hoursHint.Location = new Point(CardPad + InnerW - hintW, ToolbarY + (BtnH - hintH) / 2);
        _hoursHint.TextAlign = ContentAlignment.MiddleRight;
        _hoursHint.Font = HoursHintFont;
        _hoursHint.ForeColor = Theme.TextSecondary;
        _hoursHint.BackColor = Color.Transparent;
        _hoursHint.Visible = false;
        card.Controls.Add(_hoursHint);

        // Clears the persisted queue; only shown while the card is displaying the
        // queued fallback (input empty + something queued).
        _clearQueueBtn.Size = new Size(ToolbarBtnW, BtnH);
        _clearQueueBtn.Location = new Point(CardPad + InnerW - ToolbarBtnW, ToolbarY);
        _clearQueueBtn.Visible = false;
        _clearQueueBtn.Click += OnClearQueue;
        card.Controls.Add(_clearQueueBtn);

        // Logs the whole queued list now (same guarded drain as the tray "Log queue now");
        // shown beside Clear queue, only in the queued fallback view.
        _logAllBtn.Size = new Size(ToolbarBtnW, BtnH);
        _logAllBtn.Location = new Point(CardPad + InnerW - 2 * ToolbarBtnW - BtnGap, ToolbarY);
        _logAllBtn.Visible = false;
        _logAllBtn.Click += OnLogAllNow;
        card.Controls.Add(_logAllBtn);

        // Fixed-height list; rows scroll internally once they overflow so the Actions
        // card below stays at a stable, visible position.
        var host = new RoundedHost { Location = new Point(CardPad, CardBodyY), Size = new Size(InnerW, WillLogHostH) };
        _willLogList.Dock = DockStyle.Fill;
        _willLogList.FlowDirection = FlowDirection.TopDown;
        _willLogList.WrapContents = false;
        _willLogList.AutoScroll = true;
        _willLogList.BackColor = Theme.InputBg;
        _willLogList.Padding = UiScale.S(new Padding(6, 4, 6, 4));
        host.Controls.Add(_willLogList);
        ThemedScrollBar.Attach(_willLogList);

        _verifyTimer.Tick += (_, _) => { _verifyTimer.Stop(); VerifyTicketsAsync(); };

        card.Controls.Add(host);
        return card;
    }

    private Card BuildNewEntryCard()
    {
        var card = new Card { Size = UiScale.S(new Size(CardW, NewEntryCardH)) };
        card.Controls.Add(SectionLabel("NEW ENTRY", "NHẬP MỚI", 16, 14));

        card.Controls.Add(SectionLabel("DATE", "NGÀY", 16, 40));
        card.Controls.Add(SectionLabel("TICKET", "TICKET", 164, 40));

        var fieldY = UiScale.S(58);
        _date.Location = new Point(CardPad, fieldY);
        _date.Size = new Size(UiScale.S(140), BtnH);
        _date.ValueChanged += (_, _) => { UpdateWillLog(); VerifyTicketsAsync(); };

        var ticketHost = new RoundedHost { Location = new Point(UiScale.S(164), fieldY), Size = new Size(UiScale.S(212), BtnH) };
        _tickets.BorderStyle = BorderStyle.None;
        _tickets.Font = TicketsFont;
        _tickets.BackColor = Theme.InputBg;
        _tickets.ForeColor = Theme.TextPrimary;
        _tickets.TextChanged += (_, _) =>
        {
            _typedMinutes = null;
            UpdateWillLog();
            UpdateActionState();
            _verifyTimer.Stop();
            _verifyTimer.Start();
            EntryTicketsChanged?.Invoke(TicketParser.Parse(_tickets.Text).Tickets);
        };
        _tickets.Leave += (_, _) => { _verifyTimer.Stop(); VerifyTicketsAsync(); };
        // Enter queues only from the ticket box, and only when Add to queue is enabled.
        _tickets.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            if (_queueBtn.Enabled) OnQueue(this, EventArgs.Empty);
            e.SuppressKeyPress = true;
        };
        var ticketH = _tickets.PreferredHeight;
        _tickets.SetBounds(UiScale.S(10), (ticketHost.Height - ticketH) / 2, ticketHost.Width - UiScale.S(20), ticketH);
        ticketHost.Controls.Add(_tickets);

        var clearX = ticketHost.Right + BtnGap; // 376 + 8 at 100%
        _clearBtn.Size = new Size(CardPad + InnerW - clearX, BtnH); // fills to the card edge
        _clearBtn.Location = new Point(clearX, fieldY);
        _clearBtn.Click += (_, _) => _tickets.Text = string.Empty;

        card.Controls.Add(_date);
        card.Controls.Add(ticketHost);
        card.Controls.Add(_clearBtn);

        // One row: the primary "Add to queue", then the "Log now" and "more" dropdowns.
        var logNowW = UiScale.S(140);
        var moreW = UiScale.S(52);
        var queueW = InnerW - logNowW - moreW - 2 * BtnGap;
        var actionY = UiScale.S(96);

        _queueBtn.Size = new Size(queueW, BtnH);
        _queueBtn.Location = new Point(CardPad, actionY);
        _queueBtn.Click += OnQueue;

        _logNowBtn.Size = new Size(logNowW, BtnH);
        _logNowBtn.Glyph = Icons.ChevronDown;
        _logNowBtn.GlyphAfterText = true;
        _logNowBtn.Location = new Point(CardPad + queueW + BtnGap, actionY);
        _logNowBtn.Click += (_, _) => ThemedMenuRenderer.ShowBelow(_logNowMenu, _logNowBtn);

        _moreBtn.Size = new Size(moreW, BtnH);
        _moreBtn.Glyph = Icons.More;
        _moreBtn.GlyphFont = Icons.Medium;
        _moreBtn.Location = new Point(CardPad + InnerW - moreW, actionY);
        _moreBtn.Click += (_, _) => ThemedMenuRenderer.ShowBelow(_moreMenu, _moreBtn);

        _logBothItem.Click += OnLogNow;
        _logTscItem.Click += OnLogTsc;
        _logHrmItem.Click += OnLogHrm;
        _logNowMenu.Items.AddRange(new ToolStripItem[] { _logBothItem, _logTscItem, _logHrmItem });
        _logNowMenu.Opening += (_, _) => UpdateLogNowMenu();

        _logOffItem.Click += OnLogOff;
        _checkItem.Click += OnCheckTsc;
        _reauthItem.Click += OnReauth;
        _moreMenu.Items.AddRange(new ToolStripItem[] { _logOffItem, new ToolStripSeparator(), _checkItem, _reauthItem });

        card.Controls.Add(_queueBtn);
        card.Controls.Add(_logNowBtn);
        card.Controls.Add(_moreBtn);
        return card;
    }

    private Label SectionLabel(string english, string vietnamese, int x, int y)
    {
        var label = new Label
        {
            Text = Lang.T(english, vietnamese),
            AutoSize = true,
            Location = UiScale.S(new Point(x, y)), // x, y are logical
            Font = SectionFont,
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
        };
        _sectionLabels.Add(label);
        _sectionLabelTexts.Add((label, english, vietnamese));
        return label;
    }

    // Set every fixed text for the current language, then re-render the dynamic lists.
    // Runs once after the layout is built and again on each Lang.Changed.
    private void ApplyLanguage()
    {
        foreach (var (label, english, vietnamese) in _sectionLabelTexts) label.Text = Lang.T(english, vietnamese);

        _queueBtn.Text = Lang.T("Add to queue", "Thêm vào hàng đợi");
        _logNowBtn.Text = _busy ? BusyText : LogNowText;
        _logNowBtn.AccessibleName = Lang.T("Log now menu", "Menu log ngay");
        _moreBtn.AccessibleName = Lang.T("More actions", "Thao tác khác");
        _clearBtn.Text = Lang.T("Clear", "Xóa");
        _clearQueueBtn.Text = Lang.T("Clear queue", "Xóa hàng đợi");
        _logAllBtn.Text = Lang.T("Log queue now", "Log cả hàng đợi");

        _logTscItem.Text = Lang.T("TSC only", "Chỉ TSC");
        UpdateLogNowMenu(); // TSC + HRM / HRM only carry the 6 PM suffix
        _logOffItem.Text = Lang.T("Log OFF...", "Đánh dấu nghỉ (OFF)...");
        _logOffItem.ToolTipText = Lang.T(
            $"Write \"{TscCells.OffMarker}\" on a yellow background to TSC for the selected date (no HRM hours).",
            $"Ghi \"{TscCells.OffMarker}\" nền vàng vào TSC cho ngày đã chọn (không ghi giờ HRM).");
        _checkItem.Text = Lang.T("Check TSC session", "Kiểm tra phiên TSC");
        _reauthItem.Text = Lang.T("Re-authenticate TSC", "Đăng nhập lại TSC");

        _tickets.PlaceholderText = Lang.T("e.g. 1234, 5678  (MDP- optional)", "vd. 1234, 5678  (không cần MDP-)");

        _tips.SetToolTip(_date, Lang.T(
            "Dates and the daily auto-log use Vietnam time (Asia/Ho_Chi_Minh, UTC+7).",
            "Ngày và giờ tự log hằng ngày theo giờ Việt Nam (Asia/Ho_Chi_Minh, UTC+7)."));
        _tips.SetToolTip(_logNowBtn, Lang.T(
            "Log the typed tickets right away instead of waiting for the scheduled run.",
            "Log ngay các ticket đã nhập thay vì chờ tới giờ tự log."));
        _tips.SetToolTip(_moreBtn, Lang.T("More actions: Log OFF, TSC session", "Thao tác khác: đánh dấu nghỉ, phiên TSC"));

        UpdateWillLog();
        FitToolbarButtons();
    }

    // One fixed width for every toolbar button (sized for the longest label, both languages, at the
    // current DPI), keeping the toolbar's right edge.
    private void FitToolbarButtons()
    {
        FitButtonLeftOf(_clearQueueBtn, _clearQueueBtn.Right);
        FitButtonLeftOf(_logAllBtn, _clearQueueBtn.Left - BtnGap);
    }

    private static void FitButtonLeftOf(MacButton button, int right)
    {
        var width = ButtonWidths.ToolbarWidth(button.Font);
        button.SetBounds(right - width, button.Top, width, button.Height);
    }

    private static string LogNowText => Lang.T("Log now", "Log ngay");
    private static string BusyText => Lang.T("Working...", "Đang chạy...");

    // Re-apply theme colors to the native controls (custom controls repaint
    // themselves via Theme.Changed) and re-render the Queue rows.
    private void ApplyTheme()
    {
        BackColor = Theme.WindowBg;
        _body.BackColor = Theme.WindowBg;

        foreach (var l in _sectionLabels) l.ForeColor = Theme.TextSecondary;
        foreach (var d in _dividers) d.BackColor = Theme.Divider;

        _tickets.BackColor = Theme.InputBg;
        _tickets.ForeColor = Theme.TextPrimary;
        _willLogList.BackColor = Theme.InputBg;

        UpdateWillLog();
        Invalidate(true);
    }

    // Detailed activity goes to the log file only. Safe from any thread.
    private static void AppendLog(string line) => AppLogger.Info(line);

    // An action's result: logged, then raised so the tray routes it (in-window notice
    // when the shell is the foreground window, Windows balloon otherwise).
    private void ShowStatus(string message, bool ok)
    {
        AppLogger.Write(ok ? "INFO" : "ERROR", message);
        StatusRaised?.Invoke(message, ok ? NoticeKind.Success : NoticeKind.Error);
    }

    // Remove and dispose a container's child rows (Controls.Clear alone would leak
    // their GDI handles until finalization).
    private static void ClearRows(Control container)
    {
        var stale = container.Controls.Cast<Control>().ToArray();
        container.Controls.Clear();
        foreach (var c in stale) c.Dispose();
    }

    private static Color TicketColor(string key)
    {
        var hash = 0;
        foreach (var c in key) hash = hash * 31 + c;
        var idx = Math.Abs(hash) % TicketDark.Length;
        return Theme.Dark ? TicketDark[idx] : TicketLight[idx];
    }

    // The board's "+ Log": the ticket joins the typed ones, and focus goes to the ticket box so Enter queues right away.
    // Its summary marks it verified, as Jira would.
    internal void AddTicketToInput(string key, string summary)
    {
        _verify[key] = (VState.Valid, summary);
        var (existing, _) = TicketParser.Parse(_tickets.Text);
        if (!existing.Contains(key)) _tickets.Text = string.Join(", ", existing.Append(key));
        FocusTicketInput();
    }

    // New entry sits at the top; scrolling there first shows the whole card, not just the box.
    private void FocusTicketInput()
    {
        _body.AutoScrollPosition = Point.Empty;
        _tickets.Focus();
        _tickets.SelectionStart = _tickets.TextLength;
    }

    // The distinct tickets currently shown in "Will log" (for Jira verification):
    // what is typed, or - when nothing is typed - every queued ticket across all dates.
    private IReadOnlyList<string> ShownTickets()
    {
        var (typed, _) = TicketParser.Parse(_tickets.Text);
        if (typed.Count != 0) return typed;
        return TicketQueue.Read().SelectMany(e => e.Tickets).Distinct().ToList();
    }

    // Render "Will log". It always lists the whole persisted queue (grouped by date, each
    // headed "(queued for <LOG_TIME>)") so the accumulating list stays visible. While typing, the
    // current date's tickets are previewed on top (editable, headed "(not added yet)") so you
    // see what you are about to add without the already-queued rows disappearing. Each row
    // shows a Jira status dot and its time slots; queued rows carry a per-ticket remove [X].
    private void UpdateWillLog()
    {
        if (_willLogList.IsDisposed) return;
        _willLogReflowPending = false;
        _willLogList.SuspendLayout();
        ClearRows(_willLogList);

        // Leave room for the vertical scrollbar so rows never trigger a horizontal one.
        var rowWidth = Math.Max(UiScale.S(140), _willLogList.ClientSize.Width - UiScale.S(24));
        var (typed, _) = TicketParser.Parse(_tickets.Text);
        var entries = TicketQueue.Read();
        var composing = typed.Count != 0;

        // Current composition preview on top (editable hours), not yet added to the queue.
        if (composing)
        {
            var minutes = TypedMinutes(typed.Count);
            _willLogList.Controls.Add(WillLogText(Lang.LongDate(_date.Value) + Lang.T("   (not added yet)", "   (chưa thêm)"), rowWidth));
            AddEditableTicketRows(typed, minutes, rowWidth);
        }

        // The persisted queue below, always shown so the list visibly builds up.
        if (entries.Count != 0)
        {
            foreach (var entry in entries)
            {
                if (!DateOnly.TryParseExact(entry.Date, "yyyy-MM-dd", out var d)) continue;
                _willLogList.Controls.Add(WillLogText(Lang.LongDate(d.ToDateTime(TimeOnly.MinValue)) + Lang.T($"   (queued for {LogTimeText})", $"   (tự log lúc {LogTimeText})"), rowWidth));
                AddTicketRows(entry.Date, entry.Tickets, entry.Minutes, rowWidth);
            }
        }
        else if (!composing)
        {
            _willLogList.Controls.Add(WillLogText(Lang.T("Enter a ticket above to preview what will be logged.", "Nhập ticket ở trên để xem trước những gì sẽ được log."), rowWidth));
        }

        // Queue-wide buttons show only when not composing: they act on the whole queue and
        // would collide with the hours hint. Per-row [X] still removes queued tickets while
        // composing, and you finish the current entry before batch-acting anyway.
        var showQueueButtons = !composing && entries.Count != 0;
        _clearQueueBtn.Visible = showQueueButtons;
        _logAllBtn.Visible = showQueueButtons;
        _logAllBtn.Enabled = !_busy; // reset after a drain (the tray re-renders on completion)
        UpdateHoursHint(typedView: composing);
        _willLogList.ResumeLayout();
    }

    // Concrete per-ticket minutes for the current typed set: the user's custom values
    // when they line up with the ticket count, else the default even split.
    private IReadOnlyList<int> TypedMinutes(int count)
        => (_typedMinutes != null && _typedMinutes.Count == count)
            ? _typedMinutes
            : TimeSlots.EvenSplit(count);

    // Editable rows (typed view): each ticket carries an inline hours field.
    private void AddEditableTicketRows(IReadOnlyList<string> tickets, IReadOnlyList<int> minutes, int rowWidth)
    {
        for (var i = 0; i < tickets.Count; i++)
            _willLogList.Controls.Add(CreateWillLogEditRow(tickets[i], i, minutes[i], TimeSlots.Get(minutes, i), rowWidth));
    }

    // Read-only rows (queued fallback): honor a stored custom split, else even. Each row
    // carries its date so its [X] can remove that one ticket from that date's entry.
    private void AddTicketRows(string date, IReadOnlyList<string> tickets, IReadOnlyList<int>? minutes, int rowWidth)
    {
        var mins = minutes ?? TimeSlots.EvenSplit(tickets.Count);
        for (var i = 0; i < tickets.Count; i++)
            _willLogList.Controls.Add(CreateWillLogRow(date, tickets[i], TimeSlots.Get(mins, i), rowWidth, striped: i % 2 == 1));
    }

    // Show the running total / validation state in the header while composing. The total is
    // the whole selected day (already-queued tickets + what is being typed), so it warns
    // before Add rejects an over-8h merge rather than after.
    private void UpdateHoursHint(bool typedView)
    {
        if (!typedView) { _hoursHint.Visible = false; return; }

        var (sum, allPositive) = ProjectedDayStats();
        _hoursHint.Visible = true;
        if (!allPositive)
        {
            _hoursHint.Text = Lang.T("each ticket needs > 0h", "mỗi ticket cần > 0h");
            _hoursHint.ForeColor = Color.FromArgb(230, 76, 76);
        }
        else if (sum > TimeSlots.TotalWorkMinutes)
        {
            _hoursHint.Text = Lang.T($"{sum / 60.0:0.#}h - over 8h, trim", $"{sum / 60.0:0.#}h - quá 8h, giảm bớt");
            _hoursHint.ForeColor = Color.FromArgb(230, 76, 76);
        }
        else
        {
            _hoursHint.Text = $"{sum / 60.0:0.#}h / 8h";
            _hoursHint.ForeColor = Theme.TextSecondary;
        }
    }

    // Sum of the typed set's minutes and whether every ticket is > 0, for validation.
    private (int Sum, bool AllPositive) TypedMinuteStats()
    {
        var (typed, _) = TicketParser.Parse(_tickets.Text);
        if (typed.Count == 0) return (0, true);
        var mins = TypedMinutes(typed.Count);
        var sum = 0;
        var allPositive = true;
        foreach (var m in mins)
        {
            sum += m;
            if (m <= 0) allPositive = false;
        }
        return (sum, allPositive);
    }

    // Projected total minutes for the SELECTED date if the current typed set were added:
    // merges any queued entry for that date with the typed tickets, so the hint and the
    // hours gate reflect the whole day (queued + typed) - matching what OnQueue enforces.
    // No existing entry for the date -> just the typed sum. AllPositive is about the typed
    // tickets (already-queued minutes were validated when they were queued).
    private (int Sum, bool AllPositive) ProjectedDayStats()
    {
        var (typed, _) = TicketParser.Parse(_tickets.Text);
        if (typed.Count == 0) return (0, true);

        var (typedSum, allPositive) = TypedMinuteStats();

        var date = DateOnly.FromDateTime(_date.Value.Date).ToString("yyyy-MM-dd");
        var existing = TicketQueue.Read().FirstOrDefault(e => e.Date == date);
        if (existing is null) return (typedSum, allPositive);

        var merged = TicketQueue.MergeInto(existing, typed, TypedMinutesFor(typed));
        return (TicketQueue.DayMinutes(merged), allPositive);
    }

    // Apply an edited hours value to the typed set, then re-flow every row's slots.
    // Focus has already left the field (commit is on blur/Enter), so a rebuild is safe.
    private void OnHoursChanged(int index, double hours)
    {
        var (typed, _) = TicketParser.Parse(_tickets.Text);
        if (index < 0 || index >= typed.Count) return;

        if (_typedMinutes == null || _typedMinutes.Count != typed.Count)
            _typedMinutes = TimeSlots.EvenSplit(typed.Count).ToList();
        _typedMinutes[index] = (int)Math.Round(hours * 60);

        UpdateWillLog();
        UpdateActionState();
    }

    // The custom minutes for a to-be-logged set, or null to use the even split.
    private IReadOnlyList<int>? TypedMinutesFor(IReadOnlyList<string> tickets)
        => (_typedMinutes != null && _typedMinutes.Count == tickets.Count) ? _typedMinutes : null;

    private static Label WillLogText(string text, int width) => new()
    {
        Text = text,
        AutoSize = false,
        Width = width,
        Height = UiScale.S(20),
        Margin = new Padding(0),
        Font = WillLogFont,
        ForeColor = Theme.TextSecondary,
        BackColor = Color.Transparent,
        TextAlign = ContentAlignment.MiddleLeft,
        UseMnemonic = false,
    };

    private Control CreateWillLogRow(string date, string key, IReadOnlyList<TimeSlot> slots, int width, bool striped)
    {
        var row = new WillLogRow(striped)
        {
            Width = width,
            Key = key,
            KeyColor = TicketColor(key),
            Slots = SlotText(slots),
            Summary = SummaryFor(key),
            DotColor = DotColorFor(key),
        };
        row.SetRemoveAccessibleName(Lang.T($"Remove {key} on {date}", $"Xóa {key} ngày {date}"));
        row.RemoveClicked += () => RemoveQueuedTicket(date, key);
        return row;
    }

    private Control CreateWillLogEditRow(string key, int index, int minutes, IReadOnlyList<TimeSlot> slots, int width)
    {
        var row = new WillLogEditRow
        {
            Width = width,
            Key = key,
            KeyColor = TicketColor(key),
            Slots = SlotText(slots),
            Summary = SummaryFor(key),
            DotColor = DotColorFor(key),
            Index = index,
            Hours = Math.Round(minutes / 60.0, 2),
        };
        row.HoursChanged += OnHoursChanged;
        return row;
    }

    private static string SlotText(IReadOnlyList<TimeSlot> slots)
        => string.Join("  /  ", slots.Select(s => $"{s.Start}-{s.End}"));

    // The Jira title from verification (or the board's "+ Log"); empty while unverified.
    private string SummaryFor(string key)
        => _verify.TryGetValue(key, out var v) && v.Title != null ? v.Title : "";

    // The Jira verification color for a ticket's status dot (grey when unknown).
    private Color DotColorFor(string key)
    {
        var dotColor = Color.FromArgb(150, 150, 156);
        if (_verify.TryGetValue(key, out var v))
            dotColor = v.State switch
            {
                VState.Valid => Color.FromArgb(46, 160, 80),
                VState.NotFound => Color.FromArgb(230, 76, 76),
                VState.Error => Color.FromArgb(217, 164, 0),
                _ => dotColor,
            };
        return dotColor;
    }

    // Verify each previewed ticket against Jira (debounced), showing valid+title or
    // not-found in the "Will log" list. Covers the queued fallback too, so reopening
    // the window shows real status dots. Tickets added from the board are pre-marked valid.
    private async void VerifyTicketsAsync()
    {
        if (_service is null) return;
        var tickets = ShownTickets();
        var pending = tickets.Where(k => !_verify.ContainsKey(k)).Distinct().ToList();
        if (pending.Count == 0) return;

        foreach (var k in pending) _verify[k] = (VState.Verifying, null);
        UpdateWillLog();

        // Fire all lookups at once, then apply each result as it resolves (awaits
        // resume on the UI thread, so _verify stays single-threaded).
        var lookups = pending.Select(k => (Key: k, Task: _service.VerifyAsync(k))).ToList();
        foreach (var (key, task) in lookups)
        {
            try
            {
                var r = await task;
                _verify[key] = (r.Valid ? VState.Valid : VState.NotFound, r.Summary);
            }
            catch
            {
                _verify[key] = (VState.Error, null);
            }
            UpdateWillLog();
        }
    }

    private void OnQueue(object? sender, EventArgs e)
    {
        var (tickets, invalid) = TicketParser.Parse(_tickets.Text);
        if (invalid.Count > 0) AppendLog($"[queue] Ignored invalid: {string.Join(", ", invalid)}");
        if (tickets.Count == 0)
        {
            ShowStatus(Lang.T("No valid tickets to queue.", "Không có ticket hợp lệ để thêm."), false);
            return;
        }

        var (sum, allPositive) = TypedMinuteStats();
        if (!allPositive || sum > TimeSlots.TotalWorkMinutes)
        {
            ShowStatus(Lang.T("Fix the hours first: each ticket needs > 0h and the day can't exceed 8h.", "Sửa giờ trước: mỗi ticket cần > 0h và cả ngày không quá 8h."), false);
            return;
        }

        var newMinutes = TypedMinutesFor(tickets); // null when the user kept the even split
        var date = DateOnly.FromDateTime(_date.Value.Date).ToString("yyyy-MM-dd");
        var entries = TicketQueue.Read().ToList();
        var idx = entries.FindIndex(x => x.Date == date);
        if (idx >= 0)
        {
            var merged = TicketQueue.MergeInto(entries[idx], tickets, newMinutes);
            if (TicketQueue.DayMinutes(merged) > TimeSlots.TotalWorkMinutes)
            {
                ShowStatus(Lang.T($"That would exceed 8h for {date}. Trim the hours before queueing.", $"Ngày {date} sẽ vượt 8h. Giảm bớt giờ trước khi thêm."), false);
                return;
            }
            entries[idx] = merged;
        }
        else
        {
            entries.Add(new QueueEntry(date, tickets.ToList(), newMinutes));
        }

        TicketQueue.Write(entries.OrderBy(x => x.Date).ToList());
        AppendLog($"[queue] Added {date}: {string.Join(", ", tickets)}");
        _tickets.Text = string.Empty; // clear so "Will log" flips to the list and shows the new row
        ShowStatus(Lang.T($"Added {tickets.Count} ticket{(tickets.Count == 1 ? "" : "s")} to the queue for {date} (auto-logs at {LogTimeText}).",
            $"Đã thêm {tickets.Count} ticket vào hàng đợi ngày {date} (tự log lúc {LogTimeText})."), true);
        RefreshQueuedView();
        QueueChanged?.Invoke();
    }

    private void OnClearQueue(object? sender, EventArgs e)
    {
        var queuedDates = TicketQueue.Read().Count;
        if (queuedDates == 0) return;
        var clear = ConfirmDialog.Ask(FindForm(), Lang.T("Clear the queue?", "Xóa hàng đợi?"),
            Lang.T($"Every queued ticket ({queuedDates} {(queuedDates == 1 ? "day" : "days")}) is removed and will not be logged.",
                $"Mọi ticket đang chờ ({queuedDates} ngày) sẽ bị xóa và không được log."),
            Lang.T("Clear queue", "Xóa hàng đợi"), destructive: true);
        if (!clear) return;
        TicketQueue.Write(Array.Empty<QueueEntry>());
        RefreshQueuedView();
        ShowStatus(Lang.T("Queue cleared.", "Đã xóa hàng đợi."), true);
        QueueChanged?.Invoke();
    }

    // Remove one ticket from its date's queue entry (the row [X]) and refresh the list.
    private void RemoveQueuedTicket(string date, string ticket)
    {
        TicketQueue.RemoveTicket(date, ticket);
        RefreshQueuedView();
        ShowStatus(Lang.T($"Removed {ticket} from {date}.", $"Đã xóa {ticket} khỏi ngày {date}."), true);
        QueueChanged?.Invoke();
    }

    // Log the whole queued list now via the tray's guarded drain (DrainRequested). The
    // drain logs its progress to file, pops up the result, and re-renders on completion.
    private void OnLogAllNow(object? sender, EventArgs e)
    {
        if (_service is null) { ShowStatus(Lang.T("Config not loaded; cannot log.", "Chưa có cấu hình; không log được."), false); return; }
        if (TicketQueue.Read().Count == 0)
        {
            ShowStatus(Lang.T("Nothing queued to log.", "Hàng đợi trống, không có gì để log."), false);
            return;
        }
        _logAllBtn.Enabled = false;
        AppendLog("[log] Logging the whole queued list now...");
        // Sticky until the drain's result notice (always raised for a user drain) replaces it.
        BusyChanged?.Invoke(Lang.T("Logging the queue...", "Đang log hàng đợi..."));
        DrainRequested?.Invoke();
    }

    // Read the persisted queue and show it (read-only) so it stays visible after
    // a relaunch. The persisted queue is shown by "Will log" itself (it falls back to
    // the queue when the input is empty), so this just re-renders it. Called on
    // queue/clear, when the window activates, and after a drain.
    internal void RefreshQueuedView()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(RefreshQueuedView));
            return;
        }
        // Rebuilding would drop a half-typed hours value; the list's Leave rebuilds instead.
        if (_willLogList.ContainsFocus) _willLogReflowPending = true;
        else UpdateWillLog();
        VerifyTicketsAsync();
    }

    // Set a specific date and put the cursor in the ticket box. Used by
    // the week card to jump straight to a day that needs logging. SetDate marks it a
    // deliberate pick so OnActivatedRefresh's today-sync will not override it; ValueChanged then
    // refreshes the Will log preview.
    internal void PrepareForDate(DateOnly date)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<DateOnly>(PrepareForDate), date);
            return;
        }
        _date.SetDate(date);
        FocusTicketInput();
    }

    internal void ReloadWeekIfLoaded() => _weekCard?.ReloadIfLoaded();

    private async void OnLogNow(object? sender, EventArgs e)
    {
        if (_service is null) { ShowStatus(Lang.T("Config not loaded; cannot log.", "Chưa có cấu hình; không log được."), false); return; }
        var (tickets, date) = ParseEntry("log");
        if (tickets is null) return;
        if (HrmClosedForToday(date))
        {
            ShowStatus(Lang.T("HRM can't log today's hours before 6 PM (it rejects future times). Add it to the queue instead, or use Log now > TSC only.",
                "HRM không nhận giờ hôm nay trước 6 PM (không cho giờ tương lai). Hãy thêm vào hàng đợi, hoặc dùng Log ngay > Chỉ TSC."), false);
            return;
        }

        SetBusy(true, Lang.T("Logging to TSC + HRM...", "Đang log vào TSC + HRM..."));
        try
        {
            AppendLog($"[log] Logging {date:yyyy-MM-dd}: {string.Join(", ", tickets)} ...");
            var token = await _service.AcquireGraphTokenAsync(AppendLog);
            var result = await _service.LogEntryAsync(date, tickets, token, TypedMinutesFor(tickets), AppendLog);
            AppendLog($"[log] TSC: {(result.TscSuccess ? "OK" : result.TscError)}");
            AppendLog($"[log] HRM: {(result.HrmSuccess ? "OK" : result.HrmError)}");
            ShowStatus(result.AllSuccess
                ? Lang.T($"Logged {date:yyyy-MM-dd} to TSC + HRM.", $"Đã log ngày {date:yyyy-MM-dd} vào TSC + HRM.")
                : Lang.T("Partly failed", "Lỗi một phần") + $" - TSC: {(result.TscSuccess ? "OK" : result.TscError)}; HRM: {(result.HrmSuccess ? "OK" : result.HrmError)}",
                result.AllSuccess);
        }
        catch (Exception ex)
        {
            AppendLog($"[log] Error: {ex.Message}");
            ShowStatus(Lang.T($"Log failed: {ex.Message}", $"Log thất bại: {ex.Message}"), false);
        }
        finally { SetBusy(false); _weekCard?.ReloadIfLoaded(); }
    }

    private async void OnLogTsc(object? sender, EventArgs e)
    {
        if (_service is null) { ShowStatus(Lang.T("Config not loaded; cannot log.", "Chưa có cấu hình; không log được."), false); return; }
        var (tickets, date) = ParseEntry("tsc");
        if (tickets is null) return;

        SetBusy(true, Lang.T("Logging to TSC...", "Đang log vào TSC..."));
        try
        {
            var (ok, cell, err) = await _service.LogTscAsync(string.Join(", ", tickets), new[] { date }, AppendLog);
            AppendLog($"[tsc] {(ok ? $"OK ({cell})" : err)}");
            ShowStatus(ok ? Lang.T($"TSC logged ({cell}).", $"Đã log TSC ({cell}).") : Lang.T($"TSC failed: {err}", $"TSC lỗi: {err}"), ok);
        }
        catch (Exception ex)
        {
            AppendLog($"[tsc] Error: {ex.Message}");
            ShowStatus(Lang.T($"TSC failed: {ex.Message}", $"TSC lỗi: {ex.Message}"), false);
        }
        finally { SetBusy(false); _weekCard?.ReloadIfLoaded(); }
    }

    private async void OnLogHrm(object? sender, EventArgs e)
    {
        if (_service is null) { ShowStatus(Lang.T("Config not loaded; cannot log.", "Chưa có cấu hình; không log được."), false); return; }
        var (tickets, date) = ParseEntry("hrm");
        if (tickets is null) return;
        if (HrmClosedForToday(date))
        {
            ShowStatus(Lang.T("HRM can't log today's hours before 6 PM (it rejects future times). Add it to the queue instead.",
                "HRM không nhận giờ hôm nay trước 6 PM (không cho giờ tương lai). Hãy thêm vào hàng đợi."), false);
            return;
        }

        SetBusy(true, Lang.T("Logging to HRM...", "Đang log vào HRM..."));
        try
        {
            var (ok, err) = await _service.LogHrmAsync(tickets, date, TypedMinutesFor(tickets), AppendLog);
            AppendLog($"[hrm] {(ok ? "OK" : err)}");
            ShowStatus(ok ? Lang.T("HRM logged.", "Đã log HRM.") : Lang.T($"HRM failed: {err}", $"HRM lỗi: {err}"), ok);
        }
        catch (Exception ex)
        {
            AppendLog($"[hrm] Error: {ex.Message}");
            ShowStatus(Lang.T($"HRM failed: {ex.Message}", $"HRM lỗi: {ex.Message}"), false);
        }
        finally { SetBusy(false); _weekCard?.ReloadIfLoaded(); }
    }

    // The only path that overwrites, hence the confirm: one click, no other input, on a
    // workbook everyone reads.
    private async void OnLogOff(object? sender, EventArgs e)
    {
        if (_service is null) { ShowStatus(Lang.T("Config not loaded; cannot log.", "Chưa có cấu hình; không log được."), false); return; }

        var date = DateOnly.FromDateTime(_date.Value.Date);
        var markOff = ConfirmDialog.Ask(FindForm(),
            Lang.T($"Write \"{TscCells.OffMarker}\" to TSC for {Lang.LongDate(_date.Value.Date)}?",
                $"Ghi \"{TscCells.OffMarker}\" vào TSC cho {Lang.LongDate(_date.Value.Date)}?"),
            Lang.T("Anything already in that day's cells will be replaced, and any queued tickets for it dropped.",
                "Mọi nội dung đang có trong ô của ngày đó sẽ bị thay thế, và các ticket đang chờ của ngày đó sẽ bị bỏ."),
            Lang.T("Mark the day off", "Đánh dấu ngày nghỉ"), confirmByDefault: true, destructive: true);
        if (!markOff) return;

        SetBusy(true, Lang.T("Marking the day OFF in TSC...", "Đang đánh dấu nghỉ (OFF) trong TSC..."));
        try
        {
            var result = await _service.LogOffAsync(new[] { date }, AppendLog);
            if (result.Success && result.Marked.Count != 0)
            {
                // Drop the day's queued tickets so the scheduled drain cannot overwrite OFF.
                var queued = TicketQueue.Read().Where(q => q.Date == date.ToString("yyyy-MM-dd")).ToList();
                if (queued.Count != 0)
                {
                    TicketQueue.RemoveLogged(queued);
                    AppendLog($"[off] Removed {queued.Count} queued entr{(queued.Count == 1 ? "y" : "ies")} for {date:yyyy-MM-dd}.");
                }
                RefreshQueuedView();
                QueueChanged?.Invoke();
            }

            AppendLog($"[off] {(result.Success ? "OK" : result.Error)}");
            ShowStatus(result.Success
                ? Lang.T($"TSC marked {TscCells.OffMarker} for {date:yyyy-MM-dd}.", $"Đã đánh dấu {TscCells.OffMarker} trong TSC cho ngày {date:yyyy-MM-dd}.")
                : Lang.T($"Log OFF failed: {result.Error}", $"Đánh dấu nghỉ lỗi: {result.Error}"),
                result.Success);
        }
        catch (Exception ex)
        {
            AppendLog($"[off] Error: {ex.Message}");
            ShowStatus(Lang.T($"Log OFF failed: {ex.Message}", $"Đánh dấu nghỉ lỗi: {ex.Message}"), false);
        }
        finally { SetBusy(false); _weekCard?.ReloadIfLoaded(); }
    }

    private async void OnCheckTsc(object? sender, EventArgs e)
    {
        SetBusy(true, Lang.T("Checking TSC session...", "Đang kiểm tra phiên TSC..."));
        AppendLog("[tsc] Checking session...");
        try
        {
            var (loggedIn, error) = await TscTokenSniffer.CheckCredentialsAsync();
            AppendLog(error != null ? $"[tsc] Check failed: {error}"
                : loggedIn ? "[tsc] Session is valid." : "[tsc] Logged out - use Re-auth.");
            ShowStatus(error != null ? Lang.T($"TSC check failed: {error}", $"Kiểm tra TSC lỗi: {error}")
                : loggedIn ? Lang.T("TSC session is valid.", "Phiên TSC còn hiệu lực.")
                : Lang.T("TSC is logged out - use Re-authenticate TSC.", "TSC đã đăng xuất - hãy dùng Đăng nhập lại TSC."),
                error == null && loggedIn);
        }
        finally { SetBusy(false); }
    }

    private async void OnReauth(object? sender, EventArgs e)
    {
        SetBusy(true, Lang.T("Waiting for TSC sign-in in the browser...", "Đang chờ đăng nhập TSC trên trình duyệt..."));
        AppendLog("[tsc] Opening a browser for sign-in...");
        try
        {
            var (ok, error) = await TscTokenSniffer.ReauthenticateAsync(AppendLog);
            if (ok) _service?.InvalidateGraphToken();
            AppendLog(ok ? "[tsc] Session saved." : $"[tsc] Re-auth failed: {error}");
            ShowStatus(ok ? Lang.T("TSC session saved.", "Đã lưu phiên TSC.") : Lang.T($"Re-auth failed: {error}", $"Đăng nhập lại lỗi: {error}"), ok);
            if (ok) ReauthSucceeded?.Invoke();
        }
        finally { SetBusy(false); }
    }

    // Parse + validate the single entry (date + tickets). Returns (null, _) with a
    // logged reason if there is nothing valid to act on.
    private (IReadOnlyList<string>? Tickets, DateOnly Date) ParseEntry(string tag)
    {
        var (tickets, invalid) = TicketParser.Parse(_tickets.Text);
        if (invalid.Count > 0) AppendLog($"[{tag}] Ignored invalid: {string.Join(", ", invalid)}");
        if (tickets.Count == 0)
        {
            ShowStatus(Lang.T("No valid tickets.", "Không có ticket hợp lệ."), false);
            return (null, default);
        }
        return (tickets, DateOnly.FromDateTime(_date.Value.Date));
    }

    // The configured daily auto-log time (LOG_TIME), e.g. "6:00 PM".
    private string LogTimeText =>
        (_service?.LogTime ?? AppConfig.DefaultLogTime).ToString("h:mm tt", CultureInfo.InvariantCulture);

    // HRM rejects future stop times, so today's hours cannot be logged before 18:00 HCM.
    private static bool HrmClosedForToday(DateOnly date) => date == Hcm.Today() && Hcm.Now().Hour < 18;

    // While busy, a sticky notice says what is running; the result notice replaces it,
    // and anything still sticky (e.g. the window was hidden meanwhile) is cleared at the end.
    private void SetBusy(bool busy, string? busyNotice = null)
    {
        _busy = busy;
        _busyWork?.Dispose();
        _busyWork = busy ? BusyWork.Shared.Begin(busyNotice ?? BusyText) : null;
        UseWaitCursor = busy;
        _logNowBtn.Text = busy ? BusyText : LogNowText;
        if (busy && busyNotice != null) BusyChanged?.Invoke(busyNotice);
        if (!busy) BusyChanged?.Invoke(null);
        UpdateActionState();
    }

    // Checked on open because the 6 PM cutoff passes with no input event to react to.
    private void UpdateLogNowMenu()
    {
        UpdateActionState();
        var hrmClosed = HrmClosedForToday(DateOnly.FromDateTime(_date.Value.Date));
        var after6Pm = hrmClosed ? Lang.T("  (after 6 PM)", "  (sau 6 PM)") : "";
        _logBothItem.Text = "TSC + HRM" + after6Pm;
        _logHrmItem.Text = Lang.T("HRM only", "Chỉ HRM") + after6Pm;
        if (hrmClosed)
        {
            _logBothItem.Enabled = false;
            _logHrmItem.Enabled = false;
        }
    }

    // Enable the ticket-dependent actions only when there is at least one valid ticket
    // and no operation is in flight. Add to queue and the TSC + HRM / HRM only log items also
    // need valid hours (each ticket > 0, the selected day <= 8h); TSC only ignores time.
    private void UpdateActionState()
    {
        var hasTickets = TicketParser.Parse(_tickets.Text).Tickets.Count != 0;
        var (sum, allPositive) = ProjectedDayStats();
        var hoursOk = allPositive && sum <= TimeSlots.TotalWorkMinutes;

        _queueBtn.Enabled = !_busy && hasTickets && hoursOk;
        _logNowBtn.Enabled = !_busy && hasTickets; // TSC only still works when hours are off
        _logBothItem.Enabled = !_busy && hasTickets && hoursOk;
        _logTscItem.Enabled = !_busy && hasTickets;
        _logHrmItem.Enabled = !_busy && hasTickets && hoursOk;
        _moreBtn.Enabled = !_busy; // Log OFF needs no ticket, and a future leave day is fair game
        _logAllBtn.Enabled = !_busy; // batch drain must not fight an in-flight browser/log op
    }

    // Re-read the persisted queue each time the window is focused (e.g. reopened from
    // the tray, or after a scheduled drain happened while it was hidden).
    public void OnActivatedRefresh()
    {
        _date.SyncToTodayIfAuto();
        RefreshQueuedView();
    }

    // Every time the page is shown, start in the ticket box so you can type right away.
    public void OnPageShown()
    {
        OnActivatedRefresh();
        BeginInvoke(FocusTicketInput);
        _weekCard?.LoadIfNeeded();
    }

    // The date resets to today on reopen, so a half-typed entry would land on the wrong day.
    public void OnHidden()
    {
        _date.ForgetManualPick();
        _tickets.Clear();
        _weekCard?.Reset();
    }
}
