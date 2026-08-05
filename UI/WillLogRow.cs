using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NoisLogTray;

// One line of the "Will log" preview, drawn in a single paint pass so the status
// dot, ticket key, and time slots always share the same baseline (a stack of
// transparent Labels does not reliably line up). Only the key is colored; the
// time slots sit right-aligned in the secondary color.
internal sealed class WillLogRow : Control
{
    private static readonly Font KeyFont = new("Segoe UI", 9F, FontStyle.Bold);
    private static readonly Font TextFont = new("Segoe UI", 9F);

    private const TextFormatFlags LeftFlags =
        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

    internal Color DotColor = Color.FromArgb(150, 150, 156);
    internal string Key = "";
    internal Color KeyColor = Color.Gray;
    internal string Slots = "";

    internal WillLogRow()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 22;
        Margin = new Padding(0);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.InputBg);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var dot = new SolidBrush(DotColor))
            g.FillEllipse(dot, 4, (Height - 8) / 2f, 8, 8);

        var keyRect = new Rectangle(18, 0, Width - 18, Height);
        TextRenderer.DrawText(g, Key, KeyFont, keyRect, KeyColor, LeftFlags);

        const int slotsW = 150;
        var slotsRect = new Rectangle(Width - slotsW - 2, 0, slotsW, Height);
        TextRenderer.DrawText(g, Slots, TextFont, slotsRect, Theme.TextSecondary,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
