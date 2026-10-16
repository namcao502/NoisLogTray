using System.Drawing;

namespace NoisLogTray;

// The Log page's week: per weekday HRM hours and the TSC ticket, via LoggingService.CheckWeekAsync.
// An under-logged past weekday (or an unmarked day off) is clickable.
internal sealed class WeekCard : Card
{
    private const double FullDayHours = 8.0;
    private const int RowHLogical = 30;
    private const int RowsY = 64;
    private const int StatusY = RowsY + 5 * RowHLogical + 4;
    internal const int BuiltHeight = StatusY + 18 + 10; // logical px, at LogPage.CardW wide
    private static readonly int RowH = UiScale.S(RowHLogical);

    internal static readonly Color Green = Color.FromArgb(46, 160, 80);
    internal static readonly Color Amber = Color.FromArgb(217, 164, 0);
    internal static readonly Color Red = Color.FromArgb(230, 76, 76);
    internal static readonly Color Gray = Color.FromArgb(150, 150, 156);

    private static readonly Font LabelFont = UiScale.Font("Segoe UI", 9F);
    private static readonly Font DayFont = UiScale.Font("Segoe UI", 9.5F, FontStyle.Bold);
    private static readonly Font SectionFont = UiScale.Font("Segoe UI", 8.5F, FontStyle.Bold);

    internal event Action<DateOnly>? DayRequested;

    private readonly LoggingService? _service;
    private DateOnly _weekMonday;
    private IReadOnlyList<DayCoverage> _last = Array.Empty<DayCoverage>();
    private bool _busy;
    private bool _loaded; // the first LoadIfNeeded since the window was shown reads; page switches keep the week
    private bool _reloadPending;
    private Func<string>? _statusText; // the finished read's status, reworded on a language switch
    private int _renderedRowsWidth;

    private readonly Label _sectionLabel = new();
    private readonly Label _weekLabel = new();
    private readonly SingleLineLabel _status = new();
    private readonly Label _dayHeader = new();
    private readonly Label _hrmHeader = new();
    private readonly Label _tscHeader = new();
    private readonly MacButton _prev = MacButton.Secondary("");
    private readonly MacButton _next = MacButton.Secondary("");
    private readonly MacButton _refresh = MacButton.Secondary(Lang.T("Refresh", "Làm mới"));
    private readonly FlowLayoutPanel _rows = new();

    internal WeekCard(LoggingService? service)
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

    // The first show after the window was shown reads TSC too: with no cached Graph token it sniffs one (headless
    // Chrome, at most once per token lifetime), and a Log now meanwhile waits for that token instead of a browser.
    internal void LoadIfNeeded()
    {
        if (_loaded) return;
        _loaded = true;
        if (_busy) _reloadPending = true; // a read from before the hide is still running
        else LoadWeekAsync(allowSniff: true);
    }

    // After a log run: HRM is re-read, and TSC with the Graph token still cached (a lapsed one reads "not read").
    // A read already running may predate the write, so it is followed by one more.
    internal void ReloadIfLoaded()
    {
        if (!_loaded || IsDisposed) return; // a Settings save may have disposed this card mid-log
        if (_busy) _reloadPending = true;
        else LoadWeekAsync(allowSniff: false);
    }

    // The shell lives for days; the next show starts again from the current week.
    internal void Reset()
    {
        _loaded = false;
        _reloadPending = false;
        _weekMonday = Hcm.MondayOf(Hcm.Today());
    }

