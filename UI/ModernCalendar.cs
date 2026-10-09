using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace NoisLogTray;

// A clean owner-drawn, theme-aware month calendar (replaces the dated native
// MonthCalendar): month arrows, weekday header, teal-filled selected day, a ring on
// today, and a hover highlight. Raises DateSelected when a day is clicked.
internal sealed class ModernCalendar : Control
{
    private DateTime _value = DateTime.Today;
    private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private int _hoverDay = -1;

    private static readonly int Pad = UiScale.S(12);
    private static readonly int HeaderH = UiScale.S(36);
    private static readonly int WeekH = UiScale.S(24);
    private static readonly int CellW = UiScale.S(30);
    private static readonly int CellH = UiScale.S(30);
    private static readonly int GridLeft = UiScale.S(15);
    private static readonly int GridTop = Pad + HeaderH + WeekH; // 72 logical
    private static readonly int ArrowW = UiScale.S(28);

    internal event EventHandler<DateTime>? DateSelected;

    internal ModernCalendar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.CardSurface;
        Font = UiScale.Font("Segoe UI", 9F);
        // Width from the scaled parts, not S(240), so the grid stays centered after rounding.
        Size = new Size(2 * GridLeft + 7 * CellW, GridTop + CellH * 6 + Pad); // 240 x 264 logical
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal DateTime Value
    {
        get => _value;
        set
        {
            _value = value.Date;
            _month = new DateTime(_value.Year, _value.Month, 1);
            Invalidate();
        }
    }

    private Rectangle PrevArrow => new(Pad, Pad, ArrowW, HeaderH);
    private Rectangle NextArrow => new(Width - Pad - ArrowW, Pad, ArrowW, HeaderH);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.CardSurface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        using (var pen = new Pen(Theme.CardBorder))
        using (var border = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), UiScale.S(8)))
            g.DrawPath(pen, border);

        using (var headFont = UiScale.Font("Segoe UI Semibold", 10.5F, FontStyle.Bold))
        {
            var header = new Rectangle(Pad + ArrowW, Pad, Width - 2 * (Pad + ArrowW), HeaderH);
            TextRenderer.DrawText(g, _month.ToString("MMMM yyyy", Lang.Culture), headFont, header, Theme.TextPrimary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        DrawChevron(g, PrevArrow, true);
        DrawChevron(g, NextArrow, false);

        var days = Lang.Vietnamese
            ? new[] { "CN", "T2", "T3", "T4", "T5", "T6", "T7" }
            : new[] { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" };
        using (var weekFont = UiScale.Font("Segoe UI", 8F, FontStyle.Bold))
            for (var c = 0; c < 7; c++)
            {
                var r = new Rectangle(GridLeft + c * CellW, Pad + HeaderH, CellW, WeekH);
                TextRenderer.DrawText(g, days[c], weekFont, r, Theme.TextSecondary,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

        var first = new DateTime(_month.Year, _month.Month, 1);
        var startCol = (int)first.DayOfWeek;
        var count = DateTime.DaysInMonth(_month.Year, _month.Month);
        var today = DateTime.Today;
        for (var d = 1; d <= count; d++)
        {
            var index = startCol + (d - 1);
            var cell = new Rectangle(GridLeft + index % 7 * CellW, GridTop + index / 7 * CellH, CellW, CellH);
            var date = new DateTime(_month.Year, _month.Month, d);
            var circle = Rectangle.Inflate(cell, -UiScale.S(3), -UiScale.S(3));

            if (date == _value)
            {
                using var brush = new SolidBrush(Theme.Accent);
                g.FillEllipse(brush, circle);
            }
            else if (d == _hoverDay)
            {
                using var brush = new SolidBrush(Theme.Hover);
                g.FillEllipse(brush, circle);
            }
            else if (date == today)
            {
                using var pen = new Pen(Theme.Accent, UiScale.S(1.4f));
                g.DrawEllipse(pen, circle);
            }

            TextRenderer.DrawText(g, d.ToString(), Font, cell, date == _value ? Color.White : Theme.TextPrimary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    private static void DrawChevron(Graphics g, Rectangle area, bool left)
    {
        var cx = area.X + area.Width / 2;
        var cy = area.Y + area.Height / 2;
        var dx = UiScale.S(3);
        var dy = UiScale.S(5);
        using var pen = new Pen(Theme.TextSecondary, UiScale.S(1.8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(pen, left
            ? new[] { new Point(cx + dx, cy - dy), new Point(cx - dx, cy), new Point(cx + dx, cy + dy) }
            : new[] { new Point(cx - dx, cy - dy), new Point(cx + dx, cy), new Point(cx - dx, cy + dy) });
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var d = DayAt(e.Location);
        if (d != _hoverDay) { _hoverDay = d; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverDay != -1) { _hoverDay = -1; Invalidate(); }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (PrevArrow.Contains(e.Location)) { _month = _month.AddMonths(-1); _hoverDay = -1; Invalidate(); return; }
        if (NextArrow.Contains(e.Location)) { _month = _month.AddMonths(1); _hoverDay = -1; Invalidate(); return; }

        var day = DayAt(e.Location);
        if (day > 0)
        {
            _value = new DateTime(_month.Year, _month.Month, day);
            Invalidate();
            DateSelected?.Invoke(this, _value);
        }
    }

    private int DayAt(Point p)
    {
        if (p.X < GridLeft || p.Y < GridTop) return -1;
        var col = (p.X - GridLeft) / CellW;
        var row = (p.Y - GridTop) / CellH;
        if (col < 0 || col > 6 || row < 0 || row > 5) return -1;
        var startCol = (int)new DateTime(_month.Year, _month.Month, 1).DayOfWeek;
        var day = row * 7 + col - startCol + 1;
        return day >= 1 && day <= DateTime.DaysInMonth(_month.Year, _month.Month) ? day : -1;
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
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
}

// Borderless, rounded, shadowed popup host for the calendar.
internal sealed class CalendarPopupForm : Form
{
    internal CalendarPopupForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Theme.CardSurface;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var d = UiScale.S(16);
        var w = ClientSize.Width;
        var h = ClientSize.Height;
        using var path = new GraphicsPath();
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(w - d, 0, d, d, 270, 90);
        path.AddArc(w - d, h - d, d, d, 0, 90);
        path.AddArc(0, h - d, d, d, 90, 90);
        path.CloseFigure();
        Region = new Region(path);
    }
}
