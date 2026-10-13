using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// Flat rounded button in the macOS idiom, theme-aware: primary = system blue, tinted = its own fill,
// secondary = neutral. Fully owner-drawn.
internal sealed class MacButton : Button
{
    private readonly bool _secondary;
    private readonly Color? _tint;
    private bool _hover;
    private bool _selected;
    private bool _showDot;

    // A secondary button drawn as primary while selected: the chosen pill in an option group.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    // A small attention dot in the top-right corner.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool ShowDot
    {
        get => _showDot;
        set { _showDot = value; Invalidate(); }
    }

    internal int Radius = UiScale.S(6);
    internal bool OnWindow; // true = sits on the window body (blend corners to WindowBg)
    internal Func<Color>? Surface; // what the corners blend into when it is neither (a striped list row)

    // An Icons glyph: alone when Text is empty, else before the text (after it with GlyphAfterText, a dropdown's chevron).
    private string? _glyph;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string? Glyph
    {
        get => _glyph;
        set { _glyph = value; Invalidate(); }
    }

    internal bool GlyphAfterText;
    internal Font GlyphFont = Icons.Small;

    private MacButton(bool secondary, Color? tint = null)
    {
        _secondary = secondary;
        _tint = tint;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseOverBackColor = Color.Transparent;
        FlatAppearance.MouseDownBackColor = Color.Transparent;
        BackColor = Color.Transparent;
        Font = UiScale.Font("Segoe UI", 9.5F);
        Cursor = Cursors.Hand;
        Theme.Changed += OnThemeChanged;
    }

    internal static MacButton Primary(string text) => new(false) { Text = text };
    internal static MacButton Secondary(string text) => new(true) { Text = text };
    internal static MacButton Tinted(string text, Color tint) => new(false, tint) { Text = text };

    internal Color? Tint => _tint;

    private void OnThemeChanged() => Invalidate();

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    // Repaint on focus change so the keyboard-focus ring appears/disappears. Button
    // already handles Space/Enter activation and tab order; this only adds the visual.
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnChangeUICues(UICuesEventArgs e) { Invalidate(); base.OnChangeUICues(e); }

    // Only while Windows shows keyboard focus cues (after Tab): a clicked button keeps focus, and a ring left on it
    // after the mouse moves on reads as a stuck state. The other owner-drawn controls follow the same rule.
    internal bool FocusRingVisible => Focused && Enabled && ShowFocusCues;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Surface?.Invoke() ?? (OnWindow ? Theme.WindowBg : Theme.CardSurface)); // corners blend into the surface behind
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Rounded(rect, Radius);

        Color fill;
        Color textColor;
        if (!Enabled)
        {
            fill = Theme.Dark ? Color.FromArgb(58, 58, 62) : Color.FromArgb(232, 232, 234);
            textColor = Theme.TextSecondary;
        }
        else if (_secondary && !_selected)
        {
            fill = _hover ? Theme.SecondaryBtnHover : Theme.SecondaryBtn;
            textColor = Theme.SecondaryBtnText;
        }
        else
        {
            fill = _tint == null ? (_hover ? Theme.AccentHover : Theme.Accent) : (_hover ? Lighten(_tint.Value) : _tint.Value);
            textColor = Color.White;
        }

        using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
        if (_glyph == null)
        {
            TextRenderer.DrawText(g, Text, Font, rect, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        else
        {
            var textWidth = Text.Length == 0 ? 0 : TextRenderer.MeasureText(Text, Font).Width;
            var (textBounds, glyphBounds) = ContentBounds(Size, textWidth, GlyphFont.Height, UiScale.S(2), GlyphAfterText);
            if (textWidth != 0)
                TextRenderer.DrawText(g, Text, Font, textBounds, textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Icons.Draw(g, _glyph, GlyphFont, glyphBounds, textColor);
        }

        if (_showDot)
        {
            var dotDiameter = UiScale.S(8);
            using var dotBrush = new SolidBrush(Theme.Badge);
            g.FillEllipse(dotBrush, rect.Right - dotDiameter - UiScale.S(3), UiScale.S(3), dotDiameter, dotDiameter);
        }

        // Keyboard-focus ring: a dotted inset outline, white on a primary or tinted fill,
        // accent-coloured on the neutral secondary fill.
        if (FocusRingVisible)
        {
            var focusRect = Rectangle.Inflate(rect, -UiScale.S(3), -UiScale.S(3));
            using var focusPath = Rounded(focusRect, Math.Max(UiScale.S(2), Radius - UiScale.S(2)));
            using var pen = new Pen(_secondary && !_selected ? Theme.Accent : Color.White, UiScale.S(1.4f)) { DashStyle = DashStyle.Dot };
            g.DrawPath(pen, focusPath);
        }
    }

    // The text and the glyph side by side, the pair centered; a lone glyph (no text) is centered by itself.
    internal static (Rectangle Text, Rectangle Glyph) ContentBounds(Size button, int textWidth, int glyphWidth, int gap, bool glyphAfterText)
    {
        var pairWidth = textWidth == 0 ? glyphWidth : textWidth + gap + glyphWidth;
        var left = (button.Width - pairWidth) / 2;
        if (textWidth == 0) return (Rectangle.Empty, new Rectangle(left, 0, glyphWidth, button.Height));
        var glyphLeft = glyphAfterText ? left + textWidth + gap : left;
        var textLeft = glyphAfterText ? left : left + glyphWidth + gap;
        return (new Rectangle(textLeft, 0, textWidth, button.Height), new Rectangle(glyphLeft, 0, glyphWidth, button.Height));
    }

    // Hover: 8% toward white, as AccentHover is to Accent.
    private static Color Lighten(Color color) => Color.FromArgb(
        color.R + (255 - color.R) * 8 / 100, color.G + (255 - color.G) * 8 / 100, color.B + (255 - color.B) * 8 / 100);

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        if (d <= 0)
        {
            path.AddRectangle(r);
            path.CloseFigure();
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
