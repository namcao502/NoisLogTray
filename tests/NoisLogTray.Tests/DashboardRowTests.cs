using System.Drawing;
using NoisLogTray;

namespace NoisLogTray.Tests;

// Shares RowColumns (static, UI thread only) with TaskRowTests, so the two must not run in parallel.
[Collection("TicketRowControls")]
public class DashboardRowTests
{
    private static readonly TaskTableColumn[] Columns =
        { TaskTableColumn.Key, TaskTableColumn.Summary, TaskTableColumn.Status, TaskTableColumn.Due };

    private static readonly TableCell[] Cells =
        { new(TaskTableColumn.Status, "In Progress", Theme.ActionReadyToTest), new(TaskTableColumn.Due, "Oct 1", TaskRow.Red) };

    private static DashboardRow NewRow(IReadOnlyList<(string, Color?, bool, Action, string?)> buttons, bool striped = false) =>
        new("MDP-1", "https://x", "S", Columns, Cells, buttons, 700, striped);

    private static List<MacButton> MacButtons(DashboardRow row) =>
        row.Controls[TicketTableRow.ActionsName]!.Controls.OfType<MacButton>().ToList();

    [Fact]
    public void Buttons_KeepTheirTint_InsideTheHiddenActions()
    {
        using var row = NewRow(new (string, Color?, bool, Action, string?)[] { ("Queue", null, true, () => { }, null), ("Open", Theme.Accent, true, () => { }, null) });

        Assert.False(row.ActionsVisible);
        // Left to right, the order Tab walks them: Open, then Queue at the right edge.
        Assert.Equal(new Color?[] { Theme.Accent, null }, MacButtons(row).Select(mb => mb.Tint));
    }

    [Fact]
    public void Buttons_KeepTheirOwnEnabledState()
    {
        using var row = NewRow(new (string, Color?, bool, Action, string?)[] { ("Queued", null, false, () => { }, null), ("Open", Theme.Accent, true, () => { }, null) });

        Assert.Equal(new[] { true, false }, MacButtons(row).Select(mb => mb.Enabled));
    }

    [Fact]
    public void Row_Striped_FillsItsBackground_AndItsButtonsBlendIntoIt()
    {
        using var row = NewRow(new (string, Color?, bool, Action, string?)[] { ("Open", Theme.Accent, true, () => { }, null) }, striped: true);

        Assert.Equal(Theme.RowStripe(Theme.CardSurface), row.BackColor);
        Assert.Equal(row.BackColor, MacButtons(row).Single().Surface!());
    }

    [Fact]
    public void Row_IsOneLineHigh_AndDrawsOnlyItsColumns()
    {
        using var temp = new TempDirectory();
        using var row = NewRow(Array.Empty<(string, Color?, bool, Action, string?)>());
        using var boardRow = new TaskRow(new JiraTicket("MDP-2", "S", null, null), TicketSection.New, false, "https://x",
            new TicketBoard(new TicketStore(temp.Path)), (_, _) => { }, _ => { }, _ => { }, false, _ => { }, null, 700);

        Assert.Equal(boardRow.Height, row.Height);
        Assert.DoesNotContain(TaskTableColumn.Eta, TicketTableRow.LayoutFor(700, Columns).Columns.Keys);
        Assert.Contains("In Progress", row.Controls.OfType<LinkLabel>().Single().AccessibleDescription);
    }

    [Fact]
    public void Row_ThrowsOnMoreThanTwoButtons()
    {
        var buttons = Enumerable.Range(0, 3).Select(index => ($"B{index}", (Color?)null, true, (Action)(() => { }), (string?)null)).ToList();

        Assert.Throws<ArgumentException>(() => NewRow(buttons));
    }
}
