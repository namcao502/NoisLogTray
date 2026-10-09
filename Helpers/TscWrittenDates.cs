using System.Text.Json;

namespace NoisLogTray;

// Days whose TSC cell this app has (been about to) write; Start -> queue never creates a fresh entry for one.
// Own file, not settings.json: the drain runs on the threadpool and would race the settings saves.
internal static class TscWrittenDates
{
    private const int MaxDates = 31;
    private static readonly object Gate = new();

    internal static void Record(IEnumerable<DateOnly> dates, string? path = null)
    {
        var file = path ?? AppPaths.TscWrittenPath;
        lock (Gate)
        {
            var recorded = ReadUnlocked(file)
                .Concat(dates.Select(Hcm.ApiDate))
                .Distinct()
                .OrderBy(date => date)
                .TakeLast(MaxDates)
                .ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(recorded));
            File.Move(file + ".tmp", file, overwrite: true);
        }
    }

    internal static bool Contains(DateOnly date, string? path = null)
    {
        lock (Gate)
        {
            return ReadUnlocked(path ?? AppPaths.TscWrittenPath).Contains(Hcm.ApiDate(date));
        }
    }

    // Missing or unreadable reads as empty; only a failed write in Record throws.
    private static List<string> ReadUnlocked(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return new List<string>();
            }
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file)) ?? new List<string>();
        }
        catch (JsonException e)
        {
            AppLogger.Error($"[tsc-written] Corrupt, keeping a .bad copy: {e.Message}");
            try
            {
                File.Move(file, file + ".bad", overwrite: true);
            }
            catch (Exception moveError) when (moveError is IOException || moveError is UnauthorizedAccessException)
            {
                AppLogger.Error($"[tsc-written] Could not keep .bad copy: {moveError.Message}");
            }
            return new List<string>();
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            AppLogger.Error($"[tsc-written] Read failed: {e.Message}");
            return new List<string>();
        }
    }
}
