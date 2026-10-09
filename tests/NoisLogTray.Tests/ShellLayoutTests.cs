using System.Drawing;
using NoisLogTray;

namespace NoisLogTray.Tests;

public class ShellLayoutTests
{
    [Fact]
    public void LogColumns_SwitchesAtTwoColumnMinWidth()
    {
        Assert.Equal(1, ShellLayout.LogColumns(1179));
        Assert.Equal(2, ShellLayout.LogColumns(1180));
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
    public void LogCardBounds_OneColumn_StacksCards()
    {
        var (newEntry, queue, myTickets) = ShellLayout.LogCardBounds(1100, 900, 142, 162, 164);

        Assert.Equal(new Rectangle(20, 14, 1060, 142), newEntry);
        Assert.Equal(new Rectangle(20, 14 + 142 + 12, 1060, 162), queue);
        Assert.Equal(new Rectangle(20, queue.Bottom + 12, 1060, 900 - 20 - (queue.Bottom + 12)), myTickets);
    }

    [Fact]
    public void LogCardBounds_OneColumn_ReproducesBuiltLayout()
    {
        var (newEntry, queue, myTickets) = ShellLayout.LogCardBounds(600, 626, 142, 162, 164);

        Assert.Equal(new Rectangle(20, 14, 560, 142), newEntry);
        Assert.Equal(new Rectangle(20, 168, 560, 162), queue);
        Assert.Equal(new Rectangle(20, 342, 560, 264), myTickets);
    }

    [Fact]
    public void LogCardBounds_TwoColumns_MyTicketsFillsRightColumn()
    {
        var (newEntry, queue, myTickets) = ShellLayout.LogCardBounds(1400, 900, 142, 162, 164);
        var colW = (1400 - 60) / 2;

        Assert.Equal(new Rectangle(20, 14, colW, 142), newEntry);
        Assert.Equal(new Rectangle(20, 14 + 142 + 12, colW, 900 - 20 - (14 + 142 + 12)), queue);
        Assert.Equal(new Rectangle(40 + colW, 14, colW, 900 - 34), myTickets);
    }

    [Fact]
    public void DashboardCardBounds_OneColumn_StacksWithMinHeight()
    {
        // At the page's minimum height every card below Today sits at the 96px floor.
        var (today, inProgress, attention, newTickets) = ShellLayout.DashboardCardBounds(600, 526);

        Assert.Equal(new Rectangle(20, 14, 560, ShellLayout.DashboardTodayHeight), today);
        Assert.Equal(new Rectangle(20, 194, 560, 96), inProgress);
        Assert.Equal(new Rectangle(20, 302, 560, 96), attention);
        Assert.Equal(new Rectangle(20, 410, 560, 96), newTickets);

        var (_, shortInProgress, shortAttention, shortNew) = ShellLayout.DashboardCardBounds(600, 400);
        Assert.Equal(96, shortInProgress.Height);
        Assert.Equal(96, shortAttention.Height);
        Assert.Equal(96, shortNew.Height);

        // Taller: the three cards share the rest evenly.
        var (_, tallInProgress, tallAttention, tallNew) = ShellLayout.DashboardCardBounds(600, 826);
        Assert.Equal(196, tallInProgress.Height);
        Assert.Equal(tallInProgress.Bottom + 12, tallAttention.Y);
        Assert.Equal(196, tallAttention.Height);
        Assert.Equal(tallAttention.Bottom + 12, tallNew.Y);
        Assert.Equal(826 - 20, tallNew.Bottom);
    }

    [Fact]
    public void DashboardCardBounds_TwoColumns_SplitsRightColumn()
    {
        var (today, inProgress, attention, newTickets) = ShellLayout.DashboardCardBounds(1400, 900);
        var colW = (1400 - 60) / 2;

        Assert.Equal(new Rectangle(20, 14, colW, 168), today);
        Assert.Equal(new Rectangle(20, 194, colW, 900 - 20 - 194), inProgress);
        var rightH = (900 - 14 - 20 - 12) / 2;
        Assert.Equal(new Rectangle(40 + colW, 14, colW, rightH), attention);
        Assert.Equal(new Rectangle(40 + colW, 14 + rightH + 12, colW, 900 - 20 - (14 + rightH + 12)), newTickets);
    }

    [Fact]
    public void LogCardBounds_OneColumn_MyTicketsKeepsMinHeight()
    {
        var (_, _, myTickets) = ShellLayout.LogCardBounds(600, 400, 142, 162, 164);

        Assert.Equal(164, myTickets.Height);
    }

    [Fact]
    public void UseTaskBoard_SwitchesAtMinWidth()
    {
        Assert.False(ShellLayout.UseTaskBoard(1223));
        Assert.True(ShellLayout.UseTaskBoard(1224));
    }

    [Fact]
    public void TaskBoardColumnBounds_EmptyColumnShrinks()
    {
        // 1600 wide, New empty: New 180, then (1600 - 24 - 180) / 2 = 698 each.
        var (newColumn, inProgress, done) = ShellLayout.TaskBoardColumnBounds(1600, 700, newEmpty: true);

        Assert.Equal(new Rectangle(0, 0, 180, 700), newColumn);
        Assert.Equal(new Rectangle(192, 0, 698, 700), inProgress);
        Assert.Equal(new Rectangle(902, 0, 698, 700), done);
    }

    [Fact]
    public void TaskBoardColumnBounds_KeepsMinWidthAndOverflows()
    {
        // 1224 wide, only Done empty: two filled columns would be 510 each, above the 400 minimum.
        var (_, inProgress, done) = ShellLayout.TaskBoardColumnBounds(1224, 700, doneEmpty: true);
        Assert.Equal(510, inProgress.Width);
        Assert.Equal(1224, done.Right);

        // 900 wide with nothing empty: each keeps 400, so the board runs past the width.
        var (_, _, wideDone) = ShellLayout.TaskBoardColumnBounds(900, 700);
        Assert.Equal(400, wideDone.Width);
        Assert.True(wideDone.Right > 900);
    }

    [Fact]
    public void TaskBoardColumnBounds_ThreeEqualColumns()
    {
        var (newColumn, inProgress, done) = ShellLayout.TaskBoardColumnBounds(1300, 700);

        Assert.Equal(new Rectangle(0, 0, 425, 700), newColumn);
        Assert.Equal(new Rectangle(437, 0, 425, 700), inProgress);
        Assert.Equal(new Rectangle(874, 0, 426, 700), done);
    }
}
