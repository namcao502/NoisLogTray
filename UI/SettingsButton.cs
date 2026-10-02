using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// Header icon button (rightmost) with a GDI-drawn gear; the owner handles Click to open the
// Settings dialog, so users need not find it in the tray icon's right-click menu.
internal sealed class SettingsButton : Control
{
    private bool _hover;

    internal int Radius = 6;

    internal SettingsButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PushButton;
        UpdateAccessibleName();
        Theme.Changed += Invalidate;
        Lang.Changed += UpdateAccessibleName;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= Invalidate;
            Lang.Changed -= UpdateAccessibleName;
        }
        base.Dispose(disposing);
    }

    private void UpdateAccessibleName() => AccessibleName = Lang.T("Settings", "Cài đặt");

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.WindowBg); // sits on the header; corners blend into the window
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var fill = _hover ? Theme.SecondaryBtnHover : Theme.SecondaryBtn;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Rounded(rect, Radius))
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);

        // Gear: eight square teeth around a solid ring, with a hole in the button colour.
        var cx = Width / 2f;
        var cy = Height / 2f;
        using (var teeth = new Pen(Theme.SecondaryBtnText, 3f))
        {
            for (var tooth = 0; tooth < 8; tooth++)
            {
                var angle = tooth * Math.PI / 4;
                var cos = (float)Math.Cos(angle);
                var sin = (float)Math.Sin(angle);
                g.DrawLine(teeth, cx + cos * 5f, cy + sin * 5f, cx + cos * 8.5f, cy + sin * 8.5f);
            }
        }
        using (var body = new SolidBrush(Theme.SecondaryBtnText))
            g.FillEllipse(body, cx - 6f, cy - 6f, 12f, 12f);
        using (var hole = new SolidBrush(fill))
            g.FillEllipse(hole, cx - 2.5f, cy - 2.5f, 5f, 5f);
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
