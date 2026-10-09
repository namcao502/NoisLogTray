using System.Drawing;

namespace NoisLogTray;

// Weekly coverage check: for the selected week's weekdays, read back HRM hours and the
// TSC workbook ticket and show whether each day is logged. Read-only; reuses
// LoggingService.CheckWeekAsync. Prev/next navigate weeks; Refresh re-reads live.
internal sealed class WeekPage : UserControl, IShellPage
{
    private const double FullDayHours = 8.0;
    private static readonly int W = UiScale.S(540);
    private static readonly int H = UiScale.S(400);
    private static readonly int Inner = UiScale.S(500); // card width
    private static readonly int RowH = UiScale.S(42);

    internal static readonly Color Green = Color.FromArgb(46, 160, 80);
    internal static readonly Color Amber = Color.FromArgb(217, 164, 0);
    internal static readonly Color Red = Color.FromArgb(230, 76, 76);
    internal static readonly Color Gray = Color.FromArgb(150, 150, 156);

    private static readonly Font TitleFont = UiScale.Font("Segoe UI Semibold", 15F, FontStyle.Bold);
    private static readonly Font LabelFont = UiScale.Font("Segoe UI", 9F);
    private static readonly Font DayFont = UiScale.Font("Segoe UI", 9.5F, FontStyle.Bold);
    private static readonly Font SectionFont = UiScale.Font("Segoe UI", 8.5F, FontStyle.Bold);

    // Raised when the user clicks an under-logged weekday, asking the host to open the
    // Log page on that date. TrayApp owns the wiring (it owns both pages).
    internal event Action<DateOnly>? LogDayRequested;

    private readonly LoggingService? _service;
    private DateOnly _weekMonday;
    private IReadOnlyList<DayCoverage> _last = Array.Empty<DayCoverage>();
    private bool _busy;
    private bool _loaded; // the first OnPageShown since the window was shown loads; page switches keep the week
    private int _renderedRowsWidth;

    private readonly Label _title = new();
    private readonly Label _weekLabel = new();
    private readonly Label _status = new();
    private readonly Label _dayHeader = new();
    private readonly Label _hrmHeader = new();
    private readonly Label _tscHeader = new();
    private readonly MacButton _prev = MacButton.Secondary("<");
    private readonly MacButton _next = MacButton.Secondary(">");
    private readonly MacButton _refresh = MacButton.Secondary(Lang.T("Refresh", "Làm mới"));
    private readonly Card _card = new();
    private readonly FlowLayoutPanel _rows = new();

