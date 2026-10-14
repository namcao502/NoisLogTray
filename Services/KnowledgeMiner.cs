using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NoisLogTray;

// Runs the mine-knowledge skill headlessly for one ticket, the way docs\local-config\daily-knowledge-scan.ps1 does, and
// records the run in that scan's log, which is where the cleanup dialog reads which tickets are mined.
internal static class KnowledgeMiner
{
    internal const string ScanFilePrefix = ".daily-knowledge-scan.";
    private static readonly TimeSpan MineTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StreamDrainTimeout = TimeSpan.FromSeconds(5);
    private const int MaxLoggedErrorChars = 500;

    internal static string ScanLogPath(string devRepoPath)
    {
        return Path.Combine(devRepoPath, "docs", "local-config", $"{ScanFilePrefix}log");
    }

    internal static string RetryFilePath(string devRepoPath)
    {
        return Path.Combine(devRepoPath, "docs", "local-config", $"{ScanFilePrefix}retry");
    }

    // Returns null on success, else the error to show on the ticket's row.
    internal static string? Mine(string devRepoPath, string ticketKey)
    {
        var (exitCode, output, error) = RunClaude(devRepoPath, ticketKey);
        var (succeeded, resultText) = exitCode == 0 ? ParseResult(output) : (false, error.Length != 0 ? error : output);
        var oneLineResult = string.Join(' ', resultText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (!succeeded)
        {
            var shortError = oneLineResult.Length > MaxLoggedErrorChars ? oneLineResult[..MaxLoggedErrorChars] : oneLineResult;
            // No "Mining <KEY>" here: that line is what marks a ticket mined.
            AppendScanLog(devRepoPath, $"mine-knowledge {ticketKey} from NoisLogTray FAILED (exit {exitCode}): {shortError}");
            AppLogger.Error($"[mine] {ticketKey} failed (exit {exitCode}): {shortError}");
            return exitCode == 0 ? shortError : $"mine-knowledge exited with {exitCode} (see log)";
        }

        AppendScanLog(devRepoPath, $"Mining {ticketKey} ... (NoisLogTray)", $"  {oneLineResult}");
        RemoveRetryEntry(devRepoPath, ticketKey);
        AppLogger.Info($"[mine] {ticketKey}: {oneLineResult}");
        return null;
    }

    // `claude -p --output-format json` prints one result object; is_error is set when the run itself failed.
    internal static (bool Succeeded, string Text) ParseResult(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var resultText = root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String
                ? result.GetString() ?? ""
                : "";
            var isError = root.TryGetProperty("is_error", out var isErrorElement) && isErrorElement.ValueKind == JsonValueKind.True;
            return (!isError, resultText);
        }
        catch (JsonException)
        {
            return (false, $"could not read the result: {json}");
        }
    }

    // The retry file holds "<KEY> <failed attempts>" per line; a ticket mined here no longer needs the scan's retry.
    internal static string WithoutRetryEntry(string retryFile, string ticketKey)
    {
        var keptLines = retryFile.TrimStart('\uFEFF')
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Trim().Length != 0)
            .Where(l => !string.Equals(l.Trim().Split(' ')[0], ticketKey, StringComparison.OrdinalIgnoreCase));
        return string.Join("\r\n", keptLines);
    }

    private static (int ExitCode, string Output, string Error) RunClaude(string devRepoPath, string ticketKey)
    {
        var startInfo = new ProcessStartInfo(ClaudeSessionHost.DefaultClaudeExecutable())
        {
            WorkingDirectory = devRepoPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // The same tools and permission mode as the daily scan, which runs it with nobody watching too.
        foreach (var argument in new[] { "-p", $"mine-knowledge {ticketKey}", "--allowedTools", "Read,Write,Edit,Bash,Skill",
            "--permission-mode", "acceptEdits", "--output-format", "json" })
        {
            startInfo.ArgumentList.Add(argument);
        }
        // A tray started from a Claude Code session would otherwise make this a child session of it.
        foreach (var variableName in startInfo.Environment.Keys.Where(TicketLauncher.IsClaudeSessionVariable).ToList())
        {
            startInfo.Environment.Remove(variableName);
        }

        var output = new StringBuilder();
        var error = new StringBuilder();
        using var streamsClosed = new CountdownEvent(2);
        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.OutputDataReceived += (_, e) => AppendLine(output, e.Data, streamsClosed);
            process.ErrorDataReceived += (_, e) => AppendLine(error, e.Data, streamsClosed);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(MineTimeout))
            {
                process.Kill(entireProcessTree: true);
                return (-1, "", $"timed out after {MineTimeout.TotalMinutes:0} minutes");
            }

            // Bounded: a child process (an MCP server) may still hold the pipes after claude exits.
            streamsClosed.Wait(StreamDrainTimeout);
            lock (output)
            {
                lock (error)
                {
                    return (process.ExitCode, output.ToString(), error.ToString());
                }
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error($"[mine] could not run claude for {ticketKey}: {exception}");
            return (-1, "", exception.Message);
        }
    }

    private static void AppendLine(StringBuilder target, string? line, CountdownEvent streamsClosed)
    {
        if (line == null)
        {
            streamsClosed.Signal();
            return;
        }

        lock (target)
        {
            target.AppendLine(line);
        }
    }

    // The scan's own format: "yyyy-MM-dd HH:mm:ss <message>", one line each.
    private static void AppendScanLog(string devRepoPath, params string[] messages)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var lines = string.Concat(messages.Select(m => $"{timestamp} {m}\r\n"));
        try
        {
            File.AppendAllText(ScanLogPath(devRepoPath), lines, new UTF8Encoding(false));
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            AppLogger.Error($"[mine] writing {ScanLogPath(devRepoPath)} failed: {exception.Message}");
        }
    }

    private static void RemoveRetryEntry(string devRepoPath, string ticketKey)
    {
        var retryFilePath = RetryFilePath(devRepoPath);
        try
        {
            if (!File.Exists(retryFilePath))
            {
                return;
            }

            var keptEntries = WithoutRetryEntry(File.ReadAllText(retryFilePath), ticketKey);
            if (keptEntries.Length == 0)
            {
                File.Delete(retryFilePath);
            }
            else
            {
                File.WriteAllText(retryFilePath, keptEntries + "\r\n", new UTF8Encoding(true));
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            AppLogger.Error($"[mine] updating {retryFilePath} failed: {exception.Message}");
        }
    }
}
