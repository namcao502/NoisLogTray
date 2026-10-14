using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace NoisLogTray;

// A soft drop shadow for a borderless rounded window: a click-through layered window kept just below it. Windows' own
// CS_DROPSHADOW is only a faint right and bottom edge on Windows 10, and is gone when "Show shadows under windows" is off.
internal sealed class WindowShadow : Form
{
    private static readonly int Blur = UiScale.S(20);
    private static readonly int OffsetY = UiScale.S(5); // a light from above: more shadow under the window than over it

    private readonly Form _target;
    private readonly int _radius;
    private Size _paintedSize;
    private bool _paintedDark;

    // Lives as long as the target; nothing else holds it.
    internal static void Attach(Form target, int radius)
    {
        _ = new WindowShadow(target, radius);
    }

    private WindowShadow(Form target, int radius)
    {
        _target = target;
        _radius = radius;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        target.VisibleChanged += (_, _) => Follow();
        target.LocationChanged += (_, _) => Follow();
        target.SizeChanged += (_, _) => Follow();
        target.Disposed += (_, _) => Dispose();
        Theme.Changed += Follow;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate;
            return cp;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= Follow;
        }
        base.Dispose(disposing);
    }

    private void Follow()
    {
        if (IsDisposed || _target.IsDisposed)
        {
            return;
        }
        if (!_target.Visible || _target.WindowState == FormWindowState.Minimized)
        {
            Hide();
            return;
        }

        // Owned like the target, so it shares the target's place above the app window and goes with it.
        if (Owner != _target.Owner)
        {
            Owner = _target.Owner;
        }
        TopMost = _target.TopMost;
        if (!Visible)
        {
            Show();
        }

        var shadowLocation = new Point(_target.Left - Blur, _target.Top - Blur + OffsetY);
        // Drawn again only for a new size or theme, so dragging the window only moves this one.
        if (_paintedSize != _target.Size || _paintedDark != Theme.Dark)
        {
            using var shadow = DrawShadow(_target.Size);
            PaintLayered(shadow, shadowLocation);
            _paintedSize = _target.Size;
            _paintedDark = Theme.Dark;
        }
        SetWindowPos(Handle, _target.Handle, shadowLocation.X, shadowLocation.Y, 0, 0, SwpNoSize | SwpNoActivate);
    }

    // A blurred copy of the window's shape: its alpha follows a soft step across the window's edge (half strength on the
    // edge, fading over Blur outside and rising inside, so the offset never shows a hard line). Rounded rectangles are
    // filled from the outermost in, each one's alpha chosen so the stack adds up to that curve.
    private Bitmap DrawShadow(Size targetSize)
    {
        var shadow = new Bitmap(targetSize.Width + 2 * Blur, targetSize.Height + 2 * Blur, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(shadow);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        // Stronger on the dark theme, where black on a near-black window barely shows.
        var fullAlpha = Theme.Dark ? 0.7 : 0.42;
        var spread = Blur / 3.0;
        var stackedAlpha = 0.0;
        for (var inflate = Blur; inflate >= -Blur; inflate--)
        {
            var targetAlpha = fullAlpha / (1 + Math.Exp(1.7 * inflate / spread));
            var layerAlpha = 1 - (1 - targetAlpha) / (1 - stackedAlpha);
            stackedAlpha = targetAlpha;
            using var brush = new SolidBrush(Color.FromArgb((int)Math.Round(layerAlpha * 255), Color.Black));
            var layer = new Rectangle(Blur - inflate, Blur - inflate, targetSize.Width + 2 * inflate, targetSize.Height + 2 * inflate);
            using var path = Rounded(layer, Math.Max(_radius + inflate, 1));
            graphics.FillPath(brush, path);
        }
        return shadow;
    }

    private void PaintLayered(Bitmap shadow, Point screenLocation)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmapHandle = shadow.GetHbitmap(Color.FromArgb(0)); // keeps the alpha channel
        var previousBitmap = SelectObject(memoryDc, bitmapHandle);
        try
        {
            var size = new NativeSize { Width = shadow.Width, Height = shadow.Height };
            var destination = new NativePoint { X = screenLocation.X, Y = screenLocation.Y };
            var source = new NativePoint();
            var blend = new BlendFunction { BlendOp = AcSrcOver, SourceConstantAlpha = 255, AlphaFormat = AcSrcAlpha };
            UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, UlwAlpha);
        }
        finally
        {
            SelectObject(memoryDc, previousBitmap);
            DeleteObject(bitmapHandle);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;
    private const int UlwAlpha = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref NativePoint pptDst, ref NativeSize psize,
        IntPtr hdcSrc, ref NativePoint pptSrc, int crKey, ref BlendFunction pblend, int dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
