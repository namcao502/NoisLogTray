using System.Drawing;

namespace NoisLogTray;

// Pure layout rules for the shell window, kept out of the controls so they are unit-tested.
internal static class ShellLayout
{
    // Two 560px cards (LogPage CardW) with 20px outer margins and a 20px gap.
    internal const int TwoColumnMinWidth = 2 * (LogPage.CardW + 20) + 20;

    internal static int LogColumns(int pageWidth) => pageWidth >= TwoColumnMinWidth ? 2 : 1;

    // Card bounds for the Log page; heights are the built card heights. One column stacks
    // the cards; two columns put New entry + Queue left and a full-height My tickets right (the long list
    // gets the tall column; the queue usually holds only a few tickets).
    internal static (Rectangle NewEntry, Rectangle Queue, Rectangle MyTickets) LogCardBounds(
        int width, int height, int newEntryH, int queueH, int myTicketsMinH)
    {
        const int margin = 20;
        const int top = 14;
        const int gap = 12;

        if (LogColumns(width) == 1)
        {
            var colW = width - 2 * margin;
            var newEntry = new Rectangle(margin, top, colW, newEntryH);
            var queue = new Rectangle(margin, newEntry.Bottom + gap, colW, queueH);
            var ticketsY = queue.Bottom + gap;
            return (newEntry, queue, new Rectangle(margin, ticketsY, colW, Math.Max(height - ticketsY - margin, myTicketsMinH)));
        }

        var halfW = (width - 3 * margin) / 2;
        var left = new Rectangle(margin, top, halfW, newEntryH);
        var queueY = left.Bottom + gap;
        return (left,
            new Rectangle(margin, queueY, halfW, Math.Max(height - queueY - margin, queueH)),
            new Rectangle(2 * margin + halfW, top, halfW, Math.Max(height - top - margin, myTicketsMinH)));
    }

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

        if (LogColumns(width) == 1)
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

    internal const int TaskColumnMinWidth = 400;
    internal const int TaskColumnGap = 12;
    internal const int TaskBoardMinWidth = 3 * TaskColumnMinWidth + 2 * TaskColumnGap;

    internal static bool UseTaskBoard(int listWidth) => listWidth >= TaskBoardMinWidth;

    internal const int TaskColumnCollapsedWidth = 180;

    // An empty column shrinks to TaskColumnCollapsedWidth (just its header) and the others share the rest,
    // the last non-empty one taking the rounding remainder. When the non-empty columns would drop below
    // TaskColumnMinWidth they keep that width instead and the board runs past `width` (the page scrolls).
    internal static (Rectangle New, Rectangle InProgress, Rectangle Done) TaskBoardColumnBounds(
        int width, int height, bool newEmpty = false, bool inProgressEmpty = false, bool doneEmpty = false)
    {
        var empty = new[] { newEmpty, inProgressEmpty, doneEmpty };
        // Nothing anywhere: keep three equal columns rather than three thin strips.
        if (newEmpty && inProgressEmpty && doneEmpty) empty = new[] { false, false, false };
        var filledCount = empty.Count(isEmpty => !isEmpty);
        var collapsedTotal = (3 - filledCount) * TaskColumnCollapsedWidth;
        var filledWidth = Math.Max(TaskColumnMinWidth, (width - 2 * TaskColumnGap - collapsedTotal) / filledCount);
        var lastFilled = Array.LastIndexOf(empty, false);

        var columns = new Rectangle[3];
        var x = 0;
        for (var columnIndex = 0; columnIndex < 3; columnIndex++)
        {
            var columnWidth = empty[columnIndex] ? TaskColumnCollapsedWidth : filledWidth;
            columns[columnIndex] = new Rectangle(x, 0, columnWidth, height);
            x += columnWidth + TaskColumnGap;
        }
        var overflow = width - columns[2].Right;
        if (overflow > 0)
        {
            // Rounding left a few pixels: give them to the last non-empty column and shift what follows.
            columns[lastFilled].Width += overflow;
            for (var columnIndex = lastFilled + 1; columnIndex < 3; columnIndex++) columns[columnIndex].X += overflow;
        }
        return (columns[0], columns[1], columns[2]);
    }

    internal static bool StartMaximized(AppSettings settings) => settings.WindowMaximized ?? true;

    // WM_GETMINMAXINFO wants the maximized rect relative to the monitor's own origin.
    internal static Rectangle MaximizedRect(Rectangle monitorBounds, Rectangle workArea) =>
        new(workArea.X - monitorBounds.X, workArea.Y - monitorBounds.Y, workArea.Width, workArea.Height);
}
