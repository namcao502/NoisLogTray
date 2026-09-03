using System.Drawing;
using System.Globalization;

namespace NoisLogTray;

// The capture window, structured like the old web app: a header (with a light/dark
// toggle), then stacked cards -- "Log entries" (My tickets + date/ticket), "Will
// log" (preview), and "Actions". A scrolling "activity" log at the bottom of the
// window streams every activity line and the coloured result; TSC/HRM progress bars
// show inside that block during a log run.
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
    private const int InnerW = 528; // CardW - 2*16
    private const int WillLogHostH = 140; // fixed; rows scroll internally beyond this

    private readonly LoggingService? _service;

    private readonly Panel _body = new();
    private readonly Panel _header = new();
    private readonly Label _headerTitle = new();
    private readonly Label _headerSubtitle = new();
    private readonly ThemeToggleButton _themeBtn = new();
    private readonly List<Label> _sectionLabels = new();
    private readonly List<Panel> _dividers = new();

    private readonly RoundedDatePicker _date = new();
    private readonly ToolTip _tips = new();
    private readonly TextBox _tickets = new();
    private readonly MacButton _queueBtn = MacButton.Primary("Add to list");
    private readonly MacButton _logNowBtn = MacButton.Secondary("Log now (TSC + HRM)");
    private readonly MacButton _logTscBtn = MacButton.Secondary("Log TSC");
    private readonly MacButton _logHrmBtn = MacButton.Secondary("Log HRM");
    private readonly MacButton _logOffBtn = MacButton.Secondary("Log OFF");
    private readonly MacButton _checkBtn = MacButton.Secondary("Check TSC");
    private readonly MacButton _reauthBtn = MacButton.Secondary("Re-auth");
    private readonly MacButton _refreshBtn = MacButton.Secondary("Refresh");
    private readonly MacButton _jqlBtn = MacButton.Secondary("Edit JQL");
    private readonly MacButton _clearBtn = MacButton.Secondary("Clear");
    private readonly FlowLayoutPanel _suggestions = new();
    private readonly Label _suggestionStatus = new();
    private readonly FlowLayoutPanel _willLogList = new();
    private readonly System.Windows.Forms.Timer _verifyTimer = new() { Interval = 500 };
    private readonly Dictionary<string, (VState State, string? Title)> _verify = new();
    private readonly MacButton _clearQueueBtn = MacButton.Secondary("Clear queue");
    private readonly MacButton _logAllBtn = MacButton.Primary("Log all now");
    private readonly Label _hoursHint = new();

    // Per typed-ticket HRM minutes while composing an entry. null = the default even
    // split (see TimeSlots.EvenSplit); reset whenever the ticket text changes.
    private List<int>? _typedMinutes;

    // The bottom activity block (scrolling console + TSC/HRM progress bars).
    private readonly ActivityLogPanel _activity = new();

    private IReadOnlyList<JiraSuggestion> _lastSuggestions = Array.Empty<JiraSuggestion>();
    private bool _busy;

    private enum VState { Verifying, Valid, NotFound, Error }

    internal event Action? QueueChanged;

    // Raised after a successful TSC re-auth so the tray can retry any due queue entries.
    internal event Action? ReauthSucceeded;

    // Raised by "Log all now" so the tray drains the whole queue through its guarded path.
    internal event Action? DrainRequested;

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
        ClientSize = new Size(600, 958); // body fits the cards snugly; activity log docks below
        RestoreWindowPosition();

        BuildBody();
        Controls.Add(_activity);
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
        _body.AutoScroll = false; // main window never scrolls; only the activity log does

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

        _jqlBtn.Size = new Size(90, 26);
        _jqlBtn.Location = new Point(16 + InnerW - 188, 38);
        _jqlBtn.Click += (_, _) => EditJql();
        card.Controls.Add(_jqlBtn);

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
        _tips.SetToolTip(_date, "Dates and the 6 PM auto-log use Vietnam time (Asia/Ho_Chi_Minh, UTC+7).");

        var ticketHost = new RoundedHost { Location = new Point(214, 300), Size = new Size(262, 30) };
        _tickets.BorderStyle = BorderStyle.None;
        _tickets.Font = new Font("Segoe UI", 9.5F);
        _tickets.BackColor = Theme.InputBg;
        _tickets.ForeColor = Theme.TextPrimary;
        _tickets.PlaceholderText = "e.g. 1234, 5678  (MDP- optional)";
        _tickets.TextChanged += (_, _) => { _typedMinutes = null; UpdateWillLog(); UpdateActionState(); _verifyTimer.Stop(); _verifyTimer.Start(); };
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
        var card = new Card { Size = new Size(CardW, 38 + WillLogHostH + 12) };
        card.Controls.Add(SectionLabel("WILL LOG", 16, 14));

        // Running total / validation hint, right-aligned in the header (typed view only).
        _hoursHint.AutoSize = false;
        _hoursHint.Size = new Size(170, 18);
        _hoursHint.Location = new Point(16 + InnerW - 170, 13);
        _hoursHint.TextAlign = ContentAlignment.MiddleRight;
        _hoursHint.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
        _hoursHint.ForeColor = Theme.TextSecondary;
        _hoursHint.BackColor = Color.Transparent;
        _hoursHint.Visible = false;
        card.Controls.Add(_hoursHint);

        // Clears the persisted queue; only shown while the card is displaying the
        // queued fallback (input empty + something queued).
        _clearQueueBtn.Size = new Size(90, 24);
        _clearQueueBtn.Location = new Point(16 + InnerW - 90, 10);
        _clearQueueBtn.Visible = false;
        _clearQueueBtn.Click += OnClearQueue;
        card.Controls.Add(_clearQueueBtn);

        // Logs the whole queued list now (same guarded drain as the tray "Log queue now");
        // shown beside Clear queue, only in the queued fallback view.
        _logAllBtn.Size = new Size(96, 24);
        _logAllBtn.Location = new Point(16 + InnerW - 90 - 96 - 8, 10);
        _logAllBtn.Visible = false;
        _logAllBtn.Click += OnLogAllNow;
        card.Controls.Add(_logAllBtn);

        // Fixed-height list; rows scroll internally once they overflow so the Actions
        // card below stays at a stable, visible position.
        var host = new RoundedHost { Location = new Point(16, 38), Size = new Size(InnerW, WillLogHostH) };
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

    private Card BuildActionsCard()
    {
        var card = new Card { Size = new Size(CardW, 132) };
        card.Controls.Add(SectionLabel("ACTIONS", 16, 14));

        // 2-up primary row over a 5-up secondary row, 12px gutter. The rows deliberately
        // no longer share column edges - 5 does not divide into 2.
        const int gap = 12;
        const int half = (InnerW - gap) / 2;      // 258: top-row button width
        const int fifth = (InnerW - 4 * gap) / 5; // 96: bottom-row button width
        var col2 = 16 + half + gap;               // 286: start of the right column

        _queueBtn.Size = new Size(half, 40);
        _queueBtn.Location = new Point(16, 36);
        _queueBtn.Click += OnQueue;

        _logNowBtn.Size = new Size(half, 40);
        _logNowBtn.Location = new Point(col2, 36);
        _logNowBtn.Click += OnLogNow;

        var bottomRow = new[] { _logTscBtn, _logHrmBtn, _logOffBtn, _checkBtn, _reauthBtn };
        for (var i = 0; i < bottomRow.Length; i++)
        {
            bottomRow[i].Size = new Size(fifth, 32);
            bottomRow[i].Location = new Point(16 + i * (fifth + gap), 84);
        }
        _logTscBtn.Click += OnLogTsc;
        _logHrmBtn.Click += OnLogHrm;
        _logOffBtn.Click += OnLogOff;
        _checkBtn.Click += OnCheckTsc;
        _reauthBtn.Click += OnReauth;

        _tips.SetToolTip(_logOffBtn,
            $"Write \"{TscCells.OffMarker}\" on a yellow background to TSC for the selected date (no HRM hours).");

        card.Controls.Add(_queueBtn);
        card.Controls.Add(_logNowBtn);
        foreach (var button in bottomRow) card.Controls.Add(button);
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

        RenderSuggestions(_lastSuggestions);
        UpdateWillLog();
        Invalidate(true);
    }

    // Every activity line goes to the log file AND the activity console. Safe to call
    // from a background thread (the console marshals to the UI thread internally).
    internal void AppendLog(string line)
    {
        AppLogger.Info(line);
        _activity.Append(line, isResult: false, ok: false);
    }

    // Show a line in the console WITHOUT writing it to the log file - the caller has
    // already logged it. Used by TrayApp so a line is not written to app.log twice.
    internal void ShowActivityLine(string line) => _activity.Append(line, isResult: false, ok: false);

    // Append a coloured result line (green on success, red on failure) to the console.
    private void ShowStatus(string message, bool ok) => _activity.Append(message, isResult: true, ok: ok);

    private void ShowProgress(bool tsc, bool hrm) => _activity.ShowProgress(tsc, hrm);
    private void HideProgress() => _activity.HideProgress();
    private void ReportTsc(int done, int total) => _activity.ReportTsc(done, total);
    private void ReportHrm(int done, int total) => _activity.ReportHrm(done, total);

    // Open the JQL editor for the "My tickets" query; on save, apply it to the live
    // service, persist it, and re-fetch the list with the new query.
    private void EditJql()
    {
        if (_service is null)
        {
            SetSuggestionStatus("Config not loaded; cannot edit query.");
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
            SetSuggestionStatus("Config not loaded; cannot fetch tickets.");
            return;
        }

        _verify.Clear(); // Refresh forces a fresh Jira check for every shown ticket
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
            ? $"{ticket.Key}, {ticket.Summary}, due {dueLabel}"
            : $"{ticket.Key}, {ticket.Summary}";

        var row = new ClickableRow
        {
            Width = width,
            Height = 31,
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

    // Format a Jira ISO due date ("yyyy-MM-dd") as a short "MMM d" label; empty when unset.
    private static string FormatDue(string? due)
    {
        if (string.IsNullOrWhiteSpace(due)) return "";
        return DateTime.TryParse(due, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt.ToString("MMM d", CultureInfo.InvariantCulture)
            : due;
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

    // The distinct tickets currently shown in "Will log" (for Jira verification):
    // what is typed, or - when nothing is typed - every queued ticket across all dates.
    private IReadOnlyList<string> ShownTickets()
    {
        var (typed, _) = TicketParser.Parse(_tickets.Text);
        if (typed.Count != 0) return typed;
        return TicketQueue.Read().SelectMany(e => e.Tickets).Distinct().ToList();
    }

    // Render "Will log". It always lists the whole persisted queue (grouped by date, each
    // headed "(queued for 6 PM)") so the accumulating list stays visible. While typing, the
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
            _willLogList.Controls.Add(WillLogText(_date.Value.ToString("dddd, MMMM d, yyyy") + "   (not added yet)", rowWidth));
            AddEditableTicketRows(typed, minutes, rowWidth);
        }

        // The persisted queue below, always shown so the list visibly builds up.
        if (entries.Count != 0)
        {
            foreach (var entry in entries)
            {
                if (!DateOnly.TryParseExact(entry.Date, "yyyy-MM-dd", out var d)) continue;
                _willLogList.Controls.Add(WillLogText(d.ToString("dddd, MMMM d, yyyy") + "   (queued for 6 PM)", rowWidth));
                AddTicketRows(entry.Date, entry.Tickets, entry.Minutes, rowWidth);
            }
        }
        else if (!composing)
        {
            _willLogList.Controls.Add(WillLogText("Enter a ticket above to preview what will be logged.", rowWidth));
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
            _hoursHint.Text = "each ticket needs > 0h";
            _hoursHint.ForeColor = Color.FromArgb(230, 76, 76);
        }
        else if (sum > TimeSlots.TotalWorkMinutes)
        {
            _hoursHint.Text = $"{sum / 60.0:0.#}h - over 8h, trim";
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
        row.SetRemoveAccessibleName($"Remove {key} on {date}");
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
            AppendLog("[queue] No valid tickets to queue.");
            return;
        }

        var (sum, allPositive) = TypedMinuteStats();
        if (!allPositive || sum > TimeSlots.TotalWorkMinutes)
        {
            ShowStatus("Fix the hours first: each ticket needs > 0h and the day can't exceed 8h.", false);
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
                ShowStatus($"That would exceed 8h for {date}. Trim the hours before queueing.", false);
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
        ShowStatus($"Added {tickets.Count} ticket{(tickets.Count == 1 ? "" : "s")} to the list for {date} (auto-logs at 6 PM).", true);
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

    // Remove one ticket from its date's queue entry (the row [X]) and refresh the list.
    private void RemoveQueuedTicket(string date, string ticket)
    {
        TicketQueue.RemoveTicket(date, ticket);
        RefreshQueuedView();
        ShowStatus($"Removed {ticket} from {date}.", true);
        QueueChanged?.Invoke();
    }

    // Log the whole queued list now via the tray's guarded drain (DrainRequested). The
    // drain itself streams progress to the activity console and re-renders on completion.
    private void OnLogAllNow(object? sender, EventArgs e)
    {
        if (_service is null) { AppendLog("[error] Config not loaded; cannot log."); return; }
        if (TicketQueue.Read().Count == 0)
        {
            ShowStatus("Nothing queued to log.", false);
            return;
        }
        _logAllBtn.Enabled = false;
        AppendLog("[log] Logging the whole queued list now...");
        DrainRequested?.Invoke();
    }

    // Read the persisted 6 PM queue and show it (read-only) so it stays visible after
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
        _tickets.Focus();
    }

    private async void OnLogNow(object? sender, EventArgs e)
    {
        if (_service is null) { AppendLog("[error] Config not loaded; cannot log."); return; }
        var (tickets, date) = ParseEntry("log");
        if (tickets is null) return;
        if (HrmClosedForToday(date))
        {
            ShowStatus("HRM can't log today's hours before 6 PM (it rejects future times). Queue it for 6 PM, or use Log TSC.", false);
            return;
        }

        SetBusy(true);
        ShowProgress(true, true);
        try
        {
            AppendLog($"[log] Logging {date:yyyy-MM-dd}: {string.Join(", ", tickets)} ...");
            var token = await _service.AcquireGraphTokenAsync(AppendLog);
            var result = await _service.LogEntryAsync(date, tickets, token, TypedMinutesFor(tickets), AppendLog, ReportTsc, ReportHrm);
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
        if (HrmClosedForToday(date))
        {
            ShowStatus("HRM can't log today's hours before 6 PM (it rejects future times). Queue it for 6 PM instead.", false);
            return;
        }

        SetBusy(true);
        ShowProgress(false, true);
        try
        {
            var (ok, err) = await _service.LogHrmAsync(tickets, date, TypedMinutesFor(tickets), AppendLog, ReportHrm);
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

    // The only path that overwrites, hence the confirm: one click, no other input, on a
    // workbook everyone reads.
    private async void OnLogOff(object? sender, EventArgs e)
    {
        if (_service is null) { AppendLog("[error] Config not loaded; cannot log."); return; }

        var date = DateOnly.FromDateTime(_date.Value.Date);
        var answer = MessageBox.Show(this,
            $"Write \"{TscCells.OffMarker}\" to TSC for {date:dddd, MMMM d, yyyy}?\n\n"
                + "Anything already in that day's cells will be replaced, and any queued tickets for it dropped.",
            "Mark the day off", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        SetBusy(true);
        ShowProgress(true, false);
        try
        {
            var result = await _service.LogOffAsync(new[] { date }, overwrite: true, AppendLog, ReportTsc);
            if (result.Success && result.Marked.Count != 0)
            {
                // Drop the day's queued tickets so the scheduled drain cannot overwrite OFF.
                var queued = TicketQueue.Read().Where(q => q.Date == date.ToString("yyyy-MM-dd")).ToList();
                if (queued.Count != 0)
                {
                    TicketQueue.RemoveLogged(queued);
                    AppendLog($"[off] Removed {queued.Count} queued entr{(queued.Count == 1 ? "y" : "ies")} for {date:yyyy-MM-dd}.");
                }
                OffDayStore.Add(result.Marked);
                RefreshQueuedView();
            }

            AppendLog($"[off] {(result.Success ? "OK" : result.Error)}");
            ShowStatus(result.Success
                ? $"TSC marked {TscCells.OffMarker} for {date:yyyy-MM-dd}."
                : $"Log OFF failed: {result.Error}",
                result.Success);
        }
        catch (Exception ex)
        {
            AppendLog($"[off] Error: {ex.Message}");
            ShowStatus($"Log OFF failed: {ex.Message}", false);
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
            AppendLog($"[{tag}] No valid tickets.");
            return (null, default);
        }
        return (tickets, DateOnly.FromDateTime(_date.Value.Date));
    }

    // HRM rejects future stop times, so today's hours cannot be logged before 18:00 HCM.
    private static bool HrmClosedForToday(DateOnly date) => date == Hcm.Today() && Hcm.Now().Hour < 18;

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        UpdateActionState();
    }

    // Enable the ticket-dependent actions only when there is at least one valid ticket
    // and no operation is in flight. Queue / Log now / Log HRM additionally require valid
    // hours: each ticket > 0 and the selected day (already-queued + typed) <= 8h; Log TSC
    // ignores time, so it only needs a ticket.
    private void UpdateActionState()
    {
        var hasTickets = TicketParser.Parse(_tickets.Text).Tickets.Count != 0;
        var (sum, allPositive) = ProjectedDayStats();
        var hoursOk = allPositive && sum <= TimeSlots.TotalWorkMinutes;

        _queueBtn.Enabled = !_busy && hasTickets && hoursOk;
        _logNowBtn.Enabled = !_busy && hasTickets && hoursOk;
        _logTscBtn.Enabled = !_busy && hasTickets;
        _logHrmBtn.Enabled = !_busy && hasTickets && hoursOk;
        _logOffBtn.Enabled = !_busy; // no ticket needed, and a future leave day is fair game
        _checkBtn.Enabled = !_busy;
        _reauthBtn.Enabled = !_busy;
        _logAllBtn.Enabled = !_busy; // batch drain must not fight an in-flight browser/log op
        _refreshBtn.Enabled = !_busy;
    }

    // Re-read the persisted queue each time the window is focused (e.g. reopened from
    // the tray, or after a 6 PM drain happened while it was hidden).
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _date.SyncToTodayIfAuto();
        RefreshQueuedView();
    }

    // Hide to tray on the user's X click instead of exiting the process.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            SaveWindowPosition();
            _date.ForgetManualPick();
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}
