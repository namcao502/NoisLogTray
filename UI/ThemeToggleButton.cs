using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// A small rounded, theme-aware icon button that toggles light/dark. Shows a sun in
// dark mode (tap to go light) and a moon in light mode (tap to go dark).
internal sealed class ThemeToggleButton : Control
{
    private bool _hover;

    internal int Radius = UiScale.S(6);
    internal bool OnWindow = true; // sits on the header (blend corners to WindowBg)

    internal ThemeToggleButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        AccessibleName = Lang.T("Toggle light and dark theme", "Đổi giao diện sáng / tối");
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

        Icons.Draw(g, Theme.Dark ? Icons.Sunny : Icons.Moon, Icons.Medium, ClientRectangle, Theme.SecondaryBtnText);
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
