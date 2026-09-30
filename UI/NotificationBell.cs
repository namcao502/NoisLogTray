using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// Header icon button next to the theme toggle: a GDI-drawn bell with a red dot while
// there are notices the user has not opened. The owner handles Click (opens the history).
internal sealed class NotificationBell : Control
{
    private static readonly Color DotColor = Color.FromArgb(230, 76, 76);
    private bool _hover;
    private bool _hasUnread;

    internal int Radius = 6;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool HasUnread
    {
        get => _hasUnread;
        set
        {
            _hasUnread = value;
            AccessibleName = value ? "Notifications (unread)" : "Notifications";
            Invalidate();
        }
    }

    internal NotificationBell()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        AccessibleName = "Notifications";
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

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.WindowBg); // sits on the header; corners blend into the window
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Rounded(rect, Radius))
        using (var brush = new SolidBrush(_hover ? Theme.SecondaryBtnHover : Theme.SecondaryBtn))
            g.FillPath(brush, path);

        var cx = Width / 2f;
        var cy = Height / 2f;
        using (var bell = new GraphicsPath())
        using (var brush = new SolidBrush(Theme.SecondaryBtnText))
        {
            // Dome, flared sides, flat rim; then the clapper below the rim.
            bell.AddArc(cx - 5f, cy - 8f, 10f, 10f, 180, 180);
            bell.AddLine(cx + 5f, cy - 3f, cx + 7f, cy + 4f);
            bell.AddLine(cx + 7f, cy + 4f, cx - 7f, cy + 4f);
            bell.AddLine(cx - 7f, cy + 4f, cx - 5f, cy - 3f);
            bell.CloseFigure();
            g.FillPath(brush, bell);
            g.FillEllipse(brush, cx - 2f, cy + 5f, 4f, 4f);
        }

        if (_hasUnread)
        {
            using var dot = new SolidBrush(DotColor);
            g.FillEllipse(dot, Width - 12, 4, 7, 7);
        }
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
