namespace NoisLogTray;

internal static class AppLogger
{
    private static readonly object Gate = new();

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
            lock (Gate) File.AppendAllText(path, line);
        }
        catch { /* logging must never throw */ }
    }
}
