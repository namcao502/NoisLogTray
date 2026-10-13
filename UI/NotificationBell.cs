using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// Header icon button next to the theme toggle: the Icons bell with a red dot while
// there are notices the user has not opened. The owner handles Click (opens the history).
internal sealed class NotificationBell : Control
{
    private bool _hover;
    private bool _hasUnread;

    internal int Radius = UiScale.S(6);

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

        Icons.Draw(g, Icons.Alert, Icons.Medium, ClientRectangle, Theme.SecondaryBtnText);

        if (_hasUnread)
        {
            using var dot = new SolidBrush(Theme.Badge);
            g.FillEllipse(dot, Width - UiScale.S(12), UiScale.S(4), UiScale.S(7), UiScale.S(7));
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
