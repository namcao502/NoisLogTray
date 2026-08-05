using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// A thin rounded progress bar (owner-drawn, theme-aware). Set progress with
// SetFraction(0..1).
internal sealed class MiniProgress : Control
{
    private double _fraction;

    internal MiniProgress()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 6;
        Theme.Changed += OnThemeChanged;
    }

    private void OnThemeChanged() => Invalidate();

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }

    internal void SetFraction(double fraction)
    {
        _fraction = fraction < 0 ? 0 : fraction > 1 ? 1 : fraction;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (Parent != null) g.Clear(Parent.BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var radius = Height / 2f;
        DrawPill(g, new RectangleF(0, 0, Width, Height), radius, Theme.Divider);

        var fillWidth = (float)(Width * _fraction);
        if (fillWidth >= Height)
            DrawPill(g, new RectangleF(0, 0, fillWidth, Height), radius, Theme.Accent);
    }

    private static void DrawPill(Graphics g, RectangleF rect, float radius, Color color)
    {
        var d = radius * 2;
        using var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 90, 180);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 180);
        path.CloseFigure();
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }
}
