using System.Drawing;

namespace NoisLogTray;

// Pure layout rules for the shell window, kept out of the controls so they are unit-tested.
internal static class ShellLayout
{
    // Two 560px cards with 20px outer margins and a 20px gap.
    private const int DashboardColumnMinWidth = 560;
    internal const int TwoColumnMinWidth = 2 * (DashboardColumnMinWidth + 20) + 20;

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

    internal const int DashboardCardMinHeight = 124; // title, column titles and one row

    // The Dashboard's tables (In progress over New) at most this wide while the to-do column keeps its 560; past that the
    // two columns share the width evenly.
    private const int DashboardTablesMaxWidth = 680;
    private const int DashboardHeaderHeight = 30;
    private const int DashboardTileHeight = 76;

    // Header, two rows of tiles and three list cards at their minimum: the one-column page's height.
    internal const int DashboardMinHeight = 14 + DashboardHeaderHeight + 12 + 2 * DashboardTileHeight + 12 + 12
        + 3 * DashboardCardMinHeight + 2 * 12 + 20;

    // The header line, four tiles (one row, or two by two in one column), then the to-do card left of In progress over New,
    // or all three stacked. The last card of a column takes the rounding remainder, so it reaches the bottom margin.
    internal static (Rectangle Header, IReadOnlyList<Rectangle> Tiles, Rectangle Todo, Rectangle InProgress, Rectangle NewTickets)
        DashboardBounds(int width, int height)
    {
        const int margin = 20;
        const int top = 14;
        const int gap = 12;

        var contentW = width - 2 * margin;
        var header = new Rectangle(margin, top, contentW, DashboardHeaderHeight);
        var isTwoColumns = DashboardColumns(width) == 2;
        var tilesPerRow = isTwoColumns ? 4 : 2;
        var tileW = (contentW - (tilesPerRow - 1) * gap) / tilesPerRow;
        var tiles = new List<Rectangle>();
        for (var tileIndex = 0; tileIndex < 4; tileIndex++)
        {
            var column = tileIndex % tilesPerRow;
            var tileX = margin + column * (tileW + gap);
            // The row's last tile ends at the right margin, like the cards below.
            var tileWidth = column == tilesPerRow - 1 ? margin + contentW - tileX : tileW;
            tiles.Add(new Rectangle(tileX, header.Bottom + gap + tileIndex / tilesPerRow * (DashboardTileHeight + gap), tileWidth,
                DashboardTileHeight));
        }
        var listsY = tiles[^1].Bottom + gap;

        if (!isTwoColumns)
        {
            var cardH = Math.Max((height - listsY - margin - 2 * gap) / 3, DashboardCardMinHeight);
            var todo = new Rectangle(margin, listsY, contentW, cardH);
            var inProgress = new Rectangle(margin, todo.Bottom + gap, contentW, cardH);
            var newY = inProgress.Bottom + gap;
            return (header, tiles, todo, inProgress, new Rectangle(margin, newY, contentW, Math.Max(height - newY - margin, DashboardCardMinHeight)));
        }

        var rightW = Math.Max(Math.Min(DashboardTablesMaxWidth, width - 3 * margin - DashboardColumnMinWidth), (width - 3 * margin) / 2);
        var leftW = width - 3 * margin - rightW;
        var rightX = 2 * margin + leftW;
        var rightH = Math.Max((height - listsY - margin - gap) / 2, DashboardCardMinHeight);
        var inProgressCard = new Rectangle(rightX, listsY, rightW, rightH);
        var newTicketsY = inProgressCard.Bottom + gap;
        return (header, tiles,
            new Rectangle(margin, listsY, leftW, Math.Max(height - listsY - margin, DashboardCardMinHeight)),
            inProgressCard,
            new Rectangle(rightX, newTicketsY, rightW, Math.Max(height - newTicketsY - margin, DashboardCardMinHeight)));
    }

    internal static bool StartMaximized(AppSettings settings) => settings.WindowMaximized ?? true;

    // WM_GETMINMAXINFO wants the maximized rect relative to the monitor's own origin.
    internal static Rectangle MaximizedRect(Rectangle monitorBounds, Rectangle workArea) =>
        new(workArea.X - monitorBounds.X, workArea.Y - monitorBounds.Y, workArea.Width, workArea.Height);
}
