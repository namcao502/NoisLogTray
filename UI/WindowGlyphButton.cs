using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// Ghost header button in the CloseButton style (round hover/focus highlight), drawing a
// GDI glyph: the sidebar menu toggle, minimize, maximize, or restore.
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

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.WindowBg);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        if (_hover || Focused)
        {
            using var bg = new SolidBrush(Theme.Hover);
            g.FillEllipse(bg, rect);
        }

        var color = _hover ? Theme.TextPrimary : Theme.TextSecondary;
        using (var pen = new Pen(color, UiScale.S(1.4f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            var cx = Width / 2f;
            var cy = Height / 2f;
            var half = UiScale.S(5f);
            var step = UiScale.S(4f);
            var mid = UiScale.S(2.5f);
            switch (_glyph)
            {
                case GlyphKind.Menu:
                    for (var line = -1; line <= 1; line++)
                        g.DrawLine(pen, cx - half, cy + line * step, cx + half, cy + line * step);
                    break;
                case GlyphKind.Minimize:
                    g.DrawLine(pen, cx - half, cy, cx + half, cy);
                    break;
                case GlyphKind.Maximize:
                    var box = UiScale.S(9f);
                    g.DrawRectangle(pen, cx - box / 2, cy - box / 2, box, box);
                    break;
                case GlyphKind.Restore:
                    var front = UiScale.S(7.5f);
                    g.DrawRectangle(pen, cx - half, cy - mid, front, front);
                    g.DrawLine(pen, cx - mid, cy - mid, cx - mid, cy - half);
                    g.DrawLine(pen, cx - mid, cy - half, cx + half, cy - half);
                    g.DrawLine(pen, cx + half, cy - half, cx + half, cy + mid);
                    g.DrawLine(pen, cx + half, cy + mid, cx + mid, cy + mid);
                    break;
            }
        }

        if (Focused && Enabled)
        {
            using var ring = new Pen(Theme.Accent, UiScale.S(1.2f)) { DashStyle = DashStyle.Dot };
            g.DrawEllipse(ring, Rectangle.Inflate(rect, -1, -1));
        }
    }
}
