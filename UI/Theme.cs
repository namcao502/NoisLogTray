using System.Drawing;
using System.Text.Json;

namespace NoisLogTray;

// Central light/dark palette. Controls read these at paint time and subscribe to
// Changed to repaint when the mode flips. The choice is persisted to settings.json
// and defaults to dark.
internal static class Theme
{
    internal static bool Dark = true; // default; overridden by the saved setting

    internal static event Action? Changed;

    private static Color Pick(int r, int g, int b, int dr, int dg, int db) =>
        Dark ? Color.FromArgb(dr, dg, db) : Color.FromArgb(r, g, b);

    internal static Color WindowBg => Pick(245, 245, 247, 28, 28, 30);
    internal static Color CardSurface => Pick(255, 255, 255, 44, 44, 48);
    internal static Color CardBorder => Pick(229, 229, 234, 62, 62, 66);
    internal static Color InputBg => Pick(255, 255, 255, 52, 52, 56);
    internal static Color InputBorder => Pick(208, 208, 214, 78, 78, 84);
    internal static Color TextPrimary => Pick(40, 40, 44, 236, 236, 240);
    internal static Color TextSecondary => Pick(130, 130, 138, 150, 150, 158);
    internal static Color Hover => Pick(242, 242, 247, 60, 60, 66);
    internal static Color Divider => Pick(220, 220, 224, 62, 62, 66);
    internal static Color SecondaryBtn => Pick(238, 238, 240, 64, 64, 70);
    internal static Color SecondaryBtnHover => Pick(228, 228, 231, 80, 80, 86);
    internal static Color SecondaryBtnText => Pick(28, 28, 30, 236, 236, 240);

    internal static readonly Color Accent = Color.FromArgb(0, 122, 255);
    internal static readonly Color AccentHover = Color.FromArgb(20, 132, 255);

    // Load the persisted choice (defaults to dark when there is no saved setting).
    internal static void Load()
    {
        try
        {
            var path = AppPaths.SettingsPath;
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("dark", out var d) &&
                (d.ValueKind == JsonValueKind.True || d.ValueKind == JsonValueKind.False))
                Dark = d.GetBoolean();
        }
        catch
        {
            // keep the default on any read/parse failure
        }
    }

    internal static void Toggle()
    {
        Dark = !Dark;
        Save();
        Changed?.Invoke();
    }

    private static void Save()
    {
        try
        {
            var path = AppPaths.SettingsPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(new { dark = Dark }));
        }
        catch
        {
            // best effort; the preference just won't persist
        }
    }
}
