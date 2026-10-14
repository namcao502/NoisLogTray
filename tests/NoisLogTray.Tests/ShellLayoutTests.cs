using System.Drawing;
using NoisLogTray;

namespace NoisLogTray.Tests;

public class ShellLayoutTests
{
    [Fact]
    public void DashboardColumns_SwitchesAtTwoColumnMinWidth()
    {
        Assert.Equal(1, ShellLayout.DashboardColumns(1179));
        Assert.Equal(2, ShellLayout.DashboardColumns(1180));
    }

    [Fact]
    public void WorkSideBySide_NeedsTheBoardAndTheLogColumn()
    {
        Assert.Equal(TasksPage.DesignWidth + LogPage.CardW + 20, ShellLayout.WorkSideBySideMinWidth);
        Assert.False(ShellLayout.WorkSideBySide(ShellLayout.WorkSideBySideMinWidth - 1));
        Assert.True(ShellLayout.WorkSideBySide(ShellLayout.WorkSideBySideMinWidth));
    }

    [Fact]
    public void WorkBounds_SideBySide_LogColumnOnTheRight()
    {
        var (tasks, log) = ShellLayout.WorkBounds(1400, 900, 602, 640);

        Assert.Equal(480, ShellLayout.LogColumnWidth);
        Assert.Equal(new Rectangle(0, 0, 920, 900), tasks);
        Assert.Equal(new Rectangle(920, 0, 480, 900), log);
    }

    [Fact]
    public void WorkBounds_Narrow_StacksLogFirst()
    {
        var (tasks, log) = ShellLayout.WorkBounds(1000, 900, 602, 640);

        Assert.Equal(new Rectangle(0, 0, 1000, 602), log);
        Assert.Equal(new Rectangle(0, 602, 1000, 640), tasks); // at least its minimum height; the page scrolls

        var (tallTasks, _) = ShellLayout.WorkBounds(1000, 1400, 602, 640);
        Assert.Equal(new Rectangle(0, 602, 1000, 798), tallTasks);
    }

    [Fact]
    public void StartMaximized_DefaultsTrue_WhenNeverSaved()
    {
        Assert.True(ShellLayout.StartMaximized(new AppSettings()));
    }

    [Fact]
    public void StartMaximized_FollowsSavedValue()
    {
        Assert.False(ShellLayout.StartMaximized(new AppSettings { WindowMaximized = false }));
        Assert.True(ShellLayout.StartMaximized(new AppSettings { WindowMaximized = true }));
    }

    [Fact]
    public void MaximizedRect_IsRelativeToMonitor()
    {
        // Secondary monitor with a 40px taskbar on its left edge.
        var rect = ShellLayout.MaximizedRect(new Rectangle(1920, 0, 1920, 1080), new Rectangle(1960, 0, 1880, 1080));

        Assert.Equal(new Rectangle(40, 0, 1880, 1080), rect);
    }

    [Fact]
    public void WeekCard_BuiltHeight_Is246()
    {
        Assert.Equal(246, WeekCard.BuiltHeight);
    }

    [Theory]
    [InlineData(1180, 560)]
    [InlineData(1330, 680)]
    [InlineData(1840, 890)]
    public void DashboardBounds_TwoColumns_TodoLeftStaysAtLeast560(int width, int rightWidth)
    {
        var (_, _, todo, inProgress, newTickets) = ShellLayout.DashboardBounds(width, 900);

        Assert.Equal(rightWidth, inProgress.Width);
        Assert.Equal(width - 60 - rightWidth, todo.Width);
        Assert.True(todo.Width >= 560);
        Assert.Equal(new Rectangle(20, 144, todo.Width, 900 - 144 - 20), todo);
        Assert.Equal(new Rectangle(40 + todo.Width, 144, rightWidth, (900 - 144 - 20 - 12) / 2), inProgress);
        Assert.Equal(inProgress.Bottom + 12, newTickets.Y);
        Assert.Equal((inProgress.X, inProgress.Width, 900 - 20), (newTickets.X, newTickets.Width, newTickets.Bottom));
    }

