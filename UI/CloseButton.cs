using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// A small owner-drawn remove/close button: the Icons X with a soft round hover/focus
// highlight. Theme-aware and fully
// keyboard/screen-reader operable (the Button base gives Space/Enter activation, tab
// order, and a settable AccessibleName). Used for the per-date remove control in the
// "Will log" list, so it clears to the list surface (Theme.InputBg) to blend in.
internal sealed class CloseButton : Button
{
    private bool _hover;

    // The colour behind the button, read at paint time so a theme switch follows it.
    // Defaults to the will-log list; the Settings card and the window header override it.
    internal Func<Color> Surface = () => Theme.InputBg;

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
    protected override void OnChangeUICues(UICuesEventArgs e) { Invalidate(); base.OnChangeUICues(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Surface()); // blend with what is behind the button
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);

        // Soft round highlight on hover/focus.
        if (_hover || Focused && ShowFocusCues)
        {
            using var bg = new SolidBrush(Theme.Hover);
            g.FillEllipse(bg, rect);
        }

        // Brighter on hover.
        var color = Enabled ? (_hover ? Theme.TextPrimary : Theme.TextSecondary) : Theme.TextSecondary;
        Icons.Draw(g, Icons.Dismiss, Icons.Small, ClientRectangle, color);

        // Keyboard-focus ring: a dotted accent ellipse just inside the edge.
        if (Focused && Enabled && ShowFocusCues)
        {
            using var ring = new Pen(Theme.Accent, UiScale.S(1.2f)) { DashStyle = DashStyle.Dot };
            g.DrawEllipse(ring, Rectangle.Inflate(rect, -1, -1));
        }
    }
}
