using System.Text;

namespace NoisLogTray;

// The knowledge scan's Windows task and log, for the Schedules dialog and the tray's check. Read never throws: a half that
// fails carries its error, so the other half still shows.
internal static class KnowledgeScanService
{
    internal const int MaxLogBytes = 1024 * 1024; // a few weeks of runs; older ones drop off the page

    internal static KnowledgeScanSnapshot Read(string devRepoPath)
    {
        ScheduledTask? task = null;
        string? taskError = null;
        try
        {
            task = WindowsScheduledTask.Read(KnowledgeScan.TaskName);
        }
        catch (Exception exception)
        {
            // Late-bound COM fails in many exception types; each one only means this half shows its error.
            AppLogger.Error($"[schedules] Reading task {KnowledgeScan.TaskName} failed: {exception}");
            taskError = exception.Message;
        }

        var logPath = KnowledgeMiner.ScanLogPath(devRepoPath);
        var isLogMissing = !File.Exists(logPath);
        IReadOnlyList<KnowledgeScanRun> runs = [];
        string? logError = null;
        if (!isLogMissing)
        {
            try
            {
                var (logText, firstLineNumber) = ReadLogTail(logPath, MaxLogBytes);
                runs = KnowledgeScan.ParseRuns(logText, firstLineNumber);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                AppLogger.Error($"[schedules] Reading {logPath} failed: {exception.Message}");
                logError = exception.Message;
            }
        }

        return new KnowledgeScanSnapshot(task, taskError, runs, isLogMissing, logError, logPath);
    }

    // The last maxBytes of the log from a whole line on, and that line's 1-based number in the whole file. Shared
    // read/write: the scan may be appending right now.
    internal static (string Text, int FirstLineNumber) ReadLogTail(string logPath, int maxBytes)
    {
        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var tailStart = Math.Max(0, stream.Length - maxBytes);
        var skippedLineCount = 0;
        var buffer = new byte[64 * 1024];
        long position = 0;
        while (position < tailStart)
        {
            var readByteCount = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, tailStart - position));
            if (readByteCount == 0)
            {
                break;
            }

            skippedLineCount += buffer.AsSpan(0, readByteCount).Count((byte)'\n');
            position += readByteCount;
        }

        using var tail = new MemoryStream();
        stream.CopyTo(tail);
        var text = Encoding.UTF8.GetString(tail.GetBuffer(), 0, (int)tail.Length);
        if (tailStart == 0)
        {
            return (text, 1);
        }

        // Mid-file: the first line is cut, so the tail starts after the next newline.
        var firstNewlineIndex = text.IndexOf('\n');
        return firstNewlineIndex < 0 ? ("", skippedLineCount + 1) : (text[(firstNewlineIndex + 1)..], skippedLineCount + 2);
    }

    internal static void RunNow()
    {
        WindowsScheduledTask.Run(KnowledgeScan.TaskName);
        AppLogger.Info($"[schedules] {KnowledgeScan.TaskName} started by hand.");
    }

    internal static void SetTimeLimit(int hours)
    {
        WindowsScheduledTask.SetExecutionTimeLimit(KnowledgeScan.TaskName, TimeSpan.FromHours(hours));
        AppLogger.Info($"[schedules] {KnowledgeScan.TaskName} time limit set to {hours}h.");
    }
}
