namespace NoisLogTray;

// Per-user data lives in %AppData%\NoisLogTray (queue, settings, logs). Mirrors
// the TrayTemps layout; replaces the old repo-root data/ + logs/ directories.
internal static class AppPaths
{
    internal static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NoisLogTray");

    internal static string QueuePath => Path.Combine(DataDirectory, "queue.json");

    internal static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    // Per-user config/secrets, written by the first-run/edit dialog. Overrides the
    // optional shared defaults in the app-directory .env (see AppConfig.DefaultSources).
    internal static string EnvPath => Path.Combine(DataDirectory, ".env");

    internal static string LogPath => Path.Combine(DataDirectory, "logs", "app.log");
}
