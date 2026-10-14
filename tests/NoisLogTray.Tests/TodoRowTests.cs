using System.Drawing;
using NoisLogTray;

namespace NoisLogTray.Tests;

public class TodoRowTests
{
    private static TodoRow NewRow(IReadOnlyList<(string, Color?, bool, Action, string?)> buttons, string? ticketKey = "MDP-1",
        bool striped = false) =>
        new(ticketKey, ticketKey == null ? null : "https://x/browse/" + ticketKey, Icons.Clock, TaskRow.Red, "overdue since Oct 1",
            "Fix the export", buttons, 600, striped);

    private static List<MacButton> MacButtons(TodoRow row) => row.Controls.OfType<MacButton>().ToList();

    [Fact]
    public void Buttons_SitAtTheRightEdge_LeftToRight_WithTheirTintAndState()
    {
        using var row = NewRow(new (string, Color?, bool, Action, string?)[]
        {
            ("Seen", null, false, () => { }, null),
            ("View PR", Theme.Accent, true, () => { }, null),
        });

        var buttons = MacButtons(row);
        // Listed right-to-left; added leftmost first, so Tab walks them left to right.
        Assert.Equal(new[] { "View PR", "Seen" }, buttons.Select(mb => mb.Text));
        Assert.Equal(new Color?[] { Theme.Accent, null }, buttons.Select(mb => mb.Tint));
        Assert.Equal(new[] { true, false }, buttons.Select(mb => mb.Enabled));
        Assert.True(buttons[0].Right < buttons[1].Left);
        Assert.True(buttons[1].Right <= row.Width && buttons[1].Right > row.Width - UiScale.S(20));
    }

    [Fact]
    public void Row_ThrowsOnMoreThanTwoButtons()
    {
        var buttons = Enumerable.Range(0, 3).Select(index => ($"B{index}", (Color?)null, true, (Action)(() => { }), (string?)null)).ToList();

        Assert.Throws<ArgumentException>(() => NewRow(buttons));
    }

    [Fact]
    public void Row_Striped_FillsItsBackground_AndItsButtonsBlendIntoIt()
    {
        using var row = NewRow(new (string, Color?, bool, Action, string?)[] { ("Open", Theme.Accent, true, () => { }, null) }, striped: true);

        Assert.Equal(Theme.RowStripe(Theme.CardSurface), row.BackColor);
        Assert.Equal(row.BackColor, MacButtons(row).Single().Surface!());
    }

    [Fact]
    public void Row_LinksTheTicketKey_OnlyWhenItHasOne()
    {
        using var ticketRow = NewRow(Array.Empty<(string, Color?, bool, Action, string?)>());
        using var dayRow = NewRow(Array.Empty<(string, Color?, bool, Action, string?)>(), ticketKey: null);

        Assert.Equal("MDP-1", ticketRow.Controls.OfType<LinkLabel>().Single().Text);
        Assert.Empty(dayRow.Controls.OfType<LinkLabel>());
        Assert.Contains("overdue since Oct 1", ticketRow.AccessibleName);
        Assert.Contains("Fix the export", ticketRow.AccessibleName);
    }
}
