namespace NoisLogTray;

// X and width of every shown column of the Work board's table at one row width; a dropped column is absent. The summary
// takes what the fixed columns leave, and a row too narrow for minSummaryWidth drops ETA, then P, Logged, MAIN and DEV
// (DEV last: its merge is what Ready to Test waits for).
internal sealed record TaskTableLayout(IReadOnlyDictionary<TaskTableColumn, (int X, int Width)> Columns)
{
    private static readonly TaskTableColumn[] DropOrder =
        { TaskTableColumn.Eta, TaskTableColumn.Priority, TaskTableColumn.Logged, TaskTableColumn.PrMain, TaskTableColumn.PrDev };

    internal static TaskTableLayout For(int rowWidth, IReadOnlyDictionary<TaskTableColumn, int> fixedWidths, int gap, int pad,
        int minSummaryWidth)
    {
        var shownColumns = Enum.GetValues<TaskTableColumn>()
            .Where(ttc => ttc == TaskTableColumn.Summary || fixedWidths.ContainsKey(ttc))
            .ToList();
        var summaryWidth = rowWidth - 2 * pad - FixedWidth(shownColumns, fixedWidths, gap);
        foreach (var droppedColumn in DropOrder)
        {
            if (summaryWidth >= minSummaryWidth) break;
            shownColumns.Remove(droppedColumn);
            summaryWidth = rowWidth - 2 * pad - FixedWidth(shownColumns, fixedWidths, gap);
        }

        var columns = new Dictionary<TaskTableColumn, (int X, int Width)>();
        var columnX = pad;
        foreach (var column in shownColumns)
        {
            var width = column == TaskTableColumn.Summary ? Math.Max(0, summaryWidth) : fixedWidths[column];
            columns[column] = (columnX, width);
            columnX += width + gap;
        }
        return new TaskTableLayout(columns);
    }

    // The fixed columns' widths plus a gap between every two shown columns.
    private static int FixedWidth(List<TaskTableColumn> shownColumns, IReadOnlyDictionary<TaskTableColumn, int> fixedWidths, int gap) =>
        shownColumns.Where(ttc => ttc != TaskTableColumn.Summary).Sum(ttc => fixedWidths[ttc]) + gap * (shownColumns.Count - 1);
}
