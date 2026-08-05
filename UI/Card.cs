using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// A rounded card surface (theme-aware) on the window body.
internal sealed class Card : Panel
{
    internal int Radius = 12;

    internal Card()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Theme.WindowBg; // so the corners blend with the window
        Theme.Changed += OnThemeChanged;
    }

    private void OnThemeChanged()
    {
        BackColor = Theme.WindowBg;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        var d = Radius * 2;
        using var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        using (var brush = new SolidBrush(Theme.CardSurface)) g.FillPath(brush, path);
        using (var pen = new Pen(Theme.CardBorder)) g.DrawPath(pen, path);
    }
}
