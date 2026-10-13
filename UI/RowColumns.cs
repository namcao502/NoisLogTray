using System.Drawing;

namespace NoisLogTray;

// Fixed column widths for ticket rows (Tasks and Dashboard), so the summary and the facts start at the same x on every
// row whatever the key, date or hours. Measured once per font.
internal static class RowColumns
{
    // The facts TaskRow.Facts yields first, in its order: Due, P, ETA, Logged. Each lists its widest forms in either
    // language (every month, as "May" is wider than "Sep"); the status follows at its own width.
    private static readonly string[][] FactSamples =
    {
        DateSamples("Due", "Hạn"),
        new[] { "P 10", "P 2.5", "P -" },
        DateSamples("ETA", "ETA"),
        new[] { "Logged 10h 30m", "Đã log 10h 30m", "Logged -", "Đã log -" },
    };

    // Keyed by font: each row type has its own (static) font.
    private static readonly Dictionary<Font, int> KeyWidths = new();
    private static readonly Dictionary<Font, IReadOnlyList<int>> FactWidthsByFont = new();
    private static int _slotsWidth;

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

    // The text width of each fixed fact column, in FactSamples' order.
    internal static IReadOnlyList<int> FactWidths(Font factFont)
    {
        if (!FactWidthsByFont.TryGetValue(factFont, out var widths))
        {
            widths = FactSamples.Select(samples => samples.Max(sample => TextRenderer.MeasureText(sample, factFont).Width)).ToList();
            FactWidthsByFont[factFont] = widths;
        }
        return widths;
    }

    internal static int DueWidth(Font factFont) => FactWidths(factFont)[0];

    private static string[] DateSamples(string englishLabel, string vietnameseLabel) =>
        Enumerable.Range(1, 12)
            .Select(month => new DateTime(2026, month, 28))
            .SelectMany(date => new[] { $"{englishLabel} {Lang.ShortDate(date, false)}", $"{vietnameseLabel} {Lang.ShortDate(date, true)}" })
            .Append($"{englishLabel} -")
            .Append($"{vietnameseLabel} -")
            .ToArray();
}
