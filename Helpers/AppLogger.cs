namespace NoisLogTray;

// One log file per day (logs/app-yyyy-MM-dd.log). Files older than RetentionDays are
// purged once per day, on the first write of that day, so the folder cannot grow unbounded.
internal static class AppLogger
{
    private static readonly object Gate = new();
    internal const int RetentionDays = 30;
    private static DateOnly _lastPurge;

    // Tests point this at a temp folder so they never write into the user's real log.
    internal static string? DirectoryOverride;

    internal static void Info(string message) => Write("INFO", message);

    internal static void Error(string message) => Write("ERROR", message);

    internal static void Write(string level, string message)
    {
        try
        {
            var now = DateTime.Now;
            var dir = DirectoryOverride ?? AppPaths.LogDirectory;
            Directory.CreateDirectory(dir);
            var line = $"[{now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                var today = DateOnly.FromDateTime(now);
                if (_lastPurge != today)
                {
                    _lastPurge = today;
                    PurgeOld(dir, now);
                }
                File.AppendAllText(Path.Combine(dir, $"app-{now:yyyy-MM-dd}.log"), line);
            }
        }
        catch { /* logging must never throw */ }
    }

    // Delete log files (including the legacy app.log / app.log.1) last written more than
    // RetentionDays ago. Best-effort; a locked or vanished file is just skipped.
    internal static void PurgeOld(string dir, DateTime now)
    {
        var cutoff = now.AddDays(-RetentionDays);
        foreach (var file in Directory.EnumerateFiles(dir, "app*.log*"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
            catch { /* skip this file */ }
        }
    }
}
