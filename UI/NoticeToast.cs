using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// A small rounded notice shown under the header bell, then hidden after a few seconds
// (longer for errors). A sticky notice stays until replaced or hidden - used while an
// action runs. Click to dismiss early.
internal sealed class NoticeToast : Control
{
    private const int Radius = 8;
    private const int BarW = 4;
    private const int PadX = 12;
    private const int PadY = 9;
    private static readonly Color SuccessColor = Color.FromArgb(46, 160, 80);
    private static readonly Color ErrorColor = Color.FromArgb(230, 76, 76);

    private readonly System.Windows.Forms.Timer _timer = new();
    private NoticeKind _kind;

    internal bool IsSticky { get; private set; }

    internal NoticeToast()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = new Font("Segoe UI", 9F);
        Cursor = Cursors.Hand;
        Visible = false;
        AccessibleRole = AccessibleRole.Alert;
        _timer.Tick += (_, _) => HideNotice();
        Theme.Changed += Invalidate;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= Invalidate;
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }

    internal static Color AccentFor(NoticeKind kind) => kind switch
    {
        NoticeKind.Success => SuccessColor,
        NoticeKind.Error => ErrorColor,
        _ => Theme.Accent,
    };

    internal void ShowNotice(string message, NoticeKind kind, bool sticky = false)
    {
        _kind = kind;
        IsSticky = sticky;
        Text = message;
        AccessibleName = message;

        // Grow to fit the wrapped text (the width is fixed by the owner).
        var textW = Width - BarW - 2 * PadX;
        var textH = TextRenderer.MeasureText(message, Font, new Size(textW, int.MaxValue), TextFormatFlags.WordBreak).Height;
        Height = Math.Max(36, textH + 2 * PadY);
        using (var path = Rounded(new Rectangle(0, 0, Width, Height), Radius))
            Region = new Region(path);

        Visible = true;
        BringToFront();
        Invalidate();

        _timer.Stop();
        if (!sticky)
        {
            _timer.Interval = kind == NoticeKind.Error ? 6000 : 3000;
            _timer.Start();
        }
    }

    internal void HideNotice()
    {
        _timer.Stop();
        IsSticky = false;
        Visible = false;
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        HideNotice();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var accent = AccentFor(_kind);

        g.Clear(Theme.InputBg);
        using (var bar = new SolidBrush(accent)) g.FillRectangle(bar, 0, 0, BarW, Height);
        using (var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Radius))
        using (var pen = new Pen(Color.FromArgb(160, accent), 1f))
            g.DrawPath(pen, path);

        var textRect = new Rectangle(BarW + PadX, PadY, Width - BarW - 2 * PadX, Height - 2 * PadY);
        TextRenderer.DrawText(g, Text, Font, textRect, Theme.TextPrimary,
            TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
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
