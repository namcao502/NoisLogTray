using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// A ticket fact (Due, P, ETA, status, Logged, a PR) as a pill: a soft fill of its tone with the text in that tone, so
// the facts read at a glance yet never look like the solid action buttons. With an action it is a link instead.
// PR status marks in the text (✓ ● ✕) are drawn as their icons; the text keeps them for notices and Teams posts.
internal sealed class FactTag : Control
{
    private static readonly int PadX = UiScale.S(8);
    private static readonly int PadY = UiScale.S(2);
    private static readonly int RunGap = UiScale.S(4);
    private const TextFormatFlags TextFlags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
        | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
    private const TextFormatFlags RunFlags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
        | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    private static readonly Dictionary<char, string> MarkGlyphs = new()
    {
        ['✓'] = Icons.CheckmarkCircle,
        ['●'] = Icons.Record,
        ['✕'] = Icons.DismissCircle,
    };
    // One icon font per text height, shared by every tag of that font (a row has several).
    private static readonly Dictionary<int, Font> MarkFonts = new();

    private readonly Action? _onClick;
    private readonly IReadOnlyList<(string Text, bool IsMark)> _runs;
    private readonly Font _markFont;
    private readonly int _contentWidth;
    private bool _hover;

    internal Color Tone { get; }

    internal FactTag(string text, Color tone, Font font, int minTextWidth = 0, Action? onClick = null)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, onClick != null);
        BackColor = Color.Transparent;
        Text = text;
        Tone = tone;
        Font = font;
        _onClick = onClick;
        TabStop = onClick != null;
        if (onClick != null) Cursor = Cursors.Hand;
        AccessibleName = text;
        AccessibleRole = onClick != null ? AccessibleRole.Link : AccessibleRole.StaticText;
        Margin = new Padding(0, 0, UiScale.S(6), UiScale.S(4));
        _runs = Runs(text);
        if (!MarkFonts.TryGetValue(font.Height, out var markFont))
        {
            markFont = Icons.Regular(font.Height / UiScale.Factor);
            MarkFonts[font.Height] = markFont;
        }
        _markFont = markFont;
        // Measured without EndEllipsis, which would cut the text to the empty proposed size.
        _contentWidth = _runs.Any(r => r.IsMark)
            ? _runs.Sum(r => r.IsMark ? _markFont.Height : TextRenderer.MeasureText(r.Text, font, Size.Empty, RunFlags).Width) + (_runs.Count - 1) * RunGap
            : TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
        Size = new Size(Math.Max(minTextWidth, _contentWidth) + 2 * PadX, font.Height + 2 * PadY);
        Theme.Changed += Invalidate;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= Invalidate;
        base.Dispose(disposing);
    }

    // The mouse's and the keyboard's way in (and the tests').
    internal void Press() => _onClick?.Invoke();

    // The text split around its status marks, each mark as its glyph: "BE main ✓ · dev ●" -> "BE main", ✓, "· dev", ●.
    internal static IReadOnlyList<(string Text, bool IsMark)> Runs(string text)
    {
        var runs = new List<(string Text, bool IsMark)>();
        var segmentStart = 0;
        for (var index = 0; index <= text.Length; index++)
        {
            var atEnd = index == text.Length;
            if (!atEnd && !MarkGlyphs.ContainsKey(text[index])) continue;
            var segment = text[segmentStart..index].Trim();
            if (segment.Length != 0) runs.Add((segment, false));
            if (!atEnd) runs.Add((MarkGlyphs[text[index]], true));
            segmentStart = index + 1;
        }
        return runs;
    }

    // The tone pushed toward the theme's text color, so it keeps its contrast on its own tint.
    internal static Color TextColor(Color tone) => Theme.Dark ? Theme.Mix(tone, Color.White, 0.35) : Theme.Mix(tone, Color.Black, 0.25);

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
        if (_onClick == null) return;
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
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        // A dark surface needs a stronger tint to show it; a hovered link a little more again.
        var alpha = (Theme.Dark ? 70 : 34) + (_hover ? 30 : 0);
        using (var path = Pill(rect))
        using (var brush = new SolidBrush(Color.FromArgb(alpha, Tone)))
            g.FillPath(brush, path);
        var foreground = TextColor(Tone);
        if (_runs.Any(r => r.IsMark))
        {
            var x = (Width - _contentWidth) / 2;
            foreach (var (runText, isMark) in _runs)
            {
                var runWidth = isMark ? _markFont.Height : TextRenderer.MeasureText(runText, Font, Size.Empty, RunFlags).Width;
                var runBounds = new Rectangle(x, 0, runWidth, Height);
                if (isMark) Icons.Draw(g, runText, _markFont, runBounds, foreground);
                else TextRenderer.DrawText(g, runText, Font, runBounds, foreground, RunFlags);
                x += runWidth + RunGap;
            }
        }
        else
        {
            TextRenderer.DrawText(g, Text, Font, rect, foreground, TextFlags);
        }

        if (Focused && ShowFocusCues)
        {
            using var focusPath = Pill(Rectangle.Inflate(rect, -UiScale.S(2), -UiScale.S(2)));
            using var pen = new Pen(TextColor(Tone), UiScale.S(1.2f)) { DashStyle = DashStyle.Dot };
            g.DrawPath(pen, focusPath);
        }
    }

    private static GraphicsPath Pill(Rectangle r)
    {
        var d = Math.Max(1, Math.Min(r.Height, r.Width));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        path.CloseFigure();
        return path;
    }
}
