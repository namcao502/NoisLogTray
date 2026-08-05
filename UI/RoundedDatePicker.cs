using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// A rounded, padded, theme-aware date field. Shows the selected date + a chevron;
// clicking opens a ModernCalendar popup. Replaces the native DateTimePicker.
internal sealed class RoundedDatePicker : Control
{
    private DateTime _value = DateTime.Today;

    internal int Radius = 8;

    internal event EventHandler? ValueChanged;

    internal RoundedDatePicker()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.CardSurface;
        Font = new Font("Segoe UI", 9.5F);
        Cursor = Cursors.Hand;
        Theme.Changed += OnThemeChanged;
    }

    private void OnThemeChanged()
    {
        BackColor = Theme.CardSurface;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal DateTime Value
    {
        get => _value;
        set
        {
            if (_value == value.Date) return;
            _value = value.Date;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.CardSurface);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        var d = Radius * 2;
        using var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        using (var brush = new SolidBrush(Theme.InputBg)) g.FillPath(brush, path);
        using (var pen = new Pen(Theme.InputBorder)) g.DrawPath(pen, path);

        var textRect = new Rectangle(10, 0, Width - 34, Height);
        TextRenderer.DrawText(g, _value.ToShortDateString(), Font, textRect, Theme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        var cx = Width - 18;
        var cy = Height / 2;
        using var chevron = new Pen(Theme.TextSecondary, 1.6f);
        g.DrawLines(chevron, new[] { new Point(cx - 4, cy - 2), new Point(cx, cy + 2), new Point(cx + 4, cy - 2) });
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        ShowPicker();
    }

    private void ShowPicker()
    {
        var calendar = new ModernCalendar { Value = _value, Location = new Point(0, 0) };
        var popup = new CalendarPopupForm();
        popup.ClientSize = calendar.Size;
        popup.Controls.Add(calendar);
        popup.Location = PointToScreen(new Point(0, Height + 4));
        calendar.DateSelected += (_, date) => { Value = date; popup.Close(); };
        popup.Deactivate += (_, _) => popup.Close();
        popup.Show();
        popup.Activate();
    }
}
