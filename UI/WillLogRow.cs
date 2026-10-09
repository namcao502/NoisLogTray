using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NoisLogTray;

// One line of the "Will log" preview (queued view): a status dot, the colored ticket
// key, its time slots right after the key, the Jira summary, and a remove [X] on the far
// right that drops just that ticket. The dot/key/slots are drawn in a single paint pass so they share one
// baseline; the [X] is a small owner-drawn child control (CloseButton).
internal sealed class WillLogRow : Control
{
    private static readonly Font KeyFont = UiScale.Font("Segoe UI", 9F, FontStyle.Bold);
    private static readonly Font TextFont = UiScale.Font("Segoe UI", 9F);

    private const TextFormatFlags LeftFlags =
        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

    private static readonly int RemoveSize = UiScale.S(18); // the [X] button
    private static readonly int RemovePad = UiScale.S(2);   // gap from the right edge
    private static readonly int TextX = UiScale.S(18);      // key text starts right of the dot
    private static readonly int TextGap = UiScale.S(12);    // between key, slots and summary

    private readonly CloseButton _remove = new();
    private readonly SummaryTip _summaryTip = new();

    internal Color DotColor = Color.FromArgb(150, 150, 156);
    internal string Key = "";
    internal Color KeyColor = Color.Gray;
    internal string Slots = "";
    internal string Summary = ""; // Jira title; empty until the ticket is verified

    private static readonly int MinSummaryW = UiScale.S(40); // below this a summary is only "..."; skip it

    // Raised when the row's [X] is clicked (removes this ticket from the queue).
    internal event Action? RemoveClicked;

    internal WillLogRow()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = UiScale.S(22);
        Margin = new Padding(0);

        _remove.Size = new Size(RemoveSize, RemoveSize);
        _remove.Click += (_, _) => RemoveClicked?.Invoke();
        Controls.Add(_remove);
    }

    // Name the [X] for screen readers (e.g. "Remove MDP-1234 on 2026-07-24").
    internal void SetRemoveAccessibleName(string name) => _remove.AccessibleName = name;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _summaryTip.Dispose();
        base.Dispose(disposing);
    }

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

        var dotSize = UiScale.S(8f);
        using (var dot = new SolidBrush(DotColor))
            g.FillEllipse(dot, UiScale.S(4f), (Height - dotSize) / 2f, dotSize, dotSize);

        TextRenderer.DrawText(g, Key, KeyFont, new Rectangle(TextX, 0, Width - TextX, Height), KeyColor, LeftFlags);

        // Slots sit right after the key; leave room on the right for the [X] button.
        var keyW = Math.Max(RowColumns.KeyWidth(KeyFont), TextRenderer.MeasureText(Key, KeyFont).Width);
        var slotsX = TextX + keyW + TextGap;
        var slotsW = Math.Max(UiScale.S(10), Width - slotsX - RemoveSize - RemovePad - UiScale.S(6));
        TextRenderer.DrawText(g, Slots, TextFont, new Rectangle(slotsX, 0, slotsW, Height),
            Theme.TextSecondary, LeftFlags);

        // The Jira summary fills what is left before the [X], cut with "..." when long.
        var summaryX = slotsX + Math.Max(RowColumns.SlotsWidth(TextFont), TextRenderer.MeasureText(Slots, TextFont).Width) + TextGap;
        var summaryW = Width - summaryX - RemoveSize - RemovePad - UiScale.S(6);
        if (Summary.Length != 0 && summaryW > MinSummaryW)
            TextRenderer.DrawText(g, Summary, TextFont, new Rectangle(summaryX, 0, summaryW, Height),
                Theme.TextPrimary, LeftFlags | TextFormatFlags.EndEllipsis);
        var isSummaryCut = Summary.Length != 0
            && (summaryW <= MinSummaryW || TextRenderer.MeasureText(Summary, TextFont).Width > summaryW);
        _summaryTip.Update(this, Summary, isSummaryCut);
    }
}
