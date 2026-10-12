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
            new (string, Color?, bool, Action)[] { ("Queue", null, true, () => { }), ("Open", Theme.Accent, true, () => { }) }, 700);

        Assert.Equal(new Color?[] { null, Theme.Accent }, row.Controls.OfType<MacButton>().Select(mb => mb.Tint));
    }

    [Fact]
    public void Row_PutsThePrsOnTheirOwnLine()
    {
        using var row = new DashboardRow("MDP-1", "https://x", "S", new[] { ("Due -", Color.Gray) }, "BE main ✓",
            new (string, Color?, bool, Action)[] { ("Open", Theme.Accent, true, () => { }) }, 700);
        var facts = row.Controls.OfType<FlowLayoutPanel>().Single();

        Assert.True(row.Controls.OfType<Label>().Single(l => l.Text.StartsWith("PR  ")).Top >= facts.Bottom);
    }
}
