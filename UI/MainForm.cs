using System.Drawing;

namespace NoisLogTray;

// The capture window, structured like the old web app: a header (with a light/dark
// toggle), then stacked cards -- "Log entries" (My tickets + date/ticket), "Will
// log" (preview), and "Actions". A top status bar streams every activity line and
// the coloured result; TSC/HRM progress bars show below it during a log run.
// Closing (X) hides to the tray; TrayApp owns the process lifetime.
internal sealed class MainForm : Form
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

    private const int CardW = 560;
    private const int InnerW = 528; // CardW - 2*16

    private readonly LoggingService? _service;

    private readonly Panel _body = new();
    private readonly Panel _header = new();
    private readonly Label _headerTitle = new();
    private readonly Label _headerSubtitle = new();
    private readonly ThemeToggleButton _themeBtn = new();
    private readonly List<Label> _sectionLabels = new();
    private readonly List<Panel> _dividers = new();

    private readonly RoundedDatePicker _date = new();
    private readonly TextBox _tickets = new();
    private readonly MacButton _queueBtn = MacButton.Primary("Queue for 6 PM");
    private readonly MacButton _logNowBtn = MacButton.Secondary("Log now (TSC + HRM)");
    private readonly MacButton _logTscBtn = MacButton.Secondary("Log TSC");
    private readonly MacButton _logHrmBtn = MacButton.Secondary("Log HRM");
    private readonly MacButton _checkBtn = MacButton.Secondary("Check TSC");
    private readonly MacButton _reauthBtn = MacButton.Secondary("Re-auth");
    private readonly MacButton _refreshBtn = MacButton.Secondary("Refresh");
    private readonly MacButton _clearBtn = MacButton.Secondary("Clear");
    private readonly FlowLayoutPanel _suggestions = new();
    private readonly Label _suggestionStatus = new();
    private readonly FlowLayoutPanel _willLogList = new();
    private readonly System.Windows.Forms.Timer _verifyTimer = new() { Interval = 500 };
    private readonly Dictionary<string, (VState State, string? Title)> _verify = new();
    private readonly Label _queuedLabel = new();
    private readonly MacButton _clearQueueBtn = MacButton.Secondary("Clear");

    private Card? _willLogCard;
    private RoundedHost? _willLogHost;
    private Card? _queuedCard;
    private Card? _actionsCard;

    private readonly Panel _statusBar = new();
    private readonly Label _status = new();
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 6000 };

    private readonly Panel _progressPanel = new();
    private readonly MiniProgress _tscBar = new();
    private readonly MiniProgress _hrmBar = new();
    private readonly Label _tscLabel = new();
    private readonly Label _hrmLabel = new();
    private readonly Label _tscPct = new();
    private readonly Label _hrmPct = new();

    private IReadOnlyList<JiraSuggestion> _lastSuggestions = Array.Empty<JiraSuggestion>();
    private bool _busy;

    private enum VState { Verifying, Valid, NotFound, Error }

    internal event Action? QueueChanged;

    internal MainForm(LoggingService? service, string? configError)
    {
        _service = service;
        BuildLayout();
        Theme.Changed += ApplyTheme;
        RefreshQueuedView(); // also renders the Will log (falls back to the queue)
        if (configError != null) AppendLog($"[config] {configError}");
        LoadMyTicketsAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= ApplyTheme;
        base.Dispose(disposing);
    }

    private void BuildLayout()
    {
        Text = "NOIS Daily Log";
        Icon = AppIcon.Load(32);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Theme.WindowBg;
        ClientSize = new Size(600, 868);
        RestoreWindowPosition();

        BuildBody();
        BuildProgressPanel();
        BuildStatusBar();
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

    // Persist the window position (read-modify-write so the theme key is preserved).
    private void SaveWindowPosition()
    {
        if (WindowState != FormWindowState.Normal) return;
        var settings = AppSettings.Load();
        settings.WindowX = Location.X;
        settings.WindowY = Location.Y;
        AppSettings.Save(settings);
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        SaveWindowPosition();
    }

    private void BuildBody()
    {
        _body.Dock = DockStyle.Fill;
        _body.BackColor = Theme.WindowBg;
        _body.AutoScroll = true;

        var y = 14;
        var header = BuildHeader();
        header.Location = new Point(20, y);
        _body.Controls.Add(header);
        y += header.Height + 8;

        var entries = BuildEntriesCard();
        entries.Location = new Point(20, y);
        _body.Controls.Add(entries);
        y += entries.Height + 12;

        var willLog = BuildWillLogCard();
        willLog.Location = new Point(20, y);
        _body.Controls.Add(willLog);
        y += willLog.Height + 12;

        var queued = BuildQueuedCard();
        queued.Location = new Point(20, y);
        _body.Controls.Add(queued);
        y += queued.Height + 12;

        var actions = BuildActionsCard();
        actions.Location = new Point(20, y);
        _body.Controls.Add(actions);

        AcceptButton = _queueBtn;
        Controls.Add(_body);
    }

    private Panel BuildHeader()
    {
        _header.Size = new Size(CardW, 76);
        _header.BackColor = Theme.WindowBg;

        _headerTitle.Text = "NOIS Daily Log";
        _headerTitle.AutoSize = true;
        _headerTitle.Location = new Point(0, 4);
        _headerTitle.Font = new Font("Segoe UI Semibold", 20F, FontStyle.Bold);
        _headerTitle.ForeColor = Theme.TextPrimary;

        var now = Hcm.Now();
        _headerSubtitle.Text = $"{now:dddd}    /    {now:MMMM d}    /    {now:yyyy}".ToUpperInvariant();
        _headerSubtitle.AutoSize = true;
        _headerSubtitle.Location = new Point(3, 48);
        _headerSubtitle.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
        _headerSubtitle.ForeColor = Theme.TextSecondary;

        _themeBtn.OnWindow = true;
        _themeBtn.Size = new Size(34, 30);
        _themeBtn.Location = new Point(CardW - 34, 12);

        _header.Controls.Add(_headerTitle);
        _header.Controls.Add(_headerSubtitle);
        _header.Controls.Add(_themeBtn);
        return _header;
    }

    private Card BuildEntriesCard()
    {
        var card = new Card { Size = new Size(CardW, 342) };
        card.Controls.Add(SectionLabel("LOG ENTRIES", 16, 14));
        card.Controls.Add(SectionLabel("MY TICKETS  (click to add)", 16, 42));

        _refreshBtn.Size = new Size(90, 26);
        _refreshBtn.Location = new Point(16 + InnerW - 90, 38);
        _refreshBtn.Click += (_, _) => LoadMyTicketsAsync();
        card.Controls.Add(_refreshBtn);

        var sugHost = new RoundedHost { Location = new Point(16, 68), Size = new Size(InnerW, 204) };
        _suggestions.Dock = DockStyle.Fill;
        _suggestions.FlowDirection = FlowDirection.TopDown;
        _suggestions.WrapContents = false;
        _suggestions.AutoScroll = false;
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

        card.Controls.Add(SectionLabel("DATE", 16, 282));
        card.Controls.Add(SectionLabel("TICKET", 214, 282));

        _date.Location = new Point(16, 300);
        _date.Size = new Size(190, 30);
        _date.ValueChanged += (_, _) => { UpdateWillLog(); VerifyTicketsAsync(); };

        var ticketHost = new RoundedHost { Location = new Point(214, 300), Size = new Size(262, 30) };
        _tickets.BorderStyle = BorderStyle.None;
        _tickets.Font = new Font("Segoe UI", 9.5F);
        _tickets.BackColor = Theme.InputBg;
        _tickets.ForeColor = Theme.TextPrimary;
        _tickets.PlaceholderText = "e.g. 1234, 5678  (MDP- optional)";
        _tickets.TextChanged += (_, _) => { UpdateWillLog(); UpdateActionState(); _verifyTimer.Stop(); _verifyTimer.Start(); };
        _tickets.Leave += (_, _) => { _verifyTimer.Stop(); VerifyTicketsAsync(); };
        var ticketH = _tickets.PreferredHeight;
        _tickets.SetBounds(10, (ticketHost.Height - ticketH) / 2, ticketHost.Width - 20, ticketH);
        ticketHost.Controls.Add(_tickets);

        _clearBtn.Size = new Size(64, 30); // match the date/ticket input height
        _clearBtn.Location = new Point(480, 300);
        _clearBtn.Click += (_, _) => _tickets.Text = string.Empty;

        card.Controls.Add(sugHost);
        card.Controls.Add(_date);
        card.Controls.Add(ticketHost);
        card.Controls.Add(_clearBtn);
        return card;
    }

    private Card BuildWillLogCard()
    {
        var card = new Card { Size = new Size(CardW, 90) };
        card.Controls.Add(SectionLabel("WILL LOG", 16, 14));

        var host = new RoundedHost { Location = new Point(16, 38), Size = new Size(InnerW, 40) };
        _willLogList.Dock = DockStyle.Fill;
        _willLogList.FlowDirection = FlowDirection.TopDown;
        _willLogList.WrapContents = false;
        _willLogList.AutoScroll = false;
        _willLogList.BackColor = Theme.InputBg;
        _willLogList.Padding = new Padding(6, 4, 6, 4);
        host.Controls.Add(_willLogList);

        _verifyTimer.Tick += (_, _) => { _verifyTimer.Stop(); VerifyTicketsAsync(); };

        card.Controls.Add(host);
        _willLogCard = card;
        _willLogHost = host;
        return card;
    }

    private Card BuildQueuedCard()
    {
        var card = new Card { Size = new Size(CardW, 98) };
        card.Controls.Add(SectionLabel("QUEUED FOR 6 PM", 16, 14));

        _clearQueueBtn.Size = new Size(90, 26);
        _clearQueueBtn.Location = new Point(16 + InnerW - 90, 10);
        _clearQueueBtn.Click += OnClearQueue;
        card.Controls.Add(_clearQueueBtn);

        var host = new RoundedHost { Location = new Point(16, 42), Size = new Size(InnerW, 40) };
        _queuedLabel.Dock = DockStyle.Fill;
        _queuedLabel.TextAlign = ContentAlignment.MiddleLeft;
        _queuedLabel.Font = new Font("Segoe UI", 9F);
        _queuedLabel.ForeColor = Theme.TextPrimary;
        _queuedLabel.BackColor = Theme.InputBg;
        _queuedLabel.Padding = new Padding(8, 0, 8, 0);
        host.Controls.Add(_queuedLabel);

        card.Controls.Add(host);
        _queuedCard = card;
        return card;
    }

    private Card BuildActionsCard()
    {
        var card = new Card { Size = new Size(CardW, 132) };
        card.Controls.Add(SectionLabel("ACTIONS", 16, 14));

        // Two rows on a shared column grid (12px gutter). The 2-up top row and the
        // 4-up bottom row line up: each top button spans exactly two bottom columns,
        // so the left/center/right edges match across both rows.
        const int gap = 12;
        const int half = (InnerW - gap) / 2;      // 258: top-row button width
        const int quarter = (InnerW - 3 * gap) / 4; // 123: bottom-row button width
        var col2 = 16 + half + gap;               // 286: start of the right column

        _queueBtn.Size = new Size(half, 40);
        _queueBtn.Location = new Point(16, 36);
        _queueBtn.Click += OnQueue;

        _logNowBtn.Size = new Size(half, 40);
        _logNowBtn.Location = new Point(col2, 36);
        _logNowBtn.Click += OnLogNow;

        _logTscBtn.Size = new Size(quarter, 32);
        _logTscBtn.Location = new Point(16, 84);
        _logTscBtn.Click += OnLogTsc;

        _logHrmBtn.Size = new Size(quarter, 32);
        _logHrmBtn.Location = new Point(16 + quarter + gap, 84);
        _logHrmBtn.Click += OnLogHrm;

        _checkBtn.Size = new Size(quarter, 32);
        _checkBtn.Location = new Point(col2, 84);
        _checkBtn.Click += OnCheckTsc;

        _reauthBtn.Size = new Size(quarter, 32);
        _reauthBtn.Location = new Point(col2 + quarter + gap, 84);
        _reauthBtn.Click += OnReauth;

        card.Controls.Add(_queueBtn);
        card.Controls.Add(_logNowBtn);
        card.Controls.Add(_logTscBtn);
        card.Controls.Add(_logHrmBtn);
        card.Controls.Add(_checkBtn);
        card.Controls.Add(_reauthBtn);
        _actionsCard = card;
        return card;
    }

    private Label SectionLabel(string text, int x, int y)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Location = new Point(x, y),
            Font = SectionFont,
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
        };
        _sectionLabels.Add(label);
        return label;
    }

    // Re-apply theme colors to the native controls (custom controls repaint
    // themselves via Theme.Changed) and re-render the suggestion rows.
    private void ApplyTheme()
    {
        BackColor = Theme.WindowBg;
        _body.BackColor = Theme.WindowBg;
        _header.BackColor = Theme.WindowBg;
        _statusBar.BackColor = Theme.WindowBg;
        _progressPanel.BackColor = Theme.WindowBg;

        _headerTitle.ForeColor = Theme.TextPrimary;
        _headerSubtitle.ForeColor = Theme.TextSecondary;
        foreach (var l in _sectionLabels) l.ForeColor = Theme.TextSecondary;
        foreach (var d in _dividers) d.BackColor = Theme.Divider;

        _tickets.BackColor = Theme.InputBg;
        _tickets.ForeColor = Theme.TextPrimary;
        _suggestions.BackColor = Theme.InputBg;
        _suggestionStatus.BackColor = Theme.InputBg;
        _suggestionStatus.ForeColor = Theme.TextSecondary;
        _willLogList.BackColor = Theme.InputBg;
        _queuedLabel.BackColor = Theme.InputBg;
        _queuedLabel.ForeColor = Theme.TextPrimary;

        _tscLabel.ForeColor = Theme.TextSecondary;
        _hrmLabel.ForeColor = Theme.TextSecondary;
        _tscPct.ForeColor = Theme.TextSecondary;
        _hrmPct.ForeColor = Theme.TextSecondary;

        RenderSuggestions(_lastSuggestions);
        UpdateWillLog();
        Invalidate(true);
    }

    private void BuildStatusBar()
    {
        _statusBar.Dock = DockStyle.Top;
        _statusBar.Height = 36;
        _statusBar.BackColor = Theme.WindowBg;
        _statusBar.Visible = false;

        var line = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Divider };
        _dividers.Add(line);

        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleCenter;
        _status.Padding = new Padding(12, 4, 12, 4);
        _status.BackColor = Color.Transparent;
        _status.Font = new Font("Segoe UI", 14F, FontStyle.Bold);

        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); _status.Text = string.Empty; _statusBar.Visible = false; };

        _statusBar.Controls.Add(_status);
        _statusBar.Controls.Add(line);
        Controls.Add(_statusBar);
    }

    private void ShowStatus(string message, bool ok)
    {
        _status.Text = message;
        _status.ForeColor = ok ? Color.FromArgb(46, 160, 80) : Color.FromArgb(230, 76, 76);
        SizeStatusBar();
        _statusBar.Visible = true;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    // Grow the status bar's height to fit the full (wrapped) message -- no ellipsis.
    private void SizeStatusBar()
    {
        var width = Math.Max(100, ClientSize.Width - 28);
        var size = TextRenderer.MeasureText(_status.Text, _status.Font, new Size(width, 0),
            TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter);
        _statusBar.Height = Math.Max(36, size.Height + 16);
    }

    private void ShowActivity(string line)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(ShowActivity), line);
            return;
        }
        _status.Text = line;
        _status.ForeColor = Theme.TextPrimary;
        SizeStatusBar();
        _statusBar.Visible = true;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    // Every activity line goes to the log file AND the top status bar (live feed).
    internal void AppendLog(string line)
    {
        AppLogger.Info(line);
        ShowActivity(line);
    }

    // A TSC and/or HRM progress bar shown just below the status bar during a log.
    private void BuildProgressPanel()
    {
        _progressPanel.Dock = DockStyle.Top;
        _progressPanel.Height = 54;
        _progressPanel.BackColor = Theme.WindowBg;
        _progressPanel.Visible = false;

        var line = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Divider };
        _dividers.Add(line);
        _progressPanel.Controls.Add(line);

        ConfigureProgressRow(_tscLabel, "TSC", _tscBar, _tscPct, 10);
        ConfigureProgressRow(_hrmLabel, "HRM", _hrmBar, _hrmPct, 32);

        Controls.Add(_progressPanel);
    }

    private void ConfigureProgressRow(Label label, string text, MiniProgress bar, Label pct, int y)
    {
        label.Text = text;
        label.AutoSize = false;
        label.Bounds = new Rectangle(20, y - 2, 36, 16);
        label.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
        label.ForeColor = Theme.TextSecondary;
        label.BackColor = Color.Transparent;
        label.TextAlign = ContentAlignment.MiddleLeft;

        bar.Bounds = new Rectangle(58, y + 3, 476, 6);

        pct.Text = "0%";
        pct.AutoSize = false;
        pct.Bounds = new Rectangle(540, y - 2, 44, 16);
        pct.Font = new Font("Segoe UI", 8.5F);
        pct.ForeColor = Theme.TextSecondary;
        pct.BackColor = Color.Transparent;
        pct.TextAlign = ContentAlignment.MiddleRight;

        _progressPanel.Controls.Add(label);
        _progressPanel.Controls.Add(bar);
        _progressPanel.Controls.Add(pct);
    }

    private void ShowProgress(bool tsc, bool hrm)
    {
        _tscLabel.Visible = _tscBar.Visible = _tscPct.Visible = tsc;
        _hrmLabel.Visible = _hrmBar.Visible = _hrmPct.Visible = hrm;
        _tscBar.SetFraction(0);
        _hrmBar.SetFraction(0);
        _tscPct.Text = "0%";
        _hrmPct.Text = "0%";
        _progressPanel.Visible = true;
    }

    private void HideProgress() => _progressPanel.Visible = false;

    private void ReportTsc(int done, int total) => ReportProgress(_tscBar, _tscPct, done, total);
    private void ReportHrm(int done, int total) => ReportProgress(_hrmBar, _hrmPct, done, total);

    private void ReportProgress(MiniProgress bar, Label pct, int done, int total)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ReportProgress(bar, pct, done, total)));
            return;
        }
        var fraction = total > 0 ? (double)done / total : 0;
        bar.SetFraction(fraction);
        pct.Text = $"{(int)Math.Round(fraction * 100)}%";
    }

    // Load the user's open Jira tickets into the suggestions panel (click to add).
    private async void LoadMyTicketsAsync()
    {
        if (_service is null)
        {
            SetSuggestionStatus("Config not loaded; cannot fetch tickets.");
            return;
        }

        _refreshBtn.Enabled = false;
        SetSuggestionStatus("Loading your tickets...");
        try
        {
            var tickets = await _service.GetMyTicketsAsync(6);
            RenderSuggestions(tickets);
        }
        catch (Exception ex)
        {
            SetSuggestionStatus($"Could not load tickets: {ex.Message}");
            AppendLog($"[jira] my-tickets error: {ex.Message}");
        }
        finally
        {
            UpdateActionState();
            UpdateWillLog();
        }
    }

    private void RenderSuggestions(IReadOnlyList<JiraSuggestion> tickets)
    {
        _lastSuggestions = tickets;
        foreach (var s in tickets) _verify[s.Key] = (VState.Valid, s.Summary);
        ClearRows(_suggestions);
        if (tickets.Count == 0)
        {
            SetSuggestionStatus("No open tickets found.");
            return;
        }

        _suggestionStatus.Text = "";
        var rowWidth = _suggestions.ClientSize.Width - 8;
        if (rowWidth < 100) rowWidth = _suggestions.Width - 12;

        foreach (var t in tickets)
            _suggestions.Controls.Add(CreateSuggestionRow(t, rowWidth));
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

    // A clickable row: a per-ticket colour bar + coloured key, then the summary right
    // after it (measured, so name and description sit close), with a bottom separator.
    private Control CreateSuggestionRow(JiraSuggestion ticket, int width)
    {
        var color = TicketColor(ticket.Key);

        var row = new Panel
        {
            Width = width,
            Height = 31,
            Margin = new Padding(0),
            BackColor = Theme.InputBg,
            Cursor = Cursors.Hand,
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

        var summaryX = 12 + keyWidth + 8;
        var summary = new Label
        {
            Text = ticket.Summary,
            AutoSize = false,
            Location = new Point(summaryX, 0),
            Size = new Size(Math.Max(20, width - summaryX - 8), 30),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Font = SummaryFont,
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand,
        };

        var separator = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Divider };

        void Add(object? s, EventArgs e) => AddTicketToInput(ticket.Key);
        void Enter(object? s, EventArgs e) => row.BackColor = Theme.Hover;
        void Leave(object? s, EventArgs e)
        {
            if (!row.ClientRectangle.Contains(row.PointToClient(Cursor.Position)))
                row.BackColor = Theme.InputBg;
        }

        foreach (var c in new Control[] { row, bar, key, summary })
        {
            c.Click += Add;
            c.MouseEnter += Enter;
            c.MouseLeave += Leave;
        }

        row.Controls.Add(bar);
        row.Controls.Add(key);
        row.Controls.Add(summary);
        row.Controls.Add(separator);
        return row;
    }

    private void AddTicketToInput(string key)
    {
        var (existing, _) = TicketParser.Parse(_tickets.Text);
        if (existing.Contains(key)) return;
        _tickets.Text = string.Join(", ", existing.Append(key));
    }

    private void SetSuggestionStatus(string message)
    {
        ClearRows(_suggestions);
        _suggestionStatus.Text = message;
    }

    // The tickets currently previewed in "Will log": what is typed, or - when nothing
    // is typed - the queued tickets for the selected date, so reopening the window
    // still shows what is scheduled to log at 6 PM.
    private IReadOnlyList<string> PreviewTickets(out bool fromQueue)
    {
        fromQueue = false;
        var (typed, _) = TicketParser.Parse(_tickets.Text);
        if (typed.Count != 0) return typed;

        var date = DateOnly.FromDateTime(_date.Value.Date).ToString("yyyy-MM-dd");
        var entry = TicketQueue.Read().FirstOrDefault(e => e.Date == date);
        if (entry != null && entry.Tickets.Count != 0)
        {
            fromQueue = true;
            return entry.Tickets;
        }
        return typed;
    }

    // Preview what will be logged: the date, then each ticket with its Jira
    // verification status (green = valid + title, red = not found, amber = error) and
    // its time slots.
    private void UpdateWillLog()
    {
        if (_willLogList.IsDisposed) return;
        _willLogList.SuspendLayout();
        ClearRows(_willLogList);

        var tickets = PreviewTickets(out var fromQueue);
        var rowWidth = Math.Max(140, _willLogList.ClientSize.Width - 8);

        if (tickets.Count == 0)
        {
            _willLogList.Controls.Add(WillLogText("Enter a ticket above to preview what will be logged.", rowWidth));
        }
        else
        {
            var header = _date.Value.ToString("dddd, MMMM d, yyyy") + (fromQueue ? "   (queued for 6 PM)" : "");
            _willLogList.Controls.Add(WillLogText(header, rowWidth));
            for (var i = 0; i < tickets.Count; i++)
                _willLogList.Controls.Add(CreateWillLogRow(tickets[i], TimeSlots.Get(tickets.Count, i), rowWidth));
        }

        _willLogList.ResumeLayout();
        FitWillLogToContent();
    }

    // Grow the "Will log" card to fit all rows (no inner scroll) and push the
    // cards below it down so nothing overlaps.
    private void FitWillLogToContent()
    {
        if (_willLogHost is null || _willLogCard is null) return;

        var content = _willLogList.Padding.Vertical;
        foreach (Control c in _willLogList.Controls)
            content += c.Height + c.Margin.Vertical;

        var hostH = Math.Max(40, content + _willLogHost.Padding.Vertical);
        _willLogHost.Height = hostH;
        _willLogCard.Height = _willLogHost.Top + hostH + 12;

        if (_queuedCard != null)
        {
            _queuedCard.Top = _willLogCard.Bottom + 12;
            if (_actionsCard != null) _actionsCard.Top = _queuedCard.Bottom + 12;
        }
    }

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

    private Control CreateWillLogRow(string key, IReadOnlyList<TimeSlot> slots, int width)
    {
        var slotText = string.Join("  /  ", slots.Select(s => $"{s.Start}-{s.End}"));

        var dotColor = Color.FromArgb(150, 150, 156);
        if (_verify.TryGetValue(key, out var v))
        {
            dotColor = v.State switch
            {
                VState.Valid => Color.FromArgb(46, 160, 80),
                VState.NotFound => Color.FromArgb(230, 76, 76),
                VState.Error => Color.FromArgb(217, 164, 0),
                _ => dotColor,
            };
        }

        return new WillLogRow
        {
            Width = width,
            Key = key,
            KeyColor = TicketColor(key),
            Slots = slotText,
            DotColor = dotColor,
        };
    }

    // Verify each previewed ticket against Jira (debounced), showing valid+title or
    // not-found in the "Will log" list. Covers the queued fallback too, so reopening
    // the window shows real status dots. Known suggestions are pre-marked valid.
    private async void VerifyTicketsAsync()
    {
        if (_service is null) return;
        var tickets = PreviewTickets(out _);
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
            AppendLog("[queue] No valid tickets to queue.");
            return;
        }

        var date = DateOnly.FromDateTime(_date.Value.Date).ToString("yyyy-MM-dd");
        var entries = TicketQueue.Read().ToList();
        var idx = entries.FindIndex(x => x.Date == date);
        if (idx >= 0)
        {
            var merged = entries[idx].Tickets.ToList();
            foreach (var t in tickets)
                if (!merged.Contains(t)) merged.Add(t);
            entries[idx] = new QueueEntry(date, merged);
        }
        else
        {
            entries.Add(new QueueEntry(date, tickets.ToList()));
        }

        TicketQueue.Write(entries.OrderBy(x => x.Date).ToList());
        AppendLog($"[queue] Queued {date}: {string.Join(", ", tickets)}");
        ShowStatus($"Queued {tickets.Count} ticket{(tickets.Count == 1 ? "" : "s")} for {date} (auto-logs at 6 PM).", true);
        RefreshQueuedView();
        QueueChanged?.Invoke();
    }

    private void OnClearQueue(object? sender, EventArgs e)
    {
        TicketQueue.Write(Array.Empty<QueueEntry>());
        RefreshQueuedView();
        ShowStatus("Queue cleared.", true);
        QueueChanged?.Invoke();
    }

    // Read the persisted 6 PM queue and show it (read-only) so it stays visible after
    // a relaunch. Refreshed on queue/clear, when the window activates, and after a drain.
    // Also refreshes the "Will log" preview, which falls back to the queue when the
    // input is empty.
    internal void RefreshQueuedView()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(RefreshQueuedView));
            return;
        }
        var entries = TicketQueue.Read();
        if (entries.Count == 0)
        {
            _queuedLabel.Text = "Nothing queued.";
            _clearQueueBtn.Enabled = false;
        }
        else
        {
            _queuedLabel.Text = string.Join(Environment.NewLine,
                entries.Select(e => $"{e.Date}:   {string.Join(", ", e.Tickets)}"));
            _clearQueueBtn.Enabled = true;
        }

        UpdateWillLog();
        VerifyTicketsAsync();
    }

    private async void OnLogNow(object? sender, EventArgs e)
    {
        if (_service is null) { AppendLog("[error] Config not loaded; cannot log."); return; }
        var (tickets, date) = ParseEntry("log");
        if (tickets is null) return;

        SetBusy(true);
        ShowProgress(true, true);
        try
        {
            AppendLog($"[log] Logging {date:yyyy-MM-dd}: {string.Join(", ", tickets)} ...");
            var token = await _service.AcquireGraphTokenAsync(AppendLog);
            var result = await _service.LogEntryAsync(date, tickets, token, AppendLog, ReportTsc, ReportHrm);
            AppendLog($"[log] TSC: {(result.TscSuccess ? "OK" : result.TscError)}");
            AppendLog($"[log] HRM: {(result.HrmSuccess ? "OK" : result.HrmError)}");
            ShowStatus(result.AllSuccess
                ? $"Logged {date:yyyy-MM-dd} to TSC + HRM."
                : $"Partly failed - TSC: {(result.TscSuccess ? "OK" : result.TscError)}; HRM: {(result.HrmSuccess ? "OK" : result.HrmError)}",
                result.AllSuccess);
        }
        catch (Exception ex)
        {
            AppendLog($"[log] Error: {ex.Message}");
            ShowStatus($"Log failed: {ex.Message}", false);
        }
        finally { SetBusy(false); HideProgress(); }
    }

    private async void OnLogTsc(object? sender, EventArgs e)
    {
        if (_service is null) { AppendLog("[error] Config not loaded; cannot log."); return; }
        var (tickets, date) = ParseEntry("tsc");
        if (tickets is null) return;

        SetBusy(true);
        ShowProgress(true, false);
        try
        {
            var (ok, cell, err) = await _service.LogTscAsync(string.Join(", ", tickets), new[] { date }, AppendLog, ReportTsc);
            AppendLog($"[tsc] {(ok ? $"OK ({cell})" : err)}");
            ShowStatus(ok ? $"TSC logged ({cell})." : $"TSC failed: {err}", ok);
        }
        catch (Exception ex)
        {
            AppendLog($"[tsc] Error: {ex.Message}");
            ShowStatus($"TSC failed: {ex.Message}", false);
        }
        finally { SetBusy(false); HideProgress(); }
    }

    private async void OnLogHrm(object? sender, EventArgs e)
    {
        if (_service is null) { AppendLog("[error] Config not loaded; cannot log."); return; }
        var (tickets, date) = ParseEntry("hrm");
        if (tickets is null) return;

        SetBusy(true);
        ShowProgress(false, true);
        try
        {
            var (ok, err) = await _service.LogHrmAsync(tickets, date, AppendLog, ReportHrm);
            AppendLog($"[hrm] {(ok ? "OK" : err)}");
            ShowStatus(ok ? "HRM logged." : $"HRM failed: {err}", ok);
        }
        catch (Exception ex)
        {
            AppendLog($"[hrm] Error: {ex.Message}");
            ShowStatus($"HRM failed: {ex.Message}", false);
        }
        finally { SetBusy(false); HideProgress(); }
    }

    private async void OnCheckTsc(object? sender, EventArgs e)
    {
        SetBusy(true);
        AppendLog("[tsc] Checking session...");
        try
        {
            var (loggedIn, error) = await TscTokenSniffer.CheckCredentialsAsync();
            AppendLog(error != null ? $"[tsc] Check failed: {error}"
                : loggedIn ? "[tsc] Session is valid." : "[tsc] Logged out - use Re-auth.");
            ShowStatus(error != null ? $"TSC check failed: {error}"
                : loggedIn ? "TSC session is valid." : "TSC is logged out - use Re-auth.",
                error == null && loggedIn);
        }
        finally { SetBusy(false); }
    }

    private async void OnReauth(object? sender, EventArgs e)
    {
        SetBusy(true);
        AppendLog("[tsc] Opening a browser for sign-in...");
        try
        {
            var (ok, error) = await TscTokenSniffer.ReauthenticateAsync(AppendLog);
            if (ok) _service?.InvalidateGraphToken();
            AppendLog(ok ? "[tsc] Session saved." : $"[tsc] Re-auth failed: {error}");
            ShowStatus(ok ? "TSC session saved." : $"Re-auth failed: {error}", ok);
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
            AppendLog($"[{tag}] No valid tickets.");
            return (null, default);
        }
        return (tickets, DateOnly.FromDateTime(_date.Value.Date));
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        UpdateActionState();
    }

    // Enable the ticket-dependent actions only when there is at least one valid
    // ticket and no operation is in flight; session actions just track busy.
    private void UpdateActionState()
    {
        var canLog = !_busy && TicketParser.Parse(_tickets.Text).Tickets.Count != 0;
        _queueBtn.Enabled = canLog;
        _logNowBtn.Enabled = canLog;
        _logTscBtn.Enabled = canLog;
        _logHrmBtn.Enabled = canLog;
        _checkBtn.Enabled = !_busy;
        _reauthBtn.Enabled = !_busy;
        _refreshBtn.Enabled = !_busy;
    }

    // Re-read the persisted queue each time the window is focused (e.g. reopened from
    // the tray, or after a 6 PM drain happened while it was hidden).
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        RefreshQueuedView();
    }

    // Hide to tray on the user's X click instead of exiting the process.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            SaveWindowPosition();
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}
