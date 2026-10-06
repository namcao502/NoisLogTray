using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;

namespace NoisLogTray;

// The capture window: a header (with a light/dark toggle), then stacked cards -- "New
// entry" (date/ticket + actions), "Will log" (preview + queue), and "My tickets".
// Closing (X) hides to the tray; TrayApp owns the process lifetime.
internal sealed class MainForm : Form, IMessageFilter
{
    private static readonly Font KeyFont = new("Segoe UI", 9F, FontStyle.Bold);
    private static readonly Font SummaryFont = new("Segoe UI", 9F);
    private static readonly Font SectionFont = new("Segoe UI", 8.5F, FontStyle.Bold);
    private static readonly Font WillLogFont = new("Segoe UI", 8.5F, FontStyle.Bold);

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

    // Due-date "temperature" ramp for the My-tickets list: cool (far out / no due date)
    // to hot (due today / overdue), tuned per theme so the key text stays readable.
    private static readonly Color[] HeatDark =
    {
        Color.FromArgb(88, 166, 255),   // blue   - coolest
        Color.FromArgb(57, 197, 207),   // teal
        Color.FromArgb(63, 185, 80),    // green
        Color.FromArgb(227, 179, 65),   // amber
        Color.FromArgb(255, 157, 92),   // orange
        Color.FromArgb(255, 105, 97),   // red    - hottest
    };

    private static readonly Color[] HeatLight =
    {
        Color.FromArgb(37, 99, 235),    // blue   - coolest
        Color.FromArgb(14, 116, 144),   // teal
        Color.FromArgb(21, 128, 61),    // green
        Color.FromArgb(180, 83, 9),     // amber
        Color.FromArgb(194, 65, 12),    // orange
        Color.FromArgb(220, 38, 38),    // red    - hottest
    };

    private const int DueHeatMaxDays = 14; // due >= this many days out reads as coolest

    private const int CardW = 560;

    // One height for every button and input; widths vary by role (the primary button is
    // widest, card-header toolbar buttons share ToolbarBtnW). Neighbours sit 8px apart.
    private const int BtnH = 30;
    private const int ToolbarBtnW = 112;
    private const int BtnGap = 8;
    private const int ToolbarY = 7;   // card-header toolbar, centred on the section title
    private const int CardBodyY = 44; // first content row under a card header
    private const int InnerW = 528; // CardW - 2*16
    // Fixed height: a date header + 3 (editable, tallest) ticket rows + list padding;
    // longer lists scroll internally.
    private const int WillLogHostH = 20 + 3 * 26 + 8;
    private const int SuggestionRowPitch = 31; // suggestion row height (zero margin)

    private readonly LoggingService? _service;

    private readonly Panel _body = new();
    private readonly Panel _header = new();
    private readonly Label _headerTitle = new();
    private readonly ThemeToggleButton _themeBtn = new();
    private readonly LanguageToggleButton _langBtn = new();
    private readonly SettingsButton _settingsBtn = new();
    private readonly CloseButton _closeBtn = new() { Surface = () => Theme.WindowBg };
    private readonly NotificationBell _bell = new();
    private readonly ContextMenuStrip _bellMenu = ThemedMenuRenderer.CreateMenu();
    private readonly NoticeToast _toast = new();

    // Recent notices for the bell's history, newest last; session-only, capped.
    private const int NoticeHistoryCap = 20;
    private readonly List<(DateTime Time, string Message, NoticeKind Kind)> _notices = new();
    private readonly List<Label> _sectionLabels = new();
    private readonly List<Panel> _dividers = new();

    private readonly RoundedDatePicker _date = new();
    private readonly ToolTip _tips = new();
    private readonly TextBox _tickets = new();
    private readonly TextBox _ticketSearch = new();
    private readonly MacButton _queueBtn = MacButton.Primary("");
    private readonly MacButton _logNowBtn = MacButton.Secondary("");
    private readonly MacButton _moreBtn = MacButton.Secondary("⋯");

    // Rarely used actions live in dropdowns so "Add to queue" stays the one obvious button.
    private readonly ContextMenuStrip _logNowMenu = ThemedMenuRenderer.CreateMenu();
    private readonly ToolStripMenuItem _logBothItem = new();
    private readonly ToolStripMenuItem _logTscItem = new();
    private readonly ToolStripMenuItem _logHrmItem = new();
    private readonly ContextMenuStrip _moreMenu = ThemedMenuRenderer.CreateMenu();
    private readonly ToolStripMenuItem _logOffItem = new();
    private readonly ToolStripMenuItem _checkItem = new();
    private readonly ToolStripMenuItem _reauthItem = new();
    private readonly MacButton _refreshBtn = MacButton.Secondary("");
    private readonly MacButton _jqlBtn = MacButton.Secondary("");
    private readonly MacButton _clearBtn = MacButton.Secondary("");
    private readonly FlowLayoutPanel _suggestions = new();
    private readonly Label _suggestionStatus = new();
    private readonly FlowLayoutPanel _willLogList = new();
    private readonly System.Windows.Forms.Timer _verifyTimer = new() { Interval = 500 };
    private readonly Dictionary<string, (VState State, string? Title)> _verify = new();
    private readonly MacButton _clearQueueBtn = MacButton.Secondary("");
    private readonly MacButton _logAllBtn = MacButton.Secondary("");
    private readonly Label _hoursHint = new();

    // Per typed-ticket HRM minutes while composing an entry. null = the default even
    // split (see TimeSlots.EvenSplit); reset whenever the ticket text changes.
    private List<int>? _typedMinutes;

    private IReadOnlyList<JiraSuggestion> _lastSuggestions = Array.Empty<JiraSuggestion>();
    private bool _busy;
    private bool _loadingSuggestions;

    // The text behind the suggestion-list status line, re-read on a language switch.
    private Func<string>? _suggestionStatusText;

