using System.Drawing;

namespace NoisLogTray;

// Fixed column widths, so a ticket table's cells (Work board, Dashboard) and a queue row's slots start at the same x on
// every row whatever the key, date or hours. Measured once per font.
internal static class RowColumns
{
    // Keyed by font: each row type has its own (static) font.
    private static readonly Dictionary<Font, int> KeyWidths = new();
    private static int _slotsWidth;
    private static IReadOnlyDictionary<TaskTableColumn, int>? _tableWidths; // one table, one set of fonts

    // Widest realistic key: the project prefix plus five digits. UI thread only, like every caller.
    internal static int KeyWidth(Font keyFont)
    {
        if (!KeyWidths.TryGetValue(keyFont, out var width))
        {
            width = TextRenderer.MeasureText("MDP-00000", keyFont).Width;
            KeyWidths[keyFont] = width;
        }
        return width;
    }

    // A queue row's time slots: a full day split around lunch is the common longest form.
    internal static int SlotsWidth(Font textFont)
    {
        if (_slotsWidth == 0) _slotsWidth = TextRenderer.MeasureText("08:00-12:00, 13:00-17:00", textFont).Width;
        return _slotsWidth;
    }

    // A ticket table's fixed columns: each wide enough for its widest cell and its title in either language. The
    // status gets a set width and is cut with an ellipsis (its tooltip has the full text).
    internal static IReadOnlyDictionary<TaskTableColumn, int> TableWidths(Font keyFont, Font cellFont, Font titleFont)
    {
        if (_tableWidths != null) return _tableWidths;
        var dateSamples = Enumerable.Range(1, 12)
            .Select(month => new DateTime(2026, month, 28))
            .SelectMany(date => new[] { Lang.ShortDate(date, false), Lang.ShortDate(date, true) })
            .ToArray();
        // TaskRow.PrCell's words in both languages.
        var prSamples = new[] { "Merged", "Đã merge", "Open", "Đang mở", "Abandoned", "Đã hủy", "1/2 merged", "1/2 đã merge" };
        var cellSamples = new Dictionary<TaskTableColumn, string[]>
        {
            [TaskTableColumn.Due] = dateSamples,
            [TaskTableColumn.Eta] = dateSamples,
            [TaskTableColumn.Priority] = new[] { "10", "2.5" },
            [TaskTableColumn.Logged] = new[] { "10h 30m" },
            [TaskTableColumn.PrMain] = prSamples,
            [TaskTableColumn.PrDev] = prSamples,
        };
        var widths = new Dictionary<TaskTableColumn, int>
        {
            [TaskTableColumn.Key] = KeyWidth(keyFont),
            [TaskTableColumn.Status] = UiScale.S(120),
        };
        foreach (var (column, samples) in cellSamples)
            widths[column] = samples.Max(sample => TextRenderer.MeasureText(sample, cellFont).Width);
        foreach (var column in widths.Keys.ToList())
        {
            var (english, vietnamese) = TaskTableHeader.TitleForms(column);
            widths[column] = Math.Max(widths[column],
                Math.Max(TextRenderer.MeasureText(english, titleFont).Width, TextRenderer.MeasureText(vietnamese, titleFont).Width));
        }
        _tableWidths = widths;
        return widths;
    }
}
