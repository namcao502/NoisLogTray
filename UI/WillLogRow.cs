using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NoisLogTray;

// One line of the "Will log" preview (queued view): a status dot, the colored ticket
// key, its time slots right after the key, and a remove [X] on the far right that drops
// just that ticket. The dot/key/slots are drawn in a single paint pass so they share one
// baseline; the [X] is a small owner-drawn child control (CloseButton).
internal sealed class WillLogRow : Control
{
    private static readonly Font KeyFont = new("Segoe UI", 9F, FontStyle.Bold);
    private static readonly Font TextFont = new("Segoe UI", 9F);

    private const TextFormatFlags LeftFlags =
        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

    private const int RemoveSize = 18; // the [X] button
    private const int RemovePad = 2;   // gap from the right edge

    private readonly CloseButton _remove = new();

    internal Color DotColor = Color.FromArgb(150, 150, 156);
    internal string Key = "";
    internal Color KeyColor = Color.Gray;
    internal string Slots = "";

    // Raised when the row's [X] is clicked (removes this ticket from the queue).
    internal event Action? RemoveClicked;

    internal WillLogRow()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 22;
        Margin = new Padding(0);

        _remove.Size = new Size(RemoveSize, RemoveSize);
        _remove.Click += (_, _) => RemoveClicked?.Invoke();
        Controls.Add(_remove);
    }

    // Name the [X] for screen readers (e.g. "Remove MDP-1234 on 2026-07-24").
    internal void SetRemoveAccessibleName(string name) => _remove.AccessibleName = name;

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        _remove.Location = new Point(Width - RemoveSize - RemovePad, (Height - RemoveSize) / 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.InputBg);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var dot = new SolidBrush(DotColor))
            g.FillEllipse(dot, 4, (Height - 8) / 2f, 8, 8);

        TextRenderer.DrawText(g, Key, KeyFont, new Rectangle(18, 0, Width - 18, Height), KeyColor, LeftFlags);

        // Slots sit right after the key; leave room on the right for the [X] button.
        var keyW = TextRenderer.MeasureText(Key, KeyFont).Width;
        var slotsX = 18 + keyW + 12;
        var slotsW = Math.Max(10, Width - slotsX - RemoveSize - RemovePad - 6);
        TextRenderer.DrawText(g, Slots, TextFont, new Rectangle(slotsX, 0, slotsW, Height),
            Theme.TextSecondary, LeftFlags);
    }
}
