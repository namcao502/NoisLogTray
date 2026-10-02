using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// Header icon button that switches the UI language between English and Vietnamese. Shows
// the current language code ("EN" / "VI"); a click calls Lang.Toggle.
internal sealed class LanguageToggleButton : Control
{
    private static readonly Font CodeFont = new("Segoe UI Semibold", 8.5F, FontStyle.Bold);
    private bool _hover;

    internal int Radius = 6;

    internal LanguageToggleButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PushButton;
        UpdateAccessibleName();
        Theme.Changed += Invalidate;
        Lang.Changed += OnLanguageChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= Invalidate;
            Lang.Changed -= OnLanguageChanged;
        }
        base.Dispose(disposing);
    }

    private void OnLanguageChanged()
    {
        UpdateAccessibleName();
        Invalidate();
    }

    private void UpdateAccessibleName() =>
        AccessibleName = Lang.T("Language: English. Switch to Vietnamese", "Ngôn ngữ: Tiếng Việt. Chuyển sang tiếng Anh");

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Lang.Toggle();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.WindowBg); // sits on the header; corners blend into the window
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Rounded(rect, Radius))
        using (var brush = new SolidBrush(_hover ? Theme.SecondaryBtnHover : Theme.SecondaryBtn))
            g.FillPath(brush, path);

        TextRenderer.DrawText(g, Lang.Vietnamese ? "VI" : "EN", CodeFont, rect, Theme.SecondaryBtnText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
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
