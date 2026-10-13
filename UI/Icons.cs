using System.Drawing;
using System.Drawing.Text;

namespace NoisLogTray;

// The app's one icon set: Fluent UI System Icons (Microsoft, MIT; Assets/icons), drawn as font glyphs so they take any
// color and size. The glyphs are the _20_ variants, centered in their em box. Filled is for a selected state; the
// web views load the same fonts (Assets/diff, Assets/files).
internal static class Icons
{
    internal const string Board = "\uE156";
    internal const string TaskList = "\uEC99";
    internal const string Ticket = "\uED7C";
    internal const string Settings = "\uF6A9";
    internal const string Navigation = "\uF560";
    internal const string Alert = "\uF114";
    internal const string Sunny = "\uF8A1";
    internal const string Moon = "\uF87D";
    internal const string Minimize = "\uEBD0";
    internal const string Maximize = "\uE7EB";
    internal const string Restore = "\uEB96";
    internal const string Dismiss = "\uF369";
    internal const string ChevronDown = "\uF2A3";
    internal const string ChevronRight = "\uF2B0";
    internal const string ChevronUp = "\uF2B6";
    internal const string ChevronLeft = "\uF2AA";
    internal const string More = "\uE824";
    internal const string CheckmarkCircle = "\uF298";
    internal const string ErrorCircle = "\uF3F1";
    internal const string Info = "\uF4A3";
    internal const string Record = "\uF661";
    internal const string DismissCircle = "\uF36D";
    internal const string ArrowRight = "\uF181";
    internal const string Add = "\uF109";
    internal const string Checkmark = "\uF294";
    internal const string WindowConsole = "\uEE6F";

    internal const string BoardFilled = "\uE167";
    internal const string TaskListFilled = "\uEC97";
    internal const string TicketFilled = "\uED7C";
    internal const string WindowConsoleFilled = "\uEE6F";
    internal const string SettingsFilled = "\uF6B2";

    private const TextFormatFlags GlyphFlags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
        | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    // Before the fonts below: static fields initialize in order. AddFontFile also registers each font with GDI, so
    // TextRenderer can draw it.
    private static readonly PrivateFontCollection Collection = LoadFonts();
    private static readonly FontFamily RegularFamily = Find("FluentSystemIcons-Regular");
    private static readonly FontFamily FilledFamily = Find("FluentSystemIcons-Filled");

    internal static readonly Font Small = Regular(16);
    internal static readonly Font Medium = Regular(20);
    internal static readonly Font MediumFilled = new(FilledFamily, UiScale.S(20f) * 0.75f, GraphicsUnit.Point);

    // An icon `pixels` tall at 100% (in points, so it follows the zoom and the monitor DPI like the text does).
    internal static Font Regular(float pixels) => new(RegularFamily, UiScale.S(pixels) * 0.75f, GraphicsUnit.Point);

    internal static void Draw(Graphics graphics, string glyph, Font font, Rectangle bounds, Color color) =>
        TextRenderer.DrawText(graphics, glyph, font, bounds, color, GlyphFlags);

    private static PrivateFontCollection LoadFonts()
    {
        var collection = new PrivateFontCollection();
        foreach (var fileName in new[] { "FluentSystemIcons-Regular.ttf", "FluentSystemIcons-Filled.ttf" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "icons", fileName);
            try
            {
                collection.AddFontFile(path);
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[ui] Could not load the icon font {path}: {ex.Message}");
            }
        }
        return collection;
    }

    // A missing font file leaves the icons as boxes in a stock font rather than failing the window.
    private static FontFamily Find(string familyName) =>
        Collection.Families.FirstOrDefault(ff => ff.Name == familyName) ?? FontFamily.GenericSansSerif;
}
