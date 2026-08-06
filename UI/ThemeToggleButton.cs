using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// A small rounded, theme-aware icon button that toggles light/dark. Draws a sun in
// dark mode (tap to go light) and a moon in light mode (tap to go dark). Icons are
// drawn with GDI (no glyph fonts).
internal sealed class ThemeToggleButton : Control
{
    private bool _hover;

    internal int Radius = 6;
    internal bool OnWindow = true; // sits on the header (blend corners to WindowBg)

    internal ThemeToggleButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        AccessibleName = "Toggle light and dark theme";
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

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Theme.Toggle();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(OnWindow ? Theme.WindowBg : Theme.CardSurface);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        var fill = _hover ? Theme.SecondaryBtnHover : Theme.SecondaryBtn;
        using (var path = Rounded(rect, Radius))
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);

        var cx = Width / 2f;
        var cy = Height / 2f;
        var icon = Theme.SecondaryBtnText;
        if (Theme.Dark) DrawSun(g, cx, cy, icon);
        else DrawMoon(g, cx, cy, icon, fill);
    }

    private static void DrawSun(Graphics g, float cx, float cy, Color color)
    {
        const float r = 4f;
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, cx - r, cy - r, 2 * r, 2 * r);
        using var pen = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (var i = 0; i < 8; i++)
        {
            var a = i * Math.PI / 4;
            var x1 = cx + (float)Math.Cos(a) * (r + 2);
            var y1 = cy + (float)Math.Sin(a) * (r + 2);
            var x2 = cx + (float)Math.Cos(a) * (r + 5);
            var y2 = cy + (float)Math.Sin(a) * (r + 5);
            g.DrawLine(pen, x1, y1, x2, y2);
        }
    }

    private static void DrawMoon(Graphics g, float cx, float cy, Color color, Color bg)
    {
        const float r = 6.5f;
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, cx - r, cy - r, 2 * r, 2 * r);
        using var carve = new SolidBrush(bg);
        g.FillEllipse(carve, cx - r + 4.5f, cy - r - 1.5f, 2 * r, 2 * r);
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
