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

        Assert.Equal(new Rectangle(0, 0, 820, 900), tasks);
        Assert.Equal(new Rectangle(820, 0, 580, 900), log);
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
    public void LogCardBounds_ReproducesBuiltLayout()
    {
        var (newEntry, queue, week) = ShellLayout.LogCardBounds(600, 608, 20, 142, 162, 246);

        Assert.Equal(new Rectangle(20, 14, 560, 142), newEntry);
        Assert.Equal(new Rectangle(20, 168, 560, 162), queue);
        Assert.Equal(new Rectangle(20, 342, 560, 246), week);
    }

    [Fact]
    public void LogCardBounds_QueueTakesTheExtraHeight()
    {
        var (newEntry, queue, week) = ShellLayout.LogCardBounds(580, 900, 0, 142, 162, 246);

        Assert.Equal(new Rectangle(0, 14, 560, 142), newEntry);
        Assert.Equal(new Rectangle(0, 168, 560, 454), queue);
        Assert.Equal(new Rectangle(0, 634, 560, 246), week);
        Assert.Equal(900 - 20, week.Bottom);
    }

    [Fact]
    public void LogCardBounds_ShortColumn_KeepsBuiltHeights()
    {
        var (_, queue, week) = ShellLayout.LogCardBounds(580, 400, 0, 142, 162, 246);

        Assert.Equal(162, queue.Height);
        Assert.Equal(new Rectangle(0, 342, 560, 246), week);
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
