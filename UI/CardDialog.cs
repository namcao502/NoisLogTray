using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace NoisLogTray;

// The app's dialog frame instead of a Windows one: a borderless rounded card with a drop shadow and the theme's
// border. There is no title bar, so dragging the card's background moves it.
internal class CardDialog : Form
{
    private static readonly int Radius = UiScale.S(12);

    protected CardDialog()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false; // a borderless form gets a taskbar button unless told otherwise
        BackColor = Theme.CardSurface;
        Icon = AppIcon.Load(32);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    // Any size change (a DPI rescale included) reshapes the rounded corners.
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        var oldRegion = Region;
        using (var path = Rounded(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), Radius))
            Region = new Region(path);
        oldRegion?.Dispose();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), Radius);
        using var pen = new Pen(Theme.CardBorder, 1f);
        e.Graphics.DrawPath(pen, path);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // Subclasses forward a background control's MouseDown here so it drags the window too.
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
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
