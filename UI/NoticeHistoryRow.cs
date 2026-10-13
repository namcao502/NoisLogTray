using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// One notice in the bell's history: its kind's icon, the message (wrapped, up to three lines; the rest in a tooltip)
// and its time beneath. A notice with a target is clickable and keyboard-activatable; the others only read.
internal sealed class NoticeHistoryRow : Control
{
    private static readonly Font MessageFont = UiScale.Font("Segoe UI", 9.5F);
    private static readonly Font TimeFont = UiScale.Font("Segoe UI", 8F);
    private static readonly int PadX = UiScale.S(12);
    private static readonly int PadY = UiScale.S(8);
    private static readonly int IconWidth = UiScale.S(20);
    private static readonly int IconGap = UiScale.S(10);
    private static readonly int Radius = UiScale.S(6);
    private const int MaxLines = 3;
    // NoPadding, like the time beneath, so the two start at the same x.
    private const TextFormatFlags MessageFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix
        | TextFormatFlags.TextBoxControl | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
    private static readonly ThemedToolTip FullMessageTip = new();

    private readonly NoticeKind _kind;
    private readonly string _time;
    private readonly bool _clickable;
    private readonly Rectangle _messageBounds;
    private bool _hover;

    internal event Action? Pressed;

    internal bool IsCut { get; }

    internal bool Striped { get; }

    // Centered on the whole notice (message and time), not on its first line.
    internal Rectangle IconBounds => new(PadX, 0, IconWidth, Height);

    internal NoticeHistoryRow(DateTime time, string message, NoticeKind kind, bool clickable, int width, bool striped = false)
    {
        Striped = striped;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, clickable);
        TabStop = clickable;
        if (clickable) Cursor = Cursors.Hand;
        _kind = kind;
        _time = time.ToString("HH:mm");
        _clickable = clickable;
        Text = message;
        AccessibleRole = clickable ? AccessibleRole.Link : AccessibleRole.StaticText;
        AccessibleName = $"{_time} {message}";

        var textLeft = PadX + IconWidth + IconGap;
        var textWidth = Math.Max(UiScale.S(40), width - textLeft - PadX);
        var maxHeight = MaxLines * MessageFont.Height;
        var fullHeight = TextRenderer.MeasureText(message, MessageFont, new Size(textWidth, int.MaxValue), MessageFlags).Height;
        IsCut = fullHeight > maxHeight;
        _messageBounds = new Rectangle(textLeft, PadY, textWidth, Math.Min(fullHeight, maxHeight));
        Size = new Size(width, _messageBounds.Bottom + UiScale.S(2) + TimeFont.Height + PadY);
        if (IsCut) FullMessageTip.SetUntilDisposed(this, message);
        Theme.Changed += Invalidate;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= Invalidate;
        base.Dispose(disposing);
    }

    // The mouse's and the keyboard's way in (and the tests').
    internal void Press()
    {
        if (_clickable) Pressed?.Invoke();
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Press();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) Press();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (!_clickable) return;
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnChangeUICues(UICuesEventArgs e) { Invalidate(); base.OnChangeUICues(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Striped ? Theme.RowStripe(Theme.CardSurface) : Theme.CardSurface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var highlight = Rectangle.Inflate(ClientRectangle, -UiScale.S(4), -UiScale.S(1));
        if (_hover || Focused && ShowFocusCues)
        {
            using var path = Rounded(highlight, Radius);
            using var brush = new SolidBrush(Theme.Hover);
            g.FillPath(brush, path);
        }

        var glyph = _kind == NoticeKind.Success ? Icons.CheckmarkCircle : _kind == NoticeKind.Error ? Icons.ErrorCircle : Icons.Info;
        Icons.Draw(g, glyph, Icons.Medium, IconBounds, NoticeToast.AccentFor(_kind));
        TextRenderer.DrawText(g, Text, MessageFont, _messageBounds, Theme.TextPrimary, MessageFlags);
        TextRenderer.DrawText(g, _time, TimeFont, new Point(_messageBounds.Left, _messageBounds.Bottom + UiScale.S(2)), Theme.TextSecondary,
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        if (Focused && ShowFocusCues)
        {
            using var path = Rounded(highlight, Radius);
            using var pen = new Pen(Theme.Accent, UiScale.S(1.2f)) { DashStyle = DashStyle.Dot };
            g.DrawPath(pen, path);
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
