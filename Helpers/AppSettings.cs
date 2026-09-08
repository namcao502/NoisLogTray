using System.Text.Json;

namespace NoisLogTray;

// Persisted user data (settings.json under %AppData%\NoisLogTray): UI state (theme,
// window position) plus the Config key/value map that AppConfig reads. Kept in one
// object so independent writers (theme toggle, window move, credential save) round-trip
// the whole thing and never clobber each other's keys. Writes are atomic (temp file +
// rename); a file that exists but cannot be parsed is preserved as settings.json.bad
// rather than silently overwritten with defaults.
internal sealed class AppSettings
{
    public bool Dark { get; set; } = true;
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public Dictionary<string, string> Config { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    internal static AppSettings Load(string? path = null) => LoadOrBackup(out _, path);

    // A missing file yields defaults (corrupt=false). A file that exists but cannot be
    // parsed is copied aside to settings.json.bad - so the raw content, including any
    // credentials, is preserved for recovery - and defaults are returned with
    // corrupt=true. The primary file is never silently overwritten on a read failure.
    internal static AppSettings LoadOrBackup(out bool corrupt, string? path = null)
    {
        corrupt = false;
        var file = path ?? AppPaths.SettingsPath;
        if (!File.Exists(file)) return new AppSettings();
        try
        {
            var parsed = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), Options);
            if (parsed != null)
            {
                parsed.Config ??= new();
                return parsed;
            }
        }
        catch
        {
            // fall through to backup + defaults
        }

        corrupt = true;
        TryBackupCorrupt(file);
        return new AppSettings();
    }

    private static void TryBackupCorrupt(string file)
    {
        try
        {
            File.Copy(file, file + ".bad", overwrite: true);
            AppLogger.Error($"settings.json was unreadable; preserved a copy at {file}.bad");
        }
        catch
        {
            // best effort
        }
    }

    internal static void Save(AppSettings settings, string? path = null)
    {
        try
        {
            var file = path ?? AppPaths.SettingsPath;
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Write to a temp file then atomically replace, so an interrupted write can
            // never leave a truncated (unreadable) settings.json behind.
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, file, overwrite: true);
        }
        catch
        {
            // best effort; the setting just won't persist
        }
    }
}
