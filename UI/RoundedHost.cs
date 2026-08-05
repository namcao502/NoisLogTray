using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// A rounded, theme-aware container for native controls (ListBox, FlowLayoutPanel,
// TextBox) that can't round their own corners. The child docks/positions inside;
// this panel paints the rounded input surface + border.
internal sealed class RoundedHost : Panel
{
    internal int Radius = 8;

    internal RoundedHost()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        BackColor = Theme.CardSurface;
        Padding = new Padding(4);
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

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.CardSurface); // corners blend with the card behind
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
    }
}
