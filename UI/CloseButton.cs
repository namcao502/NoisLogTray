using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// A small owner-drawn remove/close button: an X glyph (two strokes) drawn with GDI+,
// not a text character, with a soft round hover/focus highlight. Theme-aware and fully
// keyboard/screen-reader operable (the Button base gives Space/Enter activation, tab
// order, and a settable AccessibleName). Used for the per-date remove control in the
// "Will log" list, so it clears to the list surface (Theme.InputBg) to blend in.
internal sealed class CloseButton : Button
{
    private bool _hover;

    internal CloseButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseOverBackColor = Color.Transparent;
        FlatAppearance.MouseDownBackColor = Color.Transparent;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PushButton;
        Theme.Changed += OnThemeChanged;
    }

    private void OnThemeChanged() => Invalidate();

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.InputBg); // blend with the will-log list behind the button
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);

        // Soft round highlight on hover/focus.
        if (_hover || Focused)
        {
            using var bg = new SolidBrush(Theme.Hover);
            g.FillEllipse(bg, rect);
        }

        // The X glyph: two diagonal strokes inset from the edges; brighter on hover.
        var inset = Math.Max(4, Width / 4);
        var left = inset;
        var right = Width - inset;
        var top = inset;
        var bottom = Height - inset;
        var color = Enabled ? (_hover ? Theme.TextPrimary : Theme.TextSecondary) : Theme.TextSecondary;
        using (var pen = new Pen(color, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(pen, left, top, right, bottom);
            g.DrawLine(pen, right, top, left, bottom);
        }

        // Keyboard-focus ring: a dotted accent ellipse just inside the edge.
        if (Focused && Enabled)
        {
            using var ring = new Pen(Theme.Accent, 1.2f) { DashStyle = DashStyle.Dot };
            g.DrawEllipse(ring, Rectangle.Inflate(rect, -1, -1));
        }
    }
}