    // Section labels with both texts, so ApplyLanguage can re-label them.
    private readonly List<(Label Label, string English, string Vietnamese)> _sectionLabelTexts = new();

    private enum VState { Verifying, Valid, NotFound, Error }

    internal event Action? QueueChanged;

    // Raised after a successful TSC re-auth so the tray can retry any due queue entries.
    internal event Action? ReauthSucceeded;

    // Raised by "Log queue now" so the tray drains the whole queue through its guarded path.
    internal event Action? DrainRequested;

    // Raised by the header gear; the tray owns the Settings dialog (saving rebuilds this window).
    internal event Action? SettingsRequested;

    // Raised with each action's result so the tray shows it as a popup (ok = success).
    internal event Action<string, NoticeKind>? StatusRaised;

    internal MainForm(LoggingService? service, string? configError)
    {
        _service = service;
        BuildLayout();
        ApplyLanguage();
        Theme.Changed += ApplyTheme;
        Lang.Changed += ApplyLanguage;
        Application.AddMessageFilter(this);
        RefreshQueuedView(); // also renders the Will log (falls back to the queue)
        if (configError != null) AppendLog($"[config] {configError}");
        LoadMyTicketsAsync();
        ReflowRowsOnResize();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= ApplyTheme;
            Lang.Changed -= ApplyLanguage;
            Application.RemoveMessageFilter(this);
            _bellMenu.Dispose();
            _logNowMenu.Dispose();
            _moreMenu.Dispose();
        }
        base.Dispose(disposing);
    }

    private const int WmLButtonDown = 0x0201;

    // Drop focus from the search box on a click anywhere else. Labels and rows do not take
    // focus themselves, so without this the caret would stay in the search box.
    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg == WmLButtonDown && _ticketSearch.Focused && m.HWnd != _ticketSearch.Handle)
            ActiveControl = null;
        return false; // never swallow the click
    }

    private void BuildLayout()
    {
        Text = "NOIS Daily Log";
        Icon = AppIcon.Load(32);
        // Borderless rounded window drawn by the app (like the Settings card) instead of the
        // square Windows 10 frame; the header doubles as the title bar.
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Theme.WindowBg;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        RestoreWindowPosition();

        BuildBody();
        RestoreWindowSize();
    }

    // Restore the last window position if it still lands on a connected monitor;
    // otherwise center (the saved monitor may be gone).
    private void RestoreWindowPosition()
    {
        var settings = AppSettings.Load();
        if (settings.WindowX is int x && settings.WindowY is int y &&
            Screen.AllScreens.Any(s => s.WorkingArea.Contains(new Point(x, y))))
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(x, y);
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
        }
    }

    // Restore the last size, clamped to the minimum (the built layout) and to the screen.
    private void RestoreWindowSize()
    {
        var settings = AppSettings.Load();
        if (settings.WindowWidth is not int width || settings.WindowHeight is not int height) return;
        var workingArea = Screen.FromPoint(Location).WorkingArea;
        ClientSize = new Size(
            Math.Clamp(width, MinimumSize.Width, Math.Max(MinimumSize.Width, workingArea.Width)),
            Math.Clamp(height, MinimumSize.Height, Math.Max(MinimumSize.Height, workingArea.Height)));
    }

    // Persist the window position and size (read-modify-write so the theme key is preserved).
    private void SaveWindowBounds()
    {
        if (WindowState != FormWindowState.Normal) return;
        var settings = AppSettings.Load();
        settings.WindowX = Location.X;
        settings.WindowY = Location.Y;
        settings.WindowWidth = ClientSize.Width;
        settings.WindowHeight = ClientSize.Height;
        AppSettings.Save(settings);
    }

    // ---- Borderless rounded chrome: shadow, taskbar minimize, rounded corners, border, drag.

    private const int WindowRadius = 12;

    // The form's own edge strip, outside _body, that Windows treats as the resize border.
    private const int ResizeGrip = 6;
    private const int WmNcHitTest = 0x0084;

    // How much shorter than the built layout the window may get; only My tickets shrinks.
    private const int MinHeightSlack = 100;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            cp.Style |= 0x00020000;      // WS_MINIMIZEBOX: the taskbar button still minimizes/restores
            return cp;
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (ClientSize.Width == 0 || ClientSize.Height == 0) return;
        using var path = RoundedPath(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), WindowRadius);
        Region = new Region(path);
    }

    // Drawn on the form, not _body: _body is inset by the resize grip.
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedPath(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), WindowRadius);
        using var pen = new Pen(Theme.CardBorder, 1f);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != WmNcHitTest || WindowState != FormWindowState.Normal) return;
        var lParam = m.LParam.ToInt64();
        var cursor = PointToClient(new Point((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF)));
        var hit = ResizeHitTest.At(cursor, ClientSize, ResizeGrip, WindowRadius);
        if (hit != ResizeHitTest.None) m.Result = hit;
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // Hand the drag to Windows as a title-bar drag; ResizeEnd then saves the new position.
    private void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
    }

    private static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        SaveWindowBounds();
    }

    private void BuildBody()
    {
        _body.Dock = DockStyle.Fill;
        _body.BackColor = Theme.WindowBg;
        _body.MouseDown += DragWindow;
        _body.AutoScroll = false; // main window never scrolls

        // _body starts ResizeGrip in from the window edge; offsets subtract it so the
        // cards stay 20px from the visible edge.
        const int left = 20 - ResizeGrip;
        var y = 14 - ResizeGrip;
        var header = BuildHeader();
        header.Location = new Point(left, y);
        _body.Controls.Add(header);
        y += header.Height + 8;

        // Input + primary action first (where focus lands), the queue next, the picker last.
        var newEntry = BuildNewEntryCard();
        newEntry.Location = new Point(left, y);
        _body.Controls.Add(newEntry);
        y += newEntry.Height + 12;

        var willLog = BuildWillLogCard();
        willLog.Location = new Point(left, y);
        _body.Controls.Add(willLog);
        y += willLog.Height + 12;

        var myTickets = BuildMyTicketsCard();
        myTickets.Location = new Point(left, y);
        _body.Controls.Add(myTickets);
        ClientSize = new Size(600, myTickets.Bottom + 20 + ResizeGrip); // fit the cards snugly
        MinimumSize = new Size(ClientSize.Width, ClientSize.Height - MinHeightSlack);

        _toast.Width = 320;
        _toast.Location = new Point(header.Right - _toast.Width, header.Top + _bell.Bottom + 6);
        _body.Controls.Add(_toast);

        AcceptButton = _queueBtn;
        Padding = new Padding(ResizeGrip);
        Controls.Add(_body);

        AnchorForResize(header, newEntry, willLog, myTickets);
    }

    // Set after _body is docked at its final size: WinForms records each anchor's edge
    // distances when Anchor is assigned, so the built layout is what a resize preserves.
    // Cards and inputs stretch with the width, right-hand buttons stay right, and only
    // the My tickets list takes extra height.
    private void AnchorForResize(Panel header, Card newEntry, Card willLog, Card myTickets)
    {
        const AnchorStyles stretch = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        const AnchorStyles right = AnchorStyles.Top | AnchorStyles.Right;

        header.Anchor = stretch;
        foreach (var button in new Control[] { _bell, _themeBtn, _langBtn, _settingsBtn, _closeBtn }) button.Anchor = right;
        _toast.Anchor = right;

        newEntry.Anchor = stretch;
        _tickets.Parent!.Anchor = stretch;
        _tickets.Anchor = stretch;
        _clearBtn.Anchor = right;
        _queueBtn.Anchor = stretch;
        _logNowBtn.Anchor = right;
        _moreBtn.Anchor = right;

        willLog.Anchor = stretch;
        _hoursHint.Anchor = right;
        _clearQueueBtn.Anchor = right;
        _logAllBtn.Anchor = right;
        _willLogList.Parent!.Anchor = stretch;

        myTickets.Anchor = stretch | AnchorStyles.Bottom;
        _ticketSearch.Parent!.Anchor = stretch;
        _ticketSearch.Anchor = stretch;
        _jqlBtn.Anchor = right;
        _refreshBtn.Anchor = right;
        _suggestions.Parent!.Anchor = stretch | AnchorStyles.Bottom;
    }

    // Rows are built at the list's width, so a width change rebuilds them. Hooked after
    // the first render so construction-time layout passes don't re-render early.
    // The rebuild is posted: run inside the window's layout pass, the list keeps the old
    // wider scroll extent after a shrink and shows a stray horizontal scrollbar.
    private void ReflowRowsOnResize()
    {
        var suggestionsWidth = _suggestions.Width;
        _suggestions.Resize += (_, _) =>
        {
            if (_suggestions.Width == suggestionsWidth || !IsHandleCreated) return;
            suggestionsWidth = _suggestions.Width;
            BeginInvoke(() =>
            {
                if (!_loadingSuggestions) RenderSuggestions(_lastSuggestions); // keep "Loading..." visible
            });
        };

        var willLogWidth = _willLogList.Width;
        _willLogList.Resize += (_, _) =>
        {
            if (_willLogList.Width == willLogWidth || !IsHandleCreated) return;
            willLogWidth = _willLogList.Width;
            BeginInvoke(UpdateWillLog);
        };
    }

    private Panel BuildHeader()
    {
        // Title only: the date already shows in the New entry date field.
        _header.Size = new Size(CardW, 48);
        _header.BackColor = Theme.WindowBg;

        _headerTitle.Text = "NOIS Daily Log";
        _headerTitle.AutoSize = true;
        _headerTitle.Location = new Point(0, 4);
        _headerTitle.Font = new Font("Segoe UI Semibold", 20F, FontStyle.Bold);
        _headerTitle.ForeColor = Theme.TextPrimary;

        _themeBtn.OnWindow = true;
        _themeBtn.Size = new Size(BtnH, BtnH);
        // Right to left: close (hide to tray), then a wider gap, settings, language, theme, bell.
        _closeBtn.Size = new Size(BtnH, BtnH);
        _closeBtn.Location = new Point(CardW - BtnH, 9);
        _closeBtn.Click += (_, _) => Close(); // OnFormClosing turns this into hide-to-tray

        const int toolsRight = CardW - BtnH - 12;
        _settingsBtn.Size = new Size(BtnH, BtnH);
        _settingsBtn.Location = new Point(toolsRight - BtnH, 9);
        _settingsBtn.Click += (_, _) => SettingsRequested?.Invoke();

        _langBtn.Size = new Size(BtnH, BtnH);
        _langBtn.Location = new Point(toolsRight - 2 * BtnH - BtnGap, 9);

        _themeBtn.Location = new Point(toolsRight - 3 * BtnH - 2 * BtnGap, 9);

        _bell.Size = new Size(BtnH, BtnH);
        _bell.Location = new Point(toolsRight - 4 * BtnH - 3 * BtnGap, 9);
        _bell.Click += (_, _) => OpenNoticeHistory();

        // There is no title bar, so the header (and its title) drags the window.
        _header.MouseDown += DragWindow;
        _headerTitle.MouseDown += DragWindow;

        _header.Controls.Add(_headerTitle);
        _header.Controls.Add(_themeBtn);
        _header.Controls.Add(_langBtn);
        _header.Controls.Add(_settingsBtn);
        _header.Controls.Add(_closeBtn);
        _header.Controls.Add(_bell);
        return _header;
    }

    private Card BuildMyTicketsCard()
    {
        var card = new Card { Size = new Size(CardW, CardBodyY + 204 + 16) };
        card.Controls.Add(SectionLabel("MY TICKETS", "TICKET CỦA TÔI", 16, 14));

        // Quick filter over the loaded list, between the section label and the buttons; it
        // starts after the wider of the two label texts so a language switch never overlaps.
        var labelWidth = Math.Max(TextRenderer.MeasureText("MY TICKETS", SectionFont).Width,
            TextRenderer.MeasureText("TICKET CỦA TÔI", SectionFont).Width);
        var searchX = 16 + labelWidth + 8;
        var searchHost = new RoundedHost { Location = new Point(searchX, ToolbarY), Size = new Size(16 + InnerW - 2 * (ToolbarBtnW + BtnGap) - searchX, BtnH) };
        _ticketSearch.BorderStyle = BorderStyle.None;
        _ticketSearch.Font = new Font("Segoe UI", 9F);
        _ticketSearch.BackColor = Theme.InputBg;
        _ticketSearch.ForeColor = Theme.TextPrimary;
        _ticketSearch.TextChanged += (_, _) =>
        {
            if (!_loadingSuggestions) RenderSuggestions(_lastSuggestions); // keep "Loading..." visible
        };
        _ticketSearch.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Escape) return;
            _ticketSearch.Text = string.Empty;
            e.SuppressKeyPress = true;
        };
        var searchH = _ticketSearch.PreferredHeight;
        _ticketSearch.SetBounds(8, (searchHost.Height - searchH) / 2, searchHost.Width - 16, searchH);
        searchHost.Controls.Add(_ticketSearch);
        card.Controls.Add(searchHost);

        _jqlBtn.Size = new Size(ToolbarBtnW, BtnH);
        _jqlBtn.Location = new Point(16 + InnerW - 2 * ToolbarBtnW - BtnGap, ToolbarY);
        _jqlBtn.Click += (_, _) => EditJql();
        card.Controls.Add(_jqlBtn);

        _refreshBtn.Size = new Size(ToolbarBtnW, BtnH);
        _refreshBtn.Location = new Point(16 + InnerW - ToolbarBtnW, ToolbarY);
        _refreshBtn.Click += (_, _) => LoadMyTicketsAsync();
        card.Controls.Add(_refreshBtn);

        var sugHost = new RoundedHost { Location = new Point(16, CardBodyY), Size = new Size(InnerW, 204) };
        _suggestions.Dock = DockStyle.Fill;
        _suggestions.FlowDirection = FlowDirection.TopDown;
        _suggestions.WrapContents = false;
        _suggestions.AutoScroll = true; // fixed height; scroll through the full list
        _suggestions.BorderStyle = BorderStyle.None;
        _suggestions.BackColor = Theme.InputBg;
        sugHost.Controls.Add(_suggestions);

        _suggestionStatus.Text = "";
        _suggestionStatus.AutoSize = true;
        _suggestionStatus.Location = new Point(8, 8);
        _suggestionStatus.ForeColor = Theme.TextSecondary;
        _suggestionStatus.BackColor = Theme.InputBg;
        _suggestionStatus.Font = SummaryFont;
        sugHost.Controls.Add(_suggestionStatus);
        _suggestionStatus.BringToFront();

        card.Controls.Add(sugHost);
        return card;
    }

    private Card BuildWillLogCard()
    {
        var card = new Card { Size = new Size(CardW, CardBodyY + WillLogHostH + 12) };
        card.Controls.Add(SectionLabel("QUEUE", "HÀNG ĐỢI", 16, 14));

        // Running total / validation hint, right-aligned in the header (typed view only).
        _hoursHint.AutoSize = false;
        _hoursHint.Size = new Size(170, 18);
        _hoursHint.Location = new Point(16 + InnerW - 170, ToolbarY + (BtnH - 18) / 2);
        _hoursHint.TextAlign = ContentAlignment.MiddleRight;
        _hoursHint.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
        _hoursHint.ForeColor = Theme.TextSecondary;
        _hoursHint.BackColor = Color.Transparent;
        _hoursHint.Visible = false;
        card.Controls.Add(_hoursHint);

        // Clears the persisted queue; only shown while the card is displaying the
        // queued fallback (input empty + something queued).
        _clearQueueBtn.Size = new Size(ToolbarBtnW, BtnH);
        _clearQueueBtn.Location = new Point(16 + InnerW - ToolbarBtnW, ToolbarY);
        _clearQueueBtn.Visible = false;
        _clearQueueBtn.Click += OnClearQueue;
        card.Controls.Add(_clearQueueBtn);

        // Logs the whole queued list now (same guarded drain as the tray "Log queue now");
        // shown beside Clear queue, only in the queued fallback view.
        _logAllBtn.Size = new Size(ToolbarBtnW, BtnH);
        _logAllBtn.Location = new Point(16 + InnerW - 2 * ToolbarBtnW - BtnGap, ToolbarY);
        _logAllBtn.Visible = false;
        _logAllBtn.Click += OnLogAllNow;
        card.Controls.Add(_logAllBtn);

        // Fixed-height list; rows scroll internally once they overflow so the Actions
        // card below stays at a stable, visible position.
        var host = new RoundedHost { Location = new Point(16, CardBodyY), Size = new Size(InnerW, WillLogHostH) };
        _willLogList.Dock = DockStyle.Fill;
        _willLogList.FlowDirection = FlowDirection.TopDown;
        _willLogList.WrapContents = false;
        _willLogList.AutoScroll = true;
        _willLogList.BackColor = Theme.InputBg;
        _willLogList.Padding = new Padding(6, 4, 6, 4);
        host.Controls.Add(_willLogList);

        _verifyTimer.Tick += (_, _) => { _verifyTimer.Stop(); VerifyTicketsAsync(); };

        card.Controls.Add(host);
        return card;
    }

    private Card BuildNewEntryCard()
    {
        var card = new Card { Size = new Size(CardW, 96 + BtnH + 16) };
        card.Controls.Add(SectionLabel("NEW ENTRY", "NHẬP MỚI", 16, 14));

        card.Controls.Add(SectionLabel("DATE", "NGÀY", 16, 40));
        card.Controls.Add(SectionLabel("TICKET", "TICKET", 214, 40));

        _date.Location = new Point(16, 58);
        _date.Size = new Size(190, BtnH);
        _date.ValueChanged += (_, _) => { UpdateWillLog(); VerifyTicketsAsync(); };

        var ticketHost = new RoundedHost { Location = new Point(214, 58), Size = new Size(262, BtnH) };
        _tickets.BorderStyle = BorderStyle.None;
        _tickets.Font = new Font("Segoe UI", 9.5F);
        _tickets.BackColor = Theme.InputBg;
        _tickets.ForeColor = Theme.TextPrimary;
        _tickets.TextChanged += (_, _) => { _typedMinutes = null; UpdateWillLog(); UpdateActionState(); _verifyTimer.Stop(); _verifyTimer.Start(); };
        _tickets.Leave += (_, _) => { _verifyTimer.Stop(); VerifyTicketsAsync(); };
        var ticketH = _tickets.PreferredHeight;
        _tickets.SetBounds(10, (ticketHost.Height - ticketH) / 2, ticketHost.Width - 20, ticketH);
        ticketHost.Controls.Add(_tickets);

        _clearBtn.Size = new Size(16 + InnerW - (476 + BtnGap), BtnH); // fills to the card edge
        _clearBtn.Location = new Point(476 + BtnGap, 58);
        _clearBtn.Click += (_, _) => _tickets.Text = string.Empty;

        card.Controls.Add(_date);
        card.Controls.Add(ticketHost);
        card.Controls.Add(_clearBtn);

        // One row: the primary "Add to queue", then the "Log now" and "more" dropdowns.
        const int logNowW = 140;
        const int moreW = 52;
        const int queueW = InnerW - logNowW - moreW - 2 * BtnGap;

        _queueBtn.Size = new Size(queueW, BtnH);
        _queueBtn.Location = new Point(16, 96);
        _queueBtn.Click += OnQueue;

        _logNowBtn.Size = new Size(logNowW, BtnH);
        _logNowBtn.Location = new Point(16 + queueW + BtnGap, 96);
        _logNowBtn.Click += (_, _) => ThemedMenuRenderer.ShowBelow(_logNowMenu, _logNowBtn);

        _moreBtn.Size = new Size(moreW, BtnH);
        _moreBtn.Location = new Point(16 + InnerW - moreW, 96);
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
            Location = new Point(x, y),
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
        _refreshBtn.Text = Lang.T("Refresh", "Làm mới");
        _jqlBtn.Text = Lang.T("Edit JQL", "Sửa JQL");
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
        _ticketSearch.PlaceholderText = Lang.T("Search, then click a row to add", "Tìm, rồi bấm một dòng để thêm");
        _ticketSearch.AccessibleName = Lang.T("Search my tickets", "Tìm ticket của tôi");

        _tips.SetToolTip(_bell, Lang.T("Notifications", "Thông báo"));
        _tips.SetToolTip(_themeBtn, Lang.T("Light / dark theme", "Giao diện sáng / tối"));
        _tips.SetToolTip(_langBtn, Lang.T("Switch to Vietnamese", "Chuyển sang tiếng Anh"));
        _tips.SetToolTip(_settingsBtn, Lang.T("Settings", "Cài đặt"));
        _tips.SetToolTip(_closeBtn, Lang.T("Hide to tray", "Ẩn xuống khay"));
        _closeBtn.AccessibleName = Lang.T("Hide to tray", "Ẩn xuống khay");
        _tips.SetToolTip(_date, Lang.T(
            "Dates and the daily auto-log use Vietnam time (Asia/Ho_Chi_Minh, UTC+7).",
            "Ngày và giờ tự log hằng ngày theo giờ Việt Nam (Asia/Ho_Chi_Minh, UTC+7)."));
        _tips.SetToolTip(_logNowBtn, Lang.T(
            "Log the typed tickets right away instead of waiting for the scheduled run.",
            "Log ngay các ticket đã nhập thay vì chờ tới giờ tự log."));
        _tips.SetToolTip(_moreBtn, Lang.T("More actions: Log OFF, TSC session", "Thao tác khác: đánh dấu nghỉ, phiên TSC"));

        if (_suggestionStatusText != null && _suggestionStatus.Text.Length != 0)
            _suggestionStatus.Text = _suggestionStatusText();
        else if (!_loadingSuggestions)
            RenderSuggestions(_lastSuggestions);
        UpdateWillLog();
        if (_bellMenu.Visible) RenderNoticeHistory();
    }

    private static string LogNowText => Lang.T("Log now  ▾", "Log ngay  ▾");
    private static string BusyText => Lang.T("Working...", "Đang chạy...");

    // Re-apply theme colors to the native controls (custom controls repaint
    // themselves via Theme.Changed) and re-render the suggestion rows.
    private void ApplyTheme()
    {
        BackColor = Theme.WindowBg;
        _body.BackColor = Theme.WindowBg;
        _header.BackColor = Theme.WindowBg;

        _headerTitle.ForeColor = Theme.TextPrimary;
        foreach (var l in _sectionLabels) l.ForeColor = Theme.TextSecondary;
        foreach (var d in _dividers) d.BackColor = Theme.Divider;

        _tickets.BackColor = Theme.InputBg;
        _tickets.ForeColor = Theme.TextPrimary;
        _ticketSearch.BackColor = Theme.InputBg;
        _ticketSearch.ForeColor = Theme.TextPrimary;
        _suggestions.BackColor = Theme.InputBg;
        _suggestionStatus.BackColor = Theme.InputBg;
        _suggestionStatus.ForeColor = Theme.TextSecondary;
        _willLogList.BackColor = Theme.InputBg;

        RenderSuggestions(_lastSuggestions);
        UpdateWillLog();
        Invalidate(true);
    }

    // Detailed activity goes to the log file only. Safe from any thread.
    private static void AppendLog(string line) => AppLogger.Info(line);

    // An action's result: logged, then raised so the tray routes it (in-window notice
    // while this window is shown, Windows balloon otherwise).
    private void ShowStatus(string message, bool ok)
    {
        AppLogger.Write(ok ? "INFO" : "ERROR", message);
        StatusRaised?.Invoke(message, ok ? NoticeKind.Success : NoticeKind.Error);
    }

    // Record a notice in the bell history and, when the window is on screen, show the toast.
    // With the history menu open the menu itself is refreshed instead. Safe from any thread.
    internal void PostNotice(string message, NoticeKind kind)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string, NoticeKind>(PostNotice), message, kind);
            return;
        }

        _notices.Add((DateTime.Now, message, kind));
        if (_notices.Count > NoticeHistoryCap) _notices.RemoveAt(0);

        if (_bellMenu.Visible)
        {
            RenderNoticeHistory();
            return;
        }
        _bell.HasUnread = true;
        if (IsOnScreen) _toast.ShowNotice(message, kind);
        else _toast.HideNotice(); // drop a stale "Logging..." so it is not there on reopen
    }

    internal bool IsOnScreen => Visible && WindowState != FormWindowState.Minimized;

    // The window the user is looking at right now; a window covered by another app is not.
    internal bool IsForeground => IsOnScreen && ActiveForm == this;

    private void OpenNoticeHistory()
    {
        _toast.HideNotice();
        _bell.HasUnread = false;
        RenderNoticeHistory();
        ThemedMenuRenderer.ShowBelow(_bellMenu, _bell);
    }

    // Newest first; long messages are cut in the menu and shown whole in the item tooltip.
    private void RenderNoticeHistory()
    {
        _bellMenu.Items.Clear();
        if (_notices.Count == 0)
        {
            _bellMenu.Items.Add(new ToolStripMenuItem(Lang.T("No notifications yet", "Chưa có thông báo")) { Enabled = false });
            return;
        }
        // The menu opens right-aligned under the bell, so it may span from the bell's right
        // edge back to the window's left margin, less the menu's own item padding (~50px).
        var bellRight = PointToClient(_bell.PointToScreen(new Point(_bell.Width, 0))).X;
        var maxTextWidth = bellRight - 20 - 50;
        for (var index = _notices.Count - 1; index >= 0; index--)
        {
            var (time, message, kind) = _notices[index];
            var mark = kind == NoticeKind.Success ? "✓" : kind == NoticeKind.Error ? "✕" : "•";
            var prefix = $"{time:HH:mm}   {mark}  ";
            var text = FitToWidth(prefix + message, _bellMenu.Font, maxTextWidth);
            _bellMenu.Items.Add(new ToolStripMenuItem(text)
            {
                ToolTipText = text.Length < prefix.Length + message.Length ? message : null,
            });
        }
    }

    // Cut text with "..." so it renders within maxWidth pixels.
    private static string FitToWidth(string text, Font font, int maxWidth)
    {
        if (TextRenderer.MeasureText(text, font).Width <= maxWidth) return text;
        var length = text.Length;
        while (length > 1 && TextRenderer.MeasureText(text[..length] + "...", font).Width > maxWidth) length--;
        return text[..length].TrimEnd() + "...";
    }

    // Open the JQL editor for the "My tickets" query; on save, apply it to the live
    // service, persist it, and re-fetch the list with the new query.
    private void EditJql()
    {
        if (_service is null)
        {
            SetSuggestionStatus(() => Lang.T("Config not loaded; cannot edit query.", "Chưa có cấu hình; không sửa được truy vấn."));
            return;
        }

        using var dlg = new JqlForm(_service.MyTicketsJql, JiraClient.DefaultMyTicketsJql,
            _service.ValidateMyTicketsJqlAsync);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _service.SetMyTicketsJql(dlg.Jql);
        AppConfig.SaveUserConfig(new Dictionary<string, string> { [AppConfig.MyTicketsJqlKey] = dlg.Jql });
        LoadMyTicketsAsync();
    }

    // Load the user's open Jira tickets into the suggestions panel (click to add).
    private async void LoadMyTicketsAsync()
    {
        if (_service is null)
        {
            SetSuggestionStatus(() => Lang.T("Config not loaded; cannot fetch tickets.", "Chưa có cấu hình; không tải được ticket."));
            return;
        }

        _verify.Clear(); // Refresh forces a fresh Jira check for every shown ticket
        _refreshBtn.Enabled = false;
        _loadingSuggestions = true;
        SetSuggestionStatus(() => Lang.T("Loading your tickets...", "Đang tải ticket của bạn..."));
        try
        {
            var tickets = await _service.GetMyTicketsAsync();
            _loadingSuggestions = false;
            RenderSuggestions(tickets);
        }
        catch (Exception ex)
        {
            SetSuggestionStatus(() => Lang.T($"Could not load tickets: {ex.Message}", $"Không tải được ticket: {ex.Message}"));
            AppendLog($"[jira] my-tickets error: {ex.Message}");
        }
        finally
        {
            _loadingSuggestions = false;
            UpdateActionState();
            UpdateWillLog();
            VerifyTicketsAsync(); // re-verify typed/queued tickets after the cache clear
        }
    }

    private void RenderSuggestions(IReadOnlyList<JiraSuggestion> tickets)
    {
        _lastSuggestions = tickets;
        foreach (var s in tickets) _verify[s.Key] = (VState.Valid, s.Summary);
        ClearRows(_suggestions);
        if (tickets.Count == 0)
        {
            SetSuggestionStatus(() => Lang.T("No open tickets found.", "Không có ticket nào đang mở."));
            return;
        }

        var shown = FilterSuggestions(tickets, _ticketSearch.Text);
        if (shown.Count == 0)
        {
            SetSuggestionStatus(() => Lang.T("No tickets match the search.", "Không có ticket nào khớp."));
            return;
        }

        _suggestionStatus.Text = "";
        var rowWidth = _suggestions.ClientSize.Width - 8;
        if (rowWidth < 100) rowWidth = _suggestions.Width - 12;
        // Leave room for the vertical scrollbar once the rows overflow the fixed height.
        if (shown.Count * SuggestionRowPitch > _suggestions.ClientSize.Height)
            rowWidth -= SystemInformation.VerticalScrollBarWidth;

        foreach (var t in shown)
            _suggestions.Controls.Add(CreateSuggestionRow(t, rowWidth));
    }

    // Case-insensitive match on key or summary; a blank query keeps every ticket.
    internal static IReadOnlyList<JiraSuggestion> FilterSuggestions(IReadOnlyList<JiraSuggestion> tickets, string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return tickets;
        return tickets
            .Where(t => t.Key.Contains(q, StringComparison.OrdinalIgnoreCase)
                || t.Summary.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
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

    // Temperature colour for a ticket's due date: hot when due today/overdue, cool when
    // far out or unset. Because the My-tickets list is sorted by due date, this reads as
    // a hot-at-top, cool-at-bottom gradient.
    private static Color DueColor(string? dueDate)
        => SampleRamp(Theme.Dark ? HeatDark : HeatLight, DueUrgency(dueDate));

    // 0 = coolest (far out / no due date), 1 = hottest (due today or overdue).
    private static double DueUrgency(string? dueDate)
    {
        if (string.IsNullOrWhiteSpace(dueDate)
            || !DateTime.TryParse(dueDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var due))
            return 0;

        var days = (due.Date - DateTime.Today).TotalDays;
        if (days <= 0) return 1;
        if (days >= DueHeatMaxDays) return 0;
        return 1 - days / DueHeatMaxDays;
    }

    // Linear RGB interpolation across an ordered colour ramp at t in [0,1].
    private static Color SampleRamp(Color[] ramp, double t)
    {
        if (t <= 0) return ramp[0];
        if (t >= 1) return ramp[^1];

        var scaled = t * (ramp.Length - 1);
        var i = (int)Math.Floor(scaled);
        var f = scaled - i;
        var a = ramp[i];
        var b = ramp[i + 1];
        return Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * f),
            (int)Math.Round(a.G + (b.G - a.G) * f),
            (int)Math.Round(a.B + (b.B - a.B) * f));
    }

    // A clickable row: a per-ticket colour bar + coloured key, then the summary right
    // after it (measured, so name and description sit close), with a bottom separator.
    private Control CreateSuggestionRow(JiraSuggestion ticket, int width)
    {
        var color = DueColor(ticket.DueDate);
        var dueLabel = FormatDue(ticket.DueDate);
        var accessibleName = dueLabel.Length != 0
            ? $"{ticket.Key}, {ticket.Summary}, {Lang.T("due", "hạn")} {dueLabel}"
            : $"{ticket.Key}, {ticket.Summary}";

        var row = new ClickableRow
        {
            Width = width,
            Height = SuggestionRowPitch,
            Margin = new Padding(0),
            BackColor = Theme.InputBg,
            Cursor = Cursors.Hand,
            AccessibleName = accessibleName,
        };

        var bar = new Panel { Location = new Point(0, 0), Size = new Size(4, 30), BackColor = color };

        var keyWidth = TextRenderer.MeasureText(ticket.Key, KeyFont).Width;
        var key = new Label
        {
            Text = ticket.Key,
            AutoSize = false,
            Location = new Point(12, 0),
            Size = new Size(keyWidth, 30),
            TextAlign = ContentAlignment.MiddleLeft,
            Font = KeyFont,
            ForeColor = color,
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand,
        };

        // Due date sits right-aligned at the row's end; the summary shrinks to leave room.
        var dueText = dueLabel;
        var dueWidth = dueText.Length == 0 ? 0 : TextRenderer.MeasureText(dueText, SummaryFont).Width + 8;

        var summaryX = 12 + keyWidth + 8;
        var summary = new Label
        {
            Text = ticket.Summary,
            AutoSize = false,
            Location = new Point(summaryX, 0),
            Size = new Size(Math.Max(20, width - summaryX - dueWidth - 8), 30),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Font = SummaryFont,
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand,
        };

        Label? due = null;
        if (dueWidth != 0)
        {
            due = new Label
            {
                Text = dueText,
                AutoSize = false,
                Location = new Point(width - dueWidth - 8, 0),
                Size = new Size(dueWidth, 30),
                TextAlign = ContentAlignment.MiddleRight,
                Font = SummaryFont,
                ForeColor = Theme.TextSecondary,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
            };
        }

        var separator = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Divider };

        void Add(object? s, EventArgs e) => AddTicketToInput(ticket.Key);
        void Enter(object? s, EventArgs e) => row.BackColor = Theme.Hover;
        void Leave(object? s, EventArgs e)
        {
            if (!row.ClientRectangle.Contains(row.PointToClient(Cursor.Position)))
                row.BackColor = Theme.InputBg;
        }

        var controls = due is null
            ? new Control[] { row, bar, key, summary }
            : new Control[] { row, bar, key, summary, due };
        foreach (var c in controls)
        {
            c.Click += Add;
            c.MouseEnter += Enter;
            c.MouseLeave += Leave;
        }

        row.Controls.Add(bar);
        row.Controls.Add(key);
        row.Controls.Add(summary);
        if (due is not null) row.Controls.Add(due);
        row.Controls.Add(separator);
        return row;
    }

    // Format a Jira ISO due date ("yyyy-MM-dd") as a short label ("Oct 1" / "01/10"); empty when unset.
    private static string FormatDue(string? due)
    {
        if (string.IsNullOrWhiteSpace(due)) return "";
        return DateTime.TryParse(due, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? Lang.ShortDate(dt)
            : due;
    }

    // Picking a suggestion hands focus back to the ticket box, so Enter queues right away.
    private void AddTicketToInput(string key)
    {
        var (existing, _) = TicketParser.Parse(_tickets.Text);
        if (!existing.Contains(key)) _tickets.Text = string.Join(", ", existing.Append(key));
        FocusTicketInput();
    }

    private void FocusTicketInput()
    {
        _tickets.Focus();
        _tickets.SelectionStart = _tickets.TextLength;
    }

    private void SetSuggestionStatus(Func<string> message)
    {
        ClearRows(_suggestions);
        _suggestionStatusText = message;
        _suggestionStatus.Text = message();
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
        _willLogList.SuspendLayout();
        ClearRows(_willLogList);

        // Leave room for the vertical scrollbar so rows never trigger a horizontal one.
        var rowWidth = Math.Max(140, _willLogList.ClientSize.Width - 24);
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
            _willLogList.Controls.Add(CreateWillLogRow(date, tickets[i], TimeSlots.Get(mins, i), rowWidth));
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
        Height = 20,
        Margin = new Padding(0),
        Font = WillLogFont,
        ForeColor = Theme.TextSecondary,
        BackColor = Color.Transparent,
        TextAlign = ContentAlignment.MiddleLeft,
        UseMnemonic = false,
    };

    private Control CreateWillLogRow(string date, string key, IReadOnlyList<TimeSlot> slots, int width)
    {
        var row = new WillLogRow
        {
            Width = width,
            Key = key,
            KeyColor = TicketColor(key),
            Slots = SlotText(slots),
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
            DotColor = DotColorFor(key),
            Index = index,
            Hours = Math.Round(minutes / 60.0, 2),
        };
        row.HoursChanged += OnHoursChanged;
        return row;
    }

    private static string SlotText(IReadOnlyList<TimeSlot> slots)
        => string.Join("  /  ", slots.Select(s => $"{s.Start}-{s.End}"));

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
    // the window shows real status dots. Known suggestions are pre-marked valid.
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
        _toast.ShowNotice(Lang.T("Logging the queue...", "Đang log hàng đợi..."), NoticeKind.Info, sticky: true);
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
        UpdateWillLog();
        VerifyTicketsAsync();
    }

    // Focus the window on a specific date and put the cursor in the ticket box. Used by
    // the Weekly check to jump straight to a day that needs logging. SetDate marks it a
    // deliberate pick so OnActivated's today-sync will not override it; ValueChanged then
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
        finally { SetBusy(false); }
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
        finally { SetBusy(false); }
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
        finally { SetBusy(false); }
    }

    // The only path that overwrites, hence the confirm: one click, no other input, on a
    // workbook everyone reads.
    private async void OnLogOff(object? sender, EventArgs e)
    {
        if (_service is null) { ShowStatus(Lang.T("Config not loaded; cannot log.", "Chưa có cấu hình; không log được."), false); return; }

        var date = DateOnly.FromDateTime(_date.Value.Date);
        var answer = MessageBox.Show(this,
            Lang.T($"Write \"{TscCells.OffMarker}\" to TSC for {Lang.LongDate(_date.Value.Date)}?\n\n"
                    + "Anything already in that day's cells will be replaced, and any queued tickets for it dropped.",
                $"Ghi \"{TscCells.OffMarker}\" vào TSC cho {Lang.LongDate(_date.Value.Date)}?\n\n"
                    + "Mọi nội dung đang có trong ô của ngày đó sẽ bị thay thế, và các ticket đang chờ của ngày đó sẽ bị bỏ."),
            Lang.T("Mark the day off", "Đánh dấu ngày nghỉ"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

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
        finally { SetBusy(false); }
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
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        _logNowBtn.Text = busy ? BusyText : LogNowText;
        if (busy && busyNotice != null) _toast.ShowNotice(busyNotice, NoticeKind.Info, sticky: true);
        if (!busy && _toast.IsSticky) _toast.HideNotice();
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
        _refreshBtn.Enabled = !_busy;
    }

    // Re-read the persisted queue each time the window is focused (e.g. reopened from
    // the tray, or after a scheduled drain happened while it was hidden).
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _date.SyncToTodayIfAuto();
        RefreshQueuedView();
    }

    // Every time the window is shown, start in the ticket box so you can type right away.
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) BeginInvoke(FocusTicketInput);
    }

    // Esc hides the window to the tray, except while it is clearing a non-empty search.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Enter in the search box picks the top match; it must never fall through to
        // AcceptButton and queue whatever is already in the ticket box.
        if (keyData == Keys.Enter && _ticketSearch.Focused)
        {
            var matches = FilterSuggestions(_lastSuggestions, _ticketSearch.Text);
            if (_ticketSearch.Text.Trim().Length != 0 && matches.Count != 0)
            {
                _ticketSearch.Text = string.Empty;
                AddTicketToInput(matches[0].Key);
            }
            return true;
        }

        var clearingSearch = _ticketSearch.Focused && _ticketSearch.TextLength != 0;
        if (keyData == Keys.Escape && !clearingSearch)
        {
            Close(); // OnFormClosing turns a user close into hide-to-tray
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // Hide to tray on the user's X click instead of exiting the process.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            SaveWindowBounds();
            _date.ForgetManualPick();
            // The date resets to today on reopen, so a half-typed entry would land on the wrong day.
            _tickets.Text = string.Empty;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}
