using System.Globalization;

namespace NoisLogTray;

// Current UI language: English (default) or Vietnamese. Persisted in settings.json like the
// theme; Changed lets open windows re-apply their texts. Logs stay English.
internal static class Lang
{
    internal static bool Vietnamese;

    internal static event Action? Changed;

    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo VietnameseCulture = CultureInfo.GetCultureInfo("vi-VN");

    internal static CultureInfo Culture => Vietnamese ? VietnameseCulture : EnglishCulture;

    // The text for the current language; call at display time so a switch re-reads it.
    internal static string T(string english, string vietnamese) => Vietnamese ? vietnamese : english;

    // "Thursday, October 1, 2026" / "Thứ Năm, 01/10/2026".
    internal static string LongDate(DateTime date) => Vietnamese
        ? date.ToString("dddd, dd/MM/yyyy", VietnameseCulture)
        : date.ToString("dddd, MMMM d, yyyy", EnglishCulture);

    // "Oct 1" / "01/10".
    internal static string ShortDate(DateTime date) => Vietnamese
        ? date.ToString("dd/MM", VietnameseCulture)
        : date.ToString("MMM d", EnglishCulture);

    internal static void Load() => Vietnamese = AppSettings.Load().Language == "vi";

    internal static void Toggle()
    {
        Vietnamese = !Vietnamese;
        var settings = AppSettings.Load(); // read-modify-write to keep other keys
        settings.Language = Vietnamese ? "vi" : "en";
        AppSettings.Save(settings);
        Changed?.Invoke();
    }
}
