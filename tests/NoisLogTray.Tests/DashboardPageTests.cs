using System.Drawing;
using NoisLogTray;

namespace NoisLogTray.Tests;

// The page lays out its tables, which write RowColumns (static, UI thread only): no parallel row tests.
[Collection("TicketRowControls")]
public class DashboardPageTests
{
    [Fact]
    public void Page_PutsTheFourTilesInOneRow_WhenWide_AndTwoByTwo_WhenNarrow()
    {
        using var page = new DashboardPage(null, null, "https://x", () => "C:\\missing", () => Array.Empty<string>());

        page.Size = UiScale.S(new Size(1330, 900));
        var tiles = page.Controls.OfType<StatTile>().ToList();
        Assert.Equal(4, tiles.Count);
        Assert.All(tiles, tile => Assert.Equal(tiles[0].Top, tile.Top));
        Assert.Equal(page.ClientSize.Width - UiScale.S(20), tiles[^1].Right, tolerance: 2);

        page.Size = UiScale.S(new Size(600, ShellLayout.DashboardMinHeight));
        Assert.Equal(2, tiles.Select(tile => tile.Top).Distinct().Count());
    }

    // Language-neutral asserts: other tests flip Lang in parallel.
    [Fact]
    public void CheckedText_NamesTheConfiguredInterval_AndTheLastCheck()
    {
        var checkedAt = new DateTime(2026, 10, 14, 10, 42, 0);

        var lastCheck = DashboardPage.CheckedText(15, checking: false, checkedAt, isTasksOn: true);

        Assert.Contains("15", lastCheck);
        Assert.Contains("10:42", lastCheck);
        Assert.Contains("30", DashboardPage.CheckedText(30, checking: false, null, isTasksOn: true));
        Assert.DoesNotContain("15", DashboardPage.CheckedText(15, checking: true, checkedAt, isTasksOn: true));
        Assert.Equal(string.Empty, DashboardPage.CheckedText(15, checking: false, checkedAt, isTasksOn: false));
    }
}
