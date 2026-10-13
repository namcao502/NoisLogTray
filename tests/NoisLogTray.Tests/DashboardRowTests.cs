using System.Drawing;
using NoisLogTray;

namespace NoisLogTray.Tests;

// Shares RowColumns (static, UI thread only) with TaskRowTests, so the two must not run in parallel.
[Collection("TicketRowControls")]
public class DashboardRowTests
{
    [Fact]
    public void Buttons_KeepTheirTint()
    {
        using var row = new DashboardRow("MDP-1", "https://x", "S", new[] { ("Due -", Color.Gray) }, "",
            new (string, Color?, bool, Action, string?)[] { ("Queue", null, true, () => { }, null), ("Open", Theme.Accent, true, () => { }, null) }, 700);

        Assert.Equal(new Color?[] { null, Theme.Accent }, row.Controls.OfType<MacButton>().Select(mb => mb.Tint));
    }

    [Fact]
    public void Row_Striped_FillsItsBackground_AndItsButtonsBlendIntoIt()
    {
        using var row = new DashboardRow("MDP-1", "https://x", "S", new[] { ("Due -", Color.Gray) }, "",
            new (string, Color?, bool, Action, string?)[] { ("Open", Theme.Accent, true, () => { }, null) }, 700, striped: true);

        Assert.Equal(Theme.RowStripe(Theme.CardSurface), row.BackColor);
        Assert.Equal(row.BackColor, row.Controls.OfType<MacButton>().Single().Surface!());
    }

    [Fact]
    public void Row_PutsThePrsOnTheirOwnLine()
    {
        using var row = new DashboardRow("MDP-1", "https://x", "S", new[] { ("Due -", Color.Gray) }, "BE main ✓",
            new (string, Color?, bool, Action, string?)[] { ("Open", Theme.Accent, true, () => { }, null) }, 700);
        var facts = row.Controls.OfType<FlowLayoutPanel>().Single(flp => flp.Name == TaskRow.FactsName);

        Assert.True(row.Controls.OfType<FlowLayoutPanel>().Single(flp => flp.Name == TaskRow.PrTagsName).Top >= facts.Bottom);
    }

    [Fact]
    public void Row_CentersTheButtonsOnItsHeight_RightOfTheLines()
    {
        using var row = new DashboardRow("MDP-1", "https://x", "S", new[] { ("Due -", Color.Gray) }, "BE main ✓",
            new (string, Color?, bool, Action, string?)[] { ("Queue", null, true, () => { }, null), ("Open", Theme.Accent, true, () => { }, null) }, 700);
        var lines = row.Controls.OfType<FlowLayoutPanel>().ToList();

        Assert.All(row.Controls.OfType<MacButton>(), mb =>
        {
            Assert.InRange(mb.Top + mb.Height / 2, row.Height / 2 - 1, row.Height / 2 + 1);
            Assert.All(lines, flp => Assert.True(mb.Left >= flp.Right));
        });
    }
}
