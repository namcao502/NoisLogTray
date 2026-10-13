using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// Ghost header button in the CloseButton style (round hover/focus highlight), drawing an
// Icons glyph: the sidebar menu toggle, minimize, maximize, or restore.
internal sealed class WindowGlyphButton : Button
{
    internal enum GlyphKind { Menu, Minimize, Maximize, Restore }

    private bool _hover;
    private GlyphKind _glyph;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal GlyphKind Glyph
    {
        get => _glyph;
        set { _glyph = value; Invalidate(); }
    }

    internal WindowGlyphButton(GlyphKind glyph)
    {
        _glyph = glyph;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseOverBackColor = Color.Transparent;
        FlatAppearance.MouseDownBackColor = Color.Transparent;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PushButton;
        Theme.Changed += Invalidate;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= Invalidate;
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
        g.Clear(Theme.WindowBg);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        if (_hover || Focused && ShowFocusCues)
        {
            using var bg = new SolidBrush(Theme.Hover);
            g.FillEllipse(bg, rect);
        }

        var glyph = _glyph switch
        {
            GlyphKind.Menu => Icons.Navigation,
            GlyphKind.Minimize => Icons.Minimize,
            GlyphKind.Maximize => Icons.Maximize,
            _ => Icons.Restore,
        };
        // The window controls are smaller than the menu toggle, as in Windows' own caption buttons.
        Icons.Draw(g, glyph, _glyph == GlyphKind.Menu ? Icons.Medium : Icons.Small, ClientRectangle,
            _hover ? Theme.TextPrimary : Theme.TextSecondary);

        if (Focused && Enabled && ShowFocusCues)
        {
            using var ring = new Pen(Theme.Accent, UiScale.S(1.2f)) { DashStyle = DashStyle.Dot };
            g.DrawEllipse(ring, Rectangle.Inflate(rect, -1, -1));
        }
    }
}
