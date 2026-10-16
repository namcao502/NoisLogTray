using System.Drawing;

namespace NoisLogTray;

// A Label that keeps its text on one line, cut with "..." and placed as TextAlign says. A plain Label wraps a text too
// wide for it, and a wrapped text taller than the label is drawn from its top edge instead of its middle.
internal sealed class SingleLineLabel : Label
{
    // Text only, as these labels show no image; the transparent background is painted by OnPaintBackground.
    protected override void OnPaint(PaintEventArgs e)
    {
        var textBounds = new Rectangle(Padding.Left, Padding.Top, Width - Padding.Horizontal, Height - Padding.Vertical);
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, ForeColor, TextFlags(TextAlign, UseMnemonic));
    }

    internal static TextFormatFlags TextFlags(ContentAlignment textAlign, bool useMnemonic)
    {
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        if (!useMnemonic) flags |= TextFormatFlags.NoPrefix;
        flags |= textAlign switch
        {
            ContentAlignment.TopLeft or ContentAlignment.TopCenter or ContentAlignment.TopRight => TextFormatFlags.Top,
            ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight => TextFormatFlags.Bottom,
            _ => TextFormatFlags.VerticalCenter,
        };
        flags |= textAlign switch
        {
            ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter => TextFormatFlags.HorizontalCenter,
            ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight => TextFormatFlags.Right,
            _ => TextFormatFlags.Left,
        };
        return flags;
    }
}
