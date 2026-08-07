namespace NoisLogTray;

internal static class AppLogger
{
    private static readonly object Gate = new();
    private const long MaxBytes = 1_000_000; // ~1 MB, then roll to app.log.1 (one backup)

    internal static void Info(string message) => Write("INFO", message);

    internal static void Error(string message) => Write("ERROR", message);

    internal static void Write(string level, string message, string? logPath = null)
    {
        try
        {
            var path = logPath ?? AppPaths.LogPath;
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                RotateIfNeeded(path);
                File.AppendAllText(path, line);
            }
        }
        catch { /* logging must never throw */ }
    }

    // Roll the log to <path>.1 once it passes MaxBytes, keeping a single backup so the
    // file can't grow without bound. Best-effort; a failure just skips rotation.
    private static void RotateIfNeeded(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxBytes)
                File.Move(path, path + ".1", overwrite: true);
        }
        catch { /* keep logging even if rotation fails */ }
    }
}
