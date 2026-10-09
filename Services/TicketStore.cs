using System.Text.Json;

namespace NoisLogTray;

// Ticket board state as JSON files (pending/started/done/dismissed by key, plus the seen key list),
// named and shaped like TaskWatcher's so its state can be imported as-is.
internal sealed class TicketStore
{
    // Seen goes last: it marks the import complete, so a copy failing earlier leaves it absent and the next start retries.
    internal static readonly string[] TaskWatcherFileNames =
    {
        ".jira-pending-queue.json", ".jira-started-tickets.json", ".jira-done-tickets.json",
        ".jira-dismissed-tickets.json", ".jira-pr-tracking.json", ".jira-assignment-seen.json",
    };

    private readonly string _stateDir;
    private readonly string _seenFilePath;
    private readonly string _pendingFilePath;
    private readonly string _startedFilePath;
    private readonly string _doneFilePath;
    private readonly string _dismissedFilePath;
    private readonly string _prTrackingFilePath;
    private readonly string _prCommentsFilePath;
    private readonly string _prBaselineFilePath;

    internal TicketStore(string stateDir)
    {
        _stateDir = stateDir;
        Directory.CreateDirectory(stateDir);
        _seenFilePath = Path.Combine(stateDir, ".jira-assignment-seen.json");
        _pendingFilePath = Path.Combine(stateDir, ".jira-pending-queue.json");
        _startedFilePath = Path.Combine(stateDir, ".jira-started-tickets.json");
        _doneFilePath = Path.Combine(stateDir, ".jira-done-tickets.json");
        _dismissedFilePath = Path.Combine(stateDir, ".jira-dismissed-tickets.json");
        _prTrackingFilePath = Path.Combine(stateDir, ".jira-pr-tracking.json");
        _prCommentsFilePath = Path.Combine(stateDir, ".jira-pr-comments.json");
        _prBaselineFilePath = Path.Combine(stateDir, ".pr-baseline-done");
    }

    internal bool HasSeenFile() => File.Exists(_seenFilePath);

    // Missing file -> empty list (callers use HasSeenFile()); existing but corrupt/empty/null -> null.
    internal List<string>? LoadSeen()
    {
        if (!File.Exists(_seenFilePath)) return new List<string>();

        var json = File.ReadAllText(_seenFilePath).Trim();
        if (json.Length == 0) return null;

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (JsonException)
        {
            BackUpCorruptFile(_seenFilePath);
            return null;
        }
    }

    internal void SaveSeen(IEnumerable<string> keys) => WriteAtomic(_seenFilePath, JsonSerializer.Serialize(keys.Distinct().ToList()));

    internal Dictionary<string, JiraTicket> LoadPending() => ReadMapFile<JiraTicket>(_pendingFilePath);

    internal void SavePending(Dictionary<string, JiraTicket> pending) => WriteMapFile(_pendingFilePath, pending);

    internal Dictionary<string, JiraTicket> LoadStarted() => ReadMapFile<JiraTicket>(_startedFilePath);

    internal void SaveStarted(Dictionary<string, JiraTicket> started) => WriteMapFile(_startedFilePath, started);

    internal Dictionary<string, JiraTicket> LoadDone() => ReadMapFile<JiraTicket>(_doneFilePath);

    internal void SaveDone(Dictionary<string, JiraTicket> done) => WriteMapFile(_doneFilePath, done);

    internal Dictionary<string, DismissedTicket> LoadDismissed() => ReadMapFile<DismissedTicket>(_dismissedFilePath);

    internal void SaveDismissed(Dictionary<string, DismissedTicket> dismissed) => WriteMapFile(_dismissedFilePath, dismissed);

    internal Dictionary<string, TrackedPr> LoadPrTracking() => ReadMapFile<TrackedPr>(_prTrackingFilePath);

    internal void SavePrTracking(Dictionary<string, TrackedPr> tracking) => WriteMapFile(_prTrackingFilePath, tracking);

    internal Dictionary<string, PrCommentAlert> LoadPrCommentAlerts() => ReadMapFile<PrCommentAlert>(_prCommentsFilePath);

    internal void SavePrCommentAlerts(Dictionary<string, PrCommentAlert> alerts) => WriteMapFile(_prCommentsFilePath, alerts);

    internal bool IsPrBaselineDone() => File.Exists(_prBaselineFilePath);

    // Empty marker file; its existence is the whole state.
    internal void MarkPrBaselineDone() => WriteAtomic(_prBaselineFilePath, string.Empty);

    // One-time import of TaskWatcher's state files; returns how many were copied.
    internal int ImportFromTaskWatcher(string sourceDir)
    {
        if (HasSeenFile() || !Directory.Exists(sourceDir)) return 0;

        var sourcePaths = TaskWatcherFileNames.Select(fn => Path.Combine(sourceDir, fn)).Where(File.Exists).ToList();
        var copiedCount = 0;
        foreach (var sourcePath in sourcePaths)
        {
            // Skip existing files so a retry never overwrites state changed since a partial import.
            var destinationPath = Path.Combine(_stateDir, Path.GetFileName(sourcePath));
            if (File.Exists(destinationPath)) continue;

            // Copy via temp + rename so a crash cannot leave a truncated file (esp. seen, which marks completion).
            File.Copy(sourcePath, destinationPath + ".tmp", overwrite: true);
            File.Move(destinationPath + ".tmp", destinationPath, overwrite: true);
            copiedCount++;
        }

        return copiedCount;
    }

    // An unreadable file is backed up and read as empty rather than crashing.
    private static Dictionary<string, T> ReadMapFile<T>(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, T>();

        var json = File.ReadAllText(path).Trim();
        if (json.Length == 0) return new Dictionary<string, T>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, T>>(json) ?? new Dictionary<string, T>();
        }
        catch (JsonException)
        {
            BackUpCorruptFile(path);
            return new Dictionary<string, T>();
        }
    }

    private static void WriteMapFile<T>(string path, Dictionary<string, T> map) => WriteAtomic(path, JsonSerializer.Serialize(map));

    private static void WriteAtomic(string path, string json)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    private static void BackUpCorruptFile(string path)
    {
        try
        {
            File.Copy(path, path + ".bad", overwrite: true);
            AppLogger.Error($"{Path.GetFileName(path)} was unreadable; preserved a copy at {path}.bad");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"{Path.GetFileName(path)} was unreadable and the backup failed: {ex.Message}");
        }
    }
}