    private void BuildLayout()
    {
        Controls.Add(SectionHeader(_sectionLabel, Lang.T("THIS WEEK", "TUẦN NÀY"), UiScale.S(16), UiScale.S(14)));
        // The week range starts after the wider title, so a language switch never overlaps it.
        var titleWidth = Math.Max(TextRenderer.MeasureText("THIS WEEK", SectionFont).Width, TextRenderer.MeasureText("TUẦN NÀY", SectionFont).Width);
        _weekLabel.AutoSize = true;
        _weekLabel.Location = new Point(UiScale.S(24) + titleWidth, UiScale.S(13));
        _weekLabel.Font = LabelFont;
        _weekLabel.ForeColor = Theme.TextSecondary;
        _weekLabel.BackColor = Color.Transparent;

        var buttonY = UiScale.S(7);
        var buttonH = UiScale.S(30);
        _refresh.Size = new Size(ButtonWidths.ToolbarWidth(_refresh.Font), buttonH);
        _refresh.Location = new Point(UiScale.S(LogPage.CardW - 16) - _refresh.Width, buttonY);
        _refresh.Click += (_, _) => LoadWeekAsync(allowSniff: true);
        _next.Size = new Size(UiScale.S(34), buttonH);
        _next.Glyph = Icons.ChevronRight;
        _next.Location = new Point(_refresh.Left - UiScale.S(8) - _next.Width, buttonY);
        _next.Click += (_, _) => Shift(7);
        _prev.Size = new Size(UiScale.S(34), buttonH);
        _prev.Glyph = Icons.ChevronLeft;
        _prev.Location = new Point(_next.Left - UiScale.S(4) - _prev.Width, buttonY);
        _prev.Click += (_, _) => Shift(-7);

        // Over the rows' HRM and TSC dots (BuildRow), which sit 16px further left inside the row list.
        Controls.Add(SectionHeader(_dayHeader, Lang.T("DAY", "NGÀY"), UiScale.S(24), UiScale.S(46)));
        Controls.Add(SectionHeader(_hrmHeader, Lang.T("HRM (HOURS)", "HRM (GIỜ)"), UiScale.S(128), UiScale.S(46)));
        Controls.Add(SectionHeader(_tscHeader, "TSC (TICKET)", UiScale.S(236), UiScale.S(46)));

        _rows.Location = UiScale.S(new Point(16, RowsY));
        _rows.Size = new Size(UiScale.S(LogPage.CardW - 32), 5 * RowH); // each row is RowH tall: scaled per row, not as one sum
        _rows.FlowDirection = FlowDirection.TopDown;
        _rows.WrapContents = false;
        _rows.AutoScroll = false;
        _rows.Padding = new Padding(0);
        _rows.BackColor = Theme.CardSurface;

        _status.AutoSize = false;
        ThemedToolTip.Track(_status);
        _status.Location = UiScale.S(new Point(16, StatusY));
        _status.Size = UiScale.S(new Size(LogPage.CardW - 32, 18));
        _status.Font = LabelFont;
        _status.ForeColor = Theme.TextSecondary;
        _status.BackColor = Color.Transparent;

        Controls.AddRange(new Control[] { _weekLabel, _prev, _next, _refresh, _rows, _status });
        // Sized before anchoring: WinForms records each anchor's edge distances when Anchor is set.
        Size = UiScale.S(new Size(LogPage.CardW, BuiltHeight));
        _rows.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _prev.Anchor = _next.Anchor = _refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
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

    private static Label SectionHeader(Label label, string text, int x, int y)
    {
        label.Text = text;
        label.AutoSize = true;
        label.Location = new Point(x, y);
        label.Font = SectionFont;
        label.ForeColor = Theme.TextSecondary;
        label.BackColor = Color.Transparent;
        return label;
    }

    private void ApplyTheme()
    {
        _weekLabel.ForeColor = Theme.TextSecondary;
        _status.ForeColor = Theme.TextSecondary;
        _rows.BackColor = Theme.CardSurface;
        foreach (var h in new[] { _sectionLabel, _dayHeader, _hrmHeader, _tscHeader }) h.ForeColor = Theme.TextSecondary;
        RenderRows(_last);
        Invalidate(true);
    }

    private void ApplyLanguage()
    {
        _sectionLabel.Text = Lang.T("THIS WEEK", "TUẦN NÀY");
        _dayHeader.Text = Lang.T("DAY", "NGÀY");
        _hrmHeader.Text = Lang.T("HRM (HOURS)", "HRM (GIỜ)");
        _refresh.Text = Lang.T("Refresh", "Làm mới");
        _weekLabel.Text = WeekLabelText();
        // A finished run's status is reworded in the new language; a running one is replaced as it progresses.
        if (!_busy) _status.Text = _statusText?.Invoke() ?? string.Empty;
        RenderRows(_last);
    }

    private void Shift(int days)
    {
        if (_busy) return;
        _weekMonday = _weekMonday.AddDays(days);
        LoadWeekAsync(allowSniff: false);
    }

    // The legend, or how to read TSC when this read had no Graph token.
    private static string StatusText(IReadOnlyList<DayCoverage> coverage) =>
        coverage.Any(dc => dc.TscState == TscReadState.NoToken)
            ? Lang.T("TSC not read yet: Refresh reads it (Chrome runs in the background).", "Chưa đọc TSC: bấm Làm mới để đọc (Chrome chạy ngầm).")
            : Lang.T("Green logged, amber partial, red missing, gray not due / off / not read.",
                "Xanh đã log, vàng thiếu, đỏ chưa log, xám chưa tới / nghỉ / không đọc.");

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

    private async void LoadWeekAsync(bool allowSniff)
    {
        var days = Weekdays(_weekMonday);
        _weekLabel.Text = WeekLabelText();

        if (_service is null)
        {
            _statusText = () => Lang.T("Config not loaded; set up credentials first.", "Chưa có cấu hình; hãy thiết lập thông tin đăng nhập trước.");
            SetStatus(_statusText());
            return;
        }

        SetBusy(true);
        SetStatus(Lang.T("Reading TSC + HRM ...", "Đang đọc TSC + HRM ..."));
        using var busyWork = BusyWork.Shared.Begin(Lang.T("Reading the week's logs...", "Đang đọc log của tuần..."));
        try
        {
            var coverage = await _service.CheckWeekAsync(days, AppendLog, allowSniff: allowSniff);
            if (IsDisposed) return; // a Settings save rebuilt the shell mid-load
            RenderRows(coverage);
            _statusText = () => StatusText(coverage);
            SetStatus(_statusText());
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            _statusText = () => Lang.T($"Failed: {ex.Message}", $"Lỗi: {ex.Message}");
            SetStatus(_statusText());
        }
        finally
        {
            SetBusy(false);
            if (_reloadPending && !IsDisposed)
            {
                _reloadPending = false;
                LoadWeekAsync(allowSniff: false);
            }
        }
    }

    private void RenderRows(IReadOnlyList<DayCoverage> coverage)
    {
        _last = coverage;
        _renderedRowsWidth = _rows.ClientSize.Width;
        var stale = _rows.Controls.Cast<Control>().ToArray();
        _rows.Controls.Clear();
        foreach (var c in stale) c.Dispose();

        for (var dayIndex = 0; dayIndex < coverage.Count; dayIndex++)
            _rows.Controls.Add(BuildRow(coverage[dayIndex], striped: dayIndex % 2 == 1));
    }

    private Control BuildRow(DayCoverage c, bool striped)
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
        row.BackColor = striped ? Theme.RowStripe(Theme.CardSurface) : Color.Transparent;

        // Accent bar on the left marks an actionable row (a subtle affordance).
        if (actionable)
            row.Controls.Add(new Panel { Location = new Point(0, 0), Size = new Size(UiScale.S(3), RowH - 1), BackColor = Theme.Accent });

        row.Controls.Add(new Label
        {
            Text = Lang.Vietnamese ? c.Date.ToString("ddd  dd/MM", Lang.Culture) : c.Date.ToString("ddd  MMM d", Lang.Culture),
            Location = new Point(UiScale.S(8), 0),
            Size = new Size(UiScale.S(100), RowH),
            TextAlign = ContentAlignment.MiddleLeft,
            Font = DayFont,
            ForeColor = Theme.TextPrimary,
            BackColor = Color.Transparent,
        });

        var tscTextX = UiScale.S(238);
        row.Controls.Add(Dot(hColor, UiScale.S(112)));
        row.Controls.Add(StatusLabel(hText, hColor, UiScale.S(130), UiScale.S(86)));
        row.Controls.Add(Dot(tColor, UiScale.S(220)));
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
        void Fire(object? s, EventArgs e) => DayRequested?.Invoke(date);
        var restingColor = row.BackColor;
        void Enter(object? s, EventArgs e) => row.BackColor = Theme.Hover;
        void Leave(object? s, EventArgs e)
        {
            if (!row.ClientRectangle.Contains(row.PointToClient(Cursor.Position)))
                row.BackColor = restingColor;
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

    private static Label StatusLabel(string text, Color color, int x, int width)
    {
        var statusLabel = new SingleLineLabel
        {
            Text = text,
            Location = new Point(x, 0),
            Size = new Size(Math.Max(UiScale.S(40), width), RowH),
            TextAlign = ContentAlignment.MiddleLeft,
            Font = LabelFont,
            ForeColor = color,
            BackColor = Color.Transparent,
            UseMnemonic = false,
        };
        ThemedToolTip.Track(statusLabel);
        return statusLabel;
    }

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