    [Fact]
    public void DashboardBounds_TwoColumns_HeaderThenFourTilesAcrossTheWidth()
    {
        var (header, tiles, _, _, _) = ShellLayout.DashboardBounds(1330, 900);

        Assert.Equal(new Rectangle(20, 14, 1290, 30), header);
        Assert.Equal(4, tiles.Count);
        Assert.All(tiles, tile => Assert.Equal((56, 76), (tile.Y, tile.Height)));
        Assert.Equal(20, tiles[0].X);
        for (var tileIndex = 1; tileIndex < tiles.Count; tileIndex++)
            Assert.Equal(tiles[tileIndex - 1].Right + 12, tiles[tileIndex].X);
        Assert.Equal(1330 - 20, tiles[^1].Right);
    }

    [Fact]
    public void DashboardBounds_OneColumn_TilesTwoByTwo_ThenThreeCardsAtTheirMinimum()
    {
        var (_, tiles, todo, inProgress, newTickets) = ShellLayout.DashboardBounds(600, ShellLayout.DashboardMinHeight);

        Assert.Equal(648, ShellLayout.DashboardMinHeight);
        Assert.Equal(new[] { new Rectangle(20, 56, 274, 76), new Rectangle(306, 56, 274, 76), new Rectangle(20, 144, 274, 76), new Rectangle(306, 144, 274, 76) }, tiles);
        Assert.Equal(new Rectangle(20, 232, 560, 124), todo);
        Assert.Equal(new Rectangle(20, 368, 560, 124), inProgress);
        Assert.Equal(new Rectangle(20, 504, 560, 124), newTickets);
        Assert.Equal(ShellLayout.DashboardMinHeight - 20, newTickets.Bottom);

        var (_, _, tallTodo, tallInProgress, tallNew) = ShellLayout.DashboardBounds(600, 900);
        Assert.Equal(tallTodo.Height, tallInProgress.Height);
        Assert.Equal(900 - 20, tallNew.Bottom);
    }

    [Fact]
    public void LogCardBounds_ReproducesBuiltLayout()
    {
        var (newEntry, queue, week) = ShellLayout.LogCardBounds(LogPage.CardW + 40, 608, 20, 142, 162, 246);

        Assert.Equal(new Rectangle(20, 14, LogPage.CardW, 142), newEntry);
        Assert.Equal(new Rectangle(20, 168, LogPage.CardW, 162), queue);
        Assert.Equal(new Rectangle(20, 342, LogPage.CardW, 246), week);
    }

    [Fact]
    public void LogCardBounds_QueueTakesTheExtraHeight()
    {
        var (newEntry, queue, week) = ShellLayout.LogCardBounds(ShellLayout.LogColumnWidth, 900, 0, 142, 162, 246);

        Assert.Equal(new Rectangle(0, 14, LogPage.CardW, 142), newEntry);
        Assert.Equal(new Rectangle(0, 168, LogPage.CardW, 454), queue);
        Assert.Equal(new Rectangle(0, 634, LogPage.CardW, 246), week);
        Assert.Equal(900 - 20, week.Bottom);
    }

    [Fact]
    public void LogCardBounds_ShortColumn_KeepsBuiltHeights()
    {
        var (_, queue, week) = ShellLayout.LogCardBounds(ShellLayout.LogColumnWidth, 400, 0, 142, 162, 246);

        Assert.Equal(162, queue.Height);
        Assert.Equal(new Rectangle(0, 342, LogPage.CardW, 246), week);
    }

    [Fact]
    public void LogNaturalHeight_FitsTheBuiltCards()
    {
        var natural = ShellLayout.LogNaturalHeight(142, 162, 246);

        Assert.Equal(14 + 142 + 12 + 162 + 12 + 246 + 14, natural);
        var (_, queue, _) = ShellLayout.LogCardBounds(1000, natural, 20, 142, 162, 246);
        Assert.Equal(162, queue.Height);
    }
}
