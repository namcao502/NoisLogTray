using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// Flat rounded button in the macOS idiom, theme-aware: primary = system blue,
// secondary = neutral. Fully owner-drawn.
internal sealed class MacButton : Button
{
    private readonly bool _secondary;
    private bool _hover;

    internal int Radius = 6;
    internal bool OnWindow; // true = sits on the window body (blend corners to WindowBg)

    private MacButton(bool secondary)
    {
        _secondary = secondary;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseOverBackColor = Color.Transparent;
        FlatAppearance.MouseDownBackColor = Color.Transparent;
        BackColor = Color.Transparent;
        Font = new Font("Segoe UI", 9.5F);
        Cursor = Cursors.Hand;
        Theme.Changed += OnThemeChanged;
    }

    internal static MacButton Primary(string text) => new(false) { Text = text };
    internal static MacButton Secondary(string text) => new(true) { Text = text };

    private void OnThemeChanged() => Invalidate();

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(OnWindow ? Theme.WindowBg : Theme.CardSurface); // corners blend into the surface behind
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Rounded(rect, Radius);

        Color fill;
        Color textColor;
        if (!Enabled)
        {
            fill = Theme.Dark ? Color.FromArgb(58, 58, 62) : Color.FromArgb(232, 232, 234);
            textColor = Theme.TextSecondary;
        }
        else if (_secondary)
        {
            fill = _hover ? Theme.SecondaryBtnHover : Theme.SecondaryBtn;
            textColor = Theme.SecondaryBtnText;
        }
        else
        {
            fill = _hover ? Theme.AccentHover : Theme.Accent;
            textColor = Color.White;
        }

        using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
        TextRenderer.DrawText(g, Text, Font, rect, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        if (d <= 0)
        {
            path.AddRectangle(r);
            path.CloseFigure();
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
