using System.Drawing;

namespace NoisLogTray;

// Pure layout rules for the shell window, kept out of the controls so they are unit-tested.
internal static class ShellLayout
{
    // Two 560px cards (LogPage CardW) with 20px outer margins and a 20px gap.
    internal const int TwoColumnMinWidth = 2 * (LogPage.CardW + 20) + 20;

    internal static int DashboardColumns(int pageWidth) => pageWidth >= TwoColumnMinWidth ? 2 : 1;

    // The Work page's log column: one LogPage card plus the 20px right margin its scroll bar sits in.
    internal const int LogColumnWidth = LogPage.CardW + 20;

    internal const int WorkSideBySideMinWidth = TasksPage.DesignWidth + LogColumnWidth;

    internal static bool WorkSideBySide(int pageWidth) => pageWidth >= WorkSideBySideMinWidth;

    // Side by side: the board left, the log column right, both full height. Narrower: the log column first at its
    // natural height, then the board at least tasksMinHeight tall; the page scrolls.
    internal static (Rectangle Tasks, Rectangle Log) WorkBounds(int width, int height, int logNaturalHeight, int tasksMinHeight)
    {
        if (WorkSideBySide(width))
        {
            var tasksWidth = width - LogColumnWidth;
            return (new Rectangle(0, 0, tasksWidth, height), new Rectangle(tasksWidth, 0, LogColumnWidth, height));
        }
        return (new Rectangle(0, logNaturalHeight, width, Math.Max(height - logNaturalHeight, tasksMinHeight)),
            new Rectangle(0, 0, width, logNaturalHeight));
    }

    // The log column stacks New entry, Queue and the week card; the Queue takes any extra height, so the week card
    // ends at the bottom margin. Heights are the built ones; a shorter column keeps them and scrolls.
    internal static (Rectangle NewEntry, Rectangle Queue, Rectangle Week) LogCardBounds(
        int width, int height, int left, int newEntryH, int queueH, int weekH)
    {
        const int right = 20;
        const int bottom = 20;

        var cardW = width - left - right;
        var newEntry = new Rectangle(left, LogTop, cardW, newEntryH);
        var queueY = newEntry.Bottom + LogGap;
        var queue = new Rectangle(left, queueY, cardW, Math.Max(queueH, height - bottom - weekH - LogGap - queueY));
        return (newEntry, queue, new Rectangle(left, queue.Bottom + LogGap, cardW, weekH));
    }

    // The column's built height, its bottom margin included as the gap above the board when stacked.
    internal static int LogNaturalHeight(int newEntryH, int queueH, int weekH) =>
        LogTop + newEntryH + LogGap + queueH + LogGap + weekH + LogTop;

    private const int LogTop = 14;
    private const int LogGap = 12;

    internal const int DashboardTodayHeight = 168;   // header 44 + 3 lines x 28 + 40 padding
    internal const int DashboardCardMinHeight = 96;

    // Four cards: 1 column stacks them; 2 columns put Today + In progress left, Attention + New right.
    // The last card of a column takes the rounding remainder, so the column always reaches the bottom margin.
    internal static (Rectangle Today, Rectangle InProgress, Rectangle Attention, Rectangle NewTickets) DashboardCardBounds(
        int width, int height)
    {
        const int margin = 20;
        const int top = 14;
        const int gap = 12;

        if (DashboardColumns(width) == 1)
        {
            var colW = width - 2 * margin;
            var today = new Rectangle(margin, top, colW, DashboardTodayHeight);
            var inProgressY = today.Bottom + gap;
            var cardH = Math.Max((height - inProgressY - margin - 2 * gap) / 3, DashboardCardMinHeight);
            var inProgress = new Rectangle(margin, inProgressY, colW, cardH);
            var attention = new Rectangle(margin, inProgress.Bottom + gap, colW, cardH);
            var newY = attention.Bottom + gap;
            return (today, inProgress, attention,
                new Rectangle(margin, newY, colW, Math.Max(height - newY - margin, DashboardCardMinHeight)));
        }

        var halfW = (width - 3 * margin) / 2;
        var rightX = 2 * margin + halfW;
        var leftToday = new Rectangle(margin, top, halfW, DashboardTodayHeight);
        var leftInProgressY = leftToday.Bottom + gap;
        var rightH = Math.Max((height - top - margin - gap) / 2, DashboardCardMinHeight);
        var rightAttention = new Rectangle(rightX, top, halfW, rightH);
        var rightNewY = rightAttention.Bottom + gap;
        return (leftToday,
            new Rectangle(margin, leftInProgressY, halfW, Math.Max(height - leftInProgressY - margin, DashboardCardMinHeight)),
            rightAttention,
            new Rectangle(rightX, rightNewY, halfW, Math.Max(height - rightNewY - margin, DashboardCardMinHeight)));
    }

    internal static bool StartMaximized(AppSettings settings) => settings.WindowMaximized ?? true;

    // WM_GETMINMAXINFO wants the maximized rect relative to the monitor's own origin.
    internal static Rectangle MaximizedRect(Rectangle monitorBounds, Rectangle workArea) =>
        new(workArea.X - monitorBounds.X, workArea.Y - monitorBounds.Y, workArea.Width, workArea.Height);
}
