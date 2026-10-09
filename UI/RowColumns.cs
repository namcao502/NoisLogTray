using System.Drawing;

namespace NoisLogTray;

// Fixed column widths for ticket rows (Tasks and Dashboard), so the summary and the facts after the due
// date start at the same x on every row whatever the key or date length. Measured once per font.
internal static class RowColumns
{
    private static readonly string[] DueSamples = { "Due: Sep 30", "Hạn: 30/09", "Due: -", "Hạn: -" };

    // Keyed by font: each row type has its own (static) key font.
    private static readonly Dictionary<Font, int> KeyWidths = new();
    private static int _dueWidth;
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

    internal static int DueWidth(Font factFont)
    {
        if (_dueWidth == 0) _dueWidth = DueSamples.Max(sample => TextRenderer.MeasureText(sample, factFont).Width);
        return _dueWidth;
    }
}
