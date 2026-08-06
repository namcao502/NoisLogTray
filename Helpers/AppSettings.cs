using System.Text.Json;

namespace NoisLogTray;

// Persisted user settings (settings.json under %AppData%\NoisLogTray). Kept in one
// place so independent writers (theme toggle, window move) round-trip the whole
// object and never clobber each other's keys. All I/O is best-effort: a missing or
// malformed file yields defaults, and a failed write is swallowed.
internal sealed class AppSettings
{
    public bool Dark { get; set; } = true;
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    internal static AppSettings Load()
    {
        try
        {
            var path = AppPaths.SettingsPath;
            if (!File.Exists(path)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    internal static void Save(AppSettings settings)
    {
        try
        {
            var path = AppPaths.SettingsPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // best effort; the setting just won't persist
        }
    }
}
