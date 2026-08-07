using System.Drawing;

namespace NoisLogTray;

// Weekly coverage check: for the selected week's weekdays, read back HRM hours and the
// TSC workbook ticket and show whether each day is logged. Read-only; reuses
// LoggingService.CheckWeekAsync. Prev/next navigate weeks; Refresh re-reads live.
internal sealed class WeeklyCheckForm : Form
{
    private const double FullDayHours = 8.0;
    private const int W = 540;
    private const int Inner = 500; // card width

    private static readonly Color Green = Color.FromArgb(46, 160, 80);
    private static readonly Color Amber = Color.FromArgb(217, 164, 0);
    private static readonly Color Red = Color.FromArgb(230, 76, 76);
    private static readonly Color Gray = Color.FromArgb(150, 150, 156);

    private static readonly Font TitleFont = new("Segoe UI Semibold", 15F, FontStyle.Bold);
    private static readonly Font LabelFont = new("Segoe UI", 9F);
    private static readonly Font DayFont = new("Segoe UI", 9.5F, FontStyle.Bold);
    private static readonly Font SectionFont = new("Segoe UI", 8.5F, FontStyle.Bold);

    private readonly LoggingService? _service;
    private DateOnly _weekMonday;
    private IReadOnlyList<DayCoverage> _last = Array.Empty<DayCoverage>();
    private bool _busy;

    private readonly Label _title = new();
    private readonly Label _weekLabel = new();
    private readonly Label _status = new();
    private readonly Label _dayHeader = new();
    private readonly Label _hrmHeader = new();
    private readonly Label _tscHeader = new();
    private readonly MacButton _prev = MacButton.Secondary("<");
    private readonly MacButton _next = MacButton.Secondary(">");
    private readonly MacButton _refresh = MacButton.Secondary("Refresh");
    private readonly Card _card = new();
    private readonly FlowLayoutPanel _rows = new();

    internal WeeklyCheckForm(LoggingService? service)
    {
        _service = service;
        _weekMonday = Hcm.MondayOf(Hcm.Today());
        BuildLayout();
        Theme.Changed += ApplyTheme;
        LoadWeekAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= ApplyTheme;
        base.Dispose(disposing);
    }

    private void BuildLayout()
    {
        Text = "Weekly check";
        Icon = AppIcon.Load(32);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.WindowBg;
        ClientSize = new Size(W, 400);

        _title.Text = "Weekly check";
        _title.AutoSize = true;
        _title.Location = new Point(20, 16);
        _title.Font = TitleFont;
        _title.ForeColor = Theme.TextPrimary;
        _title.BackColor = Color.Transparent;

        _weekLabel.AutoSize = true;
        _weekLabel.Location = new Point(22, 48);
        _weekLabel.Font = LabelFont;
        _weekLabel.ForeColor = Theme.TextSecondary;
        _weekLabel.BackColor = Color.Transparent;

        _prev.Size = new Size(34, 30);
        _prev.Location = new Point(350, 18);
        _prev.Click += (_, _) => Shift(-7);

        _next.Size = new Size(34, 30);
        _next.Location = new Point(390, 18);
        _next.Click += (_, _) => Shift(7);

        _refresh.Size = new Size(90, 30);
        _refresh.Location = new Point(430, 18);
        _refresh.Click += (_, _) => LoadWeekAsync();

        _card.Size = new Size(Inner, 266);
        _card.Location = new Point(20, 84);
        _card.Controls.Add(SectionHeader(_dayHeader, "DAY", 24));
        _card.Controls.Add(SectionHeader(_hrmHeader, "HRM (HOURS)", 166));
        _card.Controls.Add(SectionHeader(_tscHeader, "TSC (TICKET)", 316));

        _rows.Location = new Point(16, 44);
        _rows.Size = new Size(Inner - 32, 210);
        _rows.FlowDirection = FlowDirection.TopDown;
        _rows.WrapContents = false;
        _rows.AutoScroll = false;
        _rows.Padding = new Padding(0);
        _rows.BackColor = Theme.CardSurface;
        _card.Controls.Add(_rows);

        _status.AutoSize = false;
        _status.Location = new Point(20, 360);
        _status.Size = new Size(Inner, 20);
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
    }

    private static Label SectionHeader(Label label, string text, int x)
    {
        label.Text = text;
        label.AutoSize = true;
        label.Location = new Point(x, 14);
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

    private async void LoadWeekAsync()
    {
        var days = Weekdays(_weekMonday);
        _weekLabel.Text = $"{days[0]:MMM d} - {days[^1]:MMM d, yyyy}".ToUpperInvariant();

        if (_service is null) { SetStatus("Config not loaded; set up credentials first."); return; }

        SetBusy(true);
        SetStatus("Reading TSC + HRM ...");
        try
        {
            var coverage = await _service.CheckWeekAsync(days, AppendLog);
            RenderRows(coverage);
            SetStatus("Done. (Green = logged, amber = partial, red = missing, gray = pending/unknown.)");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RenderRows(IReadOnlyList<DayCoverage> coverage)
    {
        _last = coverage;
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

        var row = new Panel { Width = Inner - 36, Height = 42, Margin = new Padding(0), BackColor = Color.Transparent };

        row.Controls.Add(new Label
        {
            Text = c.Date.ToString("ddd  MMM d"),
            Location = new Point(8, 0),
            Size = new Size(130, 42),
            TextAlign = ContentAlignment.MiddleLeft,
            Font = DayFont,
            ForeColor = Theme.TextPrimary,
            BackColor = Color.Transparent,
        });

        row.Controls.Add(Dot(hColor, 150));
        row.Controls.Add(StatusLabel(hText, hColor, 168, 118));
        row.Controls.Add(Dot(tColor, 300));
        row.Controls.Add(StatusLabel(tText, tColor, 318, Inner - 36 - 318 - 6));

        row.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Divider });
        return row;
    }

    private static Panel Dot(Color color, int x) => new()
    {
        Size = new Size(10, 10),
        Location = new Point(x, 16),
        BackColor = color,
    };

    private static Label StatusLabel(string text, Color color, int x, int width) => new()
    {
        Text = text,
        Location = new Point(x, 0),
        Size = new Size(Math.Max(40, width), 42),
        TextAlign = ContentAlignment.MiddleLeft,
        Font = LabelFont,
        ForeColor = color,
        BackColor = Color.Transparent,
        AutoEllipsis = true,
        UseMnemonic = false,
    };

    private static (Color, string) HrmStatus(DayCoverage c, bool future)
    {
        if (future) return (Gray, "pending");
        if (c.HrmHours is null) return (Gray, "unknown");
        var h = c.HrmHours.Value;
        if (h >= FullDayHours) return (Green, h.ToString("0.#") + "h");
        if (h > 0) return (Amber, h.ToString("0.#") + "h");
        return (Red, "0h");
    }

    private static (Color, string) TscStatus(DayCoverage c, bool future)
    {
        if (future) return (Gray, "pending");
        if (string.IsNullOrWhiteSpace(c.TscTicket)) return (Red, "none");
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
