using System.Drawing;

namespace NoisLogTray;

// One ticket on the Dashboard: a TicketTableRow with the card's own columns and up to two buttons; the page decides the
// cells, texts and actions.
internal sealed class DashboardRow : TicketTableRow
{
    private const int MaxButtons = 2;

    // buttons are listed right-to-left: the first one sits at the right edge.
    internal DashboardRow(string ticketKey, string linkUrl, string summary, IReadOnlyList<TaskTableColumn> columns,
        IReadOnlyList<TableCell> cells, IReadOnlyList<(string Text, Color? Tint, bool Enabled, Action OnClick, string? Glyph)> buttons,
        int width, bool striped = false)
        : base(ticketKey, linkUrl, summary, columns, width, striped)
    {
        if (buttons.Count > MaxButtons)
            throw new ArgumentException($"A dashboard row holds at most {MaxButtons} buttons.", nameof(buttons));

        var surface = Surface;
        var builtButtons = new List<Control>();
        foreach (var (text, tint, enabled, onClick, glyph) in buttons)
        {
            var button = tint == null ? MacButton.Secondary(text) : MacButton.Tinted(text, tint.Value);
            button.Glyph = glyph;
            button.Surface = () => surface;
            // Fixed widths: a label change (Queue -> Queued) must not shift the row.
            // Ready to Test is the one label wider than the shared action width.
            var buttonWidth = text == ButtonWidths.ReadyToTestText ? ButtonWidths.ReadyToTestWidth(button.Font) : ButtonWidths.ActionWidth(button.Font);
            button.Size = new Size(buttonWidth, ButtonH);
            button.Enabled = enabled;
            button.AccessibleName = $"{text} {ticketKey}";
            button.Click += (_, _) => onClick();
            builtButtons.Add(button);
        }
        SetCells(cells, null);
        SetActions(builtButtons);
    }
}