    internal WeekPage(LoggingService? service)
    {
        _service = service;
        _weekMonday = Hcm.MondayOf(Hcm.Today());
        BuildLayout();
        Theme.Changed += ApplyTheme;
        Lang.Changed += ApplyLanguage;
        ReflowRowsOnResize();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= ApplyTheme;
            Lang.Changed -= ApplyLanguage;
        }
        base.Dispose(disposing);
    }

    public void OnPageShown()
    {
        if (_loaded) return;
        _loaded = true;
        LoadWeekAsync();
    }

    public void OnActivatedRefresh()
    {
    }

    // The shell lives for days; the next show starts again from the current week.
    public void OnHidden()
    {
        if (_busy) return;
        _loaded = false;
        _weekMonday = Hcm.MondayOf(Hcm.Today());
    }

    private void BuildLayout()
    {
        BackColor = Theme.WindowBg;

        _title.Text = Lang.T("Weekly check", "Kiểm tra tuần");
        _title.AutoSize = true;
        _title.Location = UiScale.S(new Point(20, 16));
        _title.Font = TitleFont;
        _title.ForeColor = Theme.TextPrimary;
        _title.BackColor = Color.Transparent;

        _weekLabel.AutoSize = true;
        _weekLabel.Location = UiScale.S(new Point(22, 48));
        _weekLabel.Font = LabelFont;
        _weekLabel.ForeColor = Theme.TextSecondary;
        _weekLabel.BackColor = Color.Transparent;

        var refreshW = ButtonWidths.ToolbarWidth(_refresh.Font);
        var refreshX = UiScale.S(520) - refreshW;
        var buttonY = UiScale.S(18);
        var buttonH = UiScale.S(30);
        _prev.Size = new Size(UiScale.S(34), buttonH);
        _prev.Location = new Point(refreshX - UiScale.S(80), buttonY);
        _prev.Click += (_, _) => Shift(-7);

        _next.Size = new Size(UiScale.S(34), buttonH);
        _next.Location = new Point(refreshX - UiScale.S(40), buttonY);
        _next.Click += (_, _) => Shift(7);

        _refresh.Size = new Size(refreshW, buttonH);
        _refresh.Location = new Point(refreshX, buttonY);
        _refresh.Click += (_, _) => LoadWeekAsync();

        _card.Size = new Size(Inner, UiScale.S(266));
        _card.Location = UiScale.S(new Point(20, 84));
        _card.Controls.Add(SectionHeader(_dayHeader, Lang.T("DAY", "NGÀY"), UiScale.S(24)));
        _card.Controls.Add(SectionHeader(_hrmHeader, Lang.T("HRM (HOURS)", "HRM (GIỜ)"), UiScale.S(166)));
        _card.Controls.Add(SectionHeader(_tscHeader, "TSC (TICKET)", UiScale.S(316)));

        _rows.Location = UiScale.S(new Point(16, 44));
        _rows.Size = new Size(Inner - UiScale.S(32), UiScale.S(210));
        _rows.FlowDirection = FlowDirection.TopDown;
        _rows.WrapContents = false;
        _rows.AutoScroll = false;
        _rows.Padding = new Padding(0);
        _rows.BackColor = Theme.CardSurface;
        _card.Controls.Add(_rows);

        _status.AutoSize = false;
        _status.Location = UiScale.S(new Point(20, 360));
        _status.Size = new Size(Inner, UiScale.S(20));
        _status.Font = LabelFont;
        _status.ForeColor = Theme.TextSecondary;
        _status.BackColor = Color.Transparent;

        Controls.Add(_title);
        Controls.Add(_weekLabel);
        Controls.Add(_prev);
        Controls.Add(_next);
        Controls.Add(_refresh);
        Controls.Add(_card);
        Controls.Add(_status);

        // Sized before anchoring: WinForms records each anchor's edge distances when Anchor is set.
        Size = new Size(W, H);
        MinimumSize = Size;
        _card.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _rows.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _prev.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _next.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
    }

    // Rows are built at the list's width; posted like LogPage so it runs after the layout pass.
    private void ReflowRowsOnResize()
    {
        _rows.Resize += (_, _) =>
        {
            if (_rows.ClientSize.Width == _renderedRowsWidth || !IsHandleCreated) return;
            BeginInvoke(() => RenderRows(_last));
        };
    }

    private static Label SectionHeader(Label label, string text, int x)
    {
        label.Text = text;
        label.AutoSize = true;
        label.Location = new Point(x, UiScale.S(14));
        label.Font = SectionFont;
        label.ForeColor = Theme.TextSecondary;
        label.BackColor = Color.Transparent;
        return label;
    }

    private void ApplyTheme()
    {
        BackColor = Theme.WindowBg;
        _title.ForeColor = Theme.TextPrimary;
        _weekLabel.ForeColor = Theme.TextSecondary;
        _status.ForeColor = Theme.TextSecondary;
        _rows.BackColor = Theme.CardSurface;
        foreach (var h in new[] { _dayHeader, _hrmHeader, _tscHeader }) h.ForeColor = Theme.TextSecondary;
        RenderRows(_last);
        Invalidate(true);
    }

    private void ApplyLanguage()
    {
        _title.Text = Lang.T("Weekly check", "Kiểm tra tuần");
        _dayHeader.Text = Lang.T("DAY", "NGÀY");
        _hrmHeader.Text = Lang.T("HRM (HOURS)", "HRM (GIỜ)");
        _refresh.Text = Lang.T("Refresh", "Làm mới");
        _weekLabel.Text = WeekLabelText();
        // A finished run's status was worded in the old language; a running one is replaced as it progresses.
        if (!_busy) _status.Text = string.Empty;
        RenderRows(_last);
    }

    private void Shift(int days)
    {
        if (_busy) return;
        _weekMonday = _weekMonday.AddDays(days);
        LoadWeekAsync();
    }

    private static IReadOnlyList<DateOnly> Weekdays(DateOnly monday)
    {
        var days = new List<DateOnly>();
        for (var i = 0; i < 5; i++) days.Add(monday.AddDays(i)); // Mon..Fri
        return days;
    }

    private string WeekLabelText()
    {
        var days = Weekdays(_weekMonday);
        return (Lang.Vietnamese
            ? $"{days[0]:dd/MM} - {days[^1]:dd/MM/yyyy}"
            : $"{days[0].ToString("MMM d", Lang.Culture)} - {days[^1].ToString("MMM d, yyyy", Lang.Culture)}").ToUpperInvariant();
    }

    private async void LoadWeekAsync()
    {
        var days = Weekdays(_weekMonday);
        _weekLabel.Text = WeekLabelText();

        if (_service is null) { SetStatus(Lang.T("Config not loaded; set up credentials first.", "Chưa có cấu hình; hãy thiết lập thông tin đăng nhập trước.")); return; }

        SetBusy(true);
        SetStatus(Lang.T("Reading TSC + HRM ...", "Đang đọc TSC + HRM ..."));
        try
        {
            var coverage = await _service.CheckWeekAsync(days, AppendLog);
            if (IsDisposed) return; // a Settings save rebuilt the shell mid-load
            RenderRows(coverage);
            SetStatus(Lang.T("Done. (Green = logged, amber = partial, red = missing, gray = pending/unknown/off/not read.)",
                "Xong. (Xanh = đã log, vàng = thiếu một phần, đỏ = chưa log, xám = chưa tới/không rõ/nghỉ/không đọc được.)"));
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            SetStatus(Lang.T($"Failed: {ex.Message}", $"Lỗi: {ex.Message}"));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RenderRows(IReadOnlyList<DayCoverage> coverage)
    {
        _last = coverage;
        _renderedRowsWidth = _rows.ClientSize.Width;
        var stale = _rows.Controls.Cast<Control>().ToArray();
        _rows.Controls.Clear();
        foreach (var c in stale) c.Dispose();

        foreach (var day in coverage)
            _rows.Controls.Add(BuildRow(day));
    }

    private Control BuildRow(DayCoverage c)
    {
        var future = c.Date > Hcm.Today();
        var (hColor, hText) = HrmStatus(c, future);
        var (tColor, tText) = TscStatus(c, future);
        var actionable = IsActionable(c, future);
        // Margin as at the original layout (row 4px narrower than the list), kept on resize.
        var rowWidth = _rows.ClientSize.Width - UiScale.S(4);

        Panel row = actionable
            ? new ClickableRow
            {
                AccessibleName = c.IsOff
                    ? Lang.T($"{Lang.LongDate(c.Date.ToDateTime(TimeOnly.MinValue))}, day off not marked - open to mark it",
                        $"{Lang.LongDate(c.Date.ToDateTime(TimeOnly.MinValue))}, ngày nghỉ chưa đánh dấu - mở để đánh dấu")
                    : Lang.T($"{Lang.LongDate(c.Date.ToDateTime(TimeOnly.MinValue))}, needs logging - open to log",
                        $"{Lang.LongDate(c.Date.ToDateTime(TimeOnly.MinValue))}, cần log - mở để log"),
            }
            : new Panel();
        row.Width = rowWidth;
        row.Height = RowH;
        row.Margin = new Padding(0);
        row.BackColor = Color.Transparent;

        // Accent bar on the left marks an actionable row (a subtle affordance).
        if (actionable)
            row.Controls.Add(new Panel { Location = new Point(0, 0), Size = new Size(UiScale.S(3), RowH - 1), BackColor = Theme.Accent });

        row.Controls.Add(new Label
        {
            Text = Lang.Vietnamese ? c.Date.ToString("ddd  dd/MM", Lang.Culture) : c.Date.ToString("ddd  MMM d", Lang.Culture),
            Location = new Point(UiScale.S(8), 0),
            Size = new Size(UiScale.S(130), RowH),
            TextAlign = ContentAlignment.MiddleLeft,
            Font = DayFont,
            ForeColor = Theme.TextPrimary,
            BackColor = Color.Transparent,
        });

        var tscTextX = UiScale.S(318);
        row.Controls.Add(Dot(hColor, UiScale.S(150)));
        row.Controls.Add(StatusLabel(hText, hColor, UiScale.S(168), UiScale.S(118)));
        row.Controls.Add(Dot(tColor, UiScale.S(300)));
        row.Controls.Add(StatusLabel(tText, tColor, tscTextX, rowWidth - tscTextX - UiScale.S(6)));

        if (actionable) WireRowActions(row, c.Date);

        row.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Divider });
        return row;
    }

    // Make an actionable row behave like a button: hand cursor, hover highlight, and a
    // click (mouse or keyboard, via ClickableRow) that asks to open logging for its date.
    private void WireRowActions(Panel row, DateOnly date)
    {
        row.Cursor = Cursors.Hand;
        void Fire(object? s, EventArgs e) => LogDayRequested?.Invoke(date);
        void Enter(object? s, EventArgs e) => row.BackColor = Theme.Hover;
        void Leave(object? s, EventArgs e)
        {
            if (!row.ClientRectangle.Contains(row.PointToClient(Cursor.Position)))
                row.BackColor = Color.Transparent;
        }

        row.Click += Fire;
        row.MouseEnter += Enter;
        row.MouseLeave += Leave;
        foreach (Control child in row.Controls)
        {
            child.Cursor = Cursors.Hand;
            child.Click += Fire;
            child.MouseEnter += Enter;
            child.MouseLeave += Leave;
        }
    }

    private static Panel Dot(Color color, int x) => new()
    {
        Size = UiScale.S(new Size(10, 10)),
        Location = new Point(x, (RowH - UiScale.S(10)) / 2),
        BackColor = color,
    };

    private static Label StatusLabel(string text, Color color, int x, int width) => new()
    {
        Text = text,
        Location = new Point(x, 0),
        Size = new Size(Math.Max(UiScale.S(40), width), RowH),
        TextAlign = ContentAlignment.MiddleLeft,
        Font = LabelFont,
        ForeColor = color,
        BackColor = Color.Transparent,
        AutoEllipsis = true,
        UseMnemonic = false,
    };

    // A clickable row jumps into logging its date. A day off ignores `future` so a planned
    // leave can be marked ahead; an unread TSC cell is unknown, not outstanding.
    internal static bool IsActionable(DayCoverage c, bool future)
    {
        var (hColor, _) = HrmStatus(c, future);
        var (tColor, _) = TscStatus(c, future);
        var tscOutstanding = (c.TscState == TscReadState.Read || c.TscState == TscReadState.NotDue) && tColor != Green;
        return c.IsOff ? tscOutstanding : !future && (hColor != Green || tscOutstanding);
    }

    // A day off has no hours to log, so zero is never a miss.
    internal static (Color, string) HrmStatus(DayCoverage c, bool future)
    {
        if (c.IsOff) return (Gray, Lang.T("off", "nghỉ"));
        if (future) return (Gray, Lang.T("pending", "chưa tới"));
        if (c.HrmHours is null) return (Gray, Lang.T("unknown", "không rõ"));
        var h = c.HrmHours.Value;
        if (h >= FullDayHours) return (Green, h.ToString("0.#") + "h");
        if (h > 0) return (Amber, h.ToString("0.#") + "h");
        return (Red, "0h");
    }

    // A day off stays outstanding until the OFF marker reaches the workbook.
    internal static (Color, string) TscStatus(DayCoverage c, bool future)
    {
        // Checked before IsOff: an unread cell cannot tell a marked day off from an unmarked one.
        if (c.TscState == TscReadState.NoToken || c.TscState == TscReadState.Failed)
            return (Gray, Lang.T("not read", "không đọc được"));
        if (c.IsOff)
        {
            return string.Equals(c.TscTicket?.Trim(), TscCells.OffMarker, StringComparison.OrdinalIgnoreCase)
                ? (Green, TscCells.OffMarker)
                : (Amber, Lang.T("off - not marked", "nghỉ - chưa đánh dấu"));
        }
        if (future) return (Gray, Lang.T("pending", "chưa tới"));
        if (string.IsNullOrWhiteSpace(c.TscTicket)) return (Red, Lang.T("none", "trống"));
        return (Green, c.TscTicket!);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        _prev.Enabled = _next.Enabled = _refresh.Enabled = !busy;
    }

    // Route the service's progress lines to the log file + the status label (latest line).
    private void AppendLog(string line)
    {
        AppLogger.Info(line);
        SetStatus(line);
    }

    private void SetStatus(string text)
    {
        if (InvokeRequired) { BeginInvoke(new Action<string>(SetStatus), text); return; }
        _status.Text = text;
    }
}
