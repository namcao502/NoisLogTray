using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace NoisLogTray;

// Settings > "Rebuild and restart": republishes the running copy with scripts\publish.ps1 (which swaps the files in use),
// then starts the new build, which waits for this process to quit before it takes the single-instance mutex.
internal static class AppRebuild
{
    internal const string WaitForExitArgument = "--wait-for-exit";
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PreviousInstanceTimeout = TimeSpan.FromMinutes(1);
    private static readonly string PublishFolder = Path.Combine("bin", "publish", "win-x64");
    private static readonly string PublishScript = Path.Combine("scripts", "publish.ps1");

    // The source repo when this exe runs from its <repo>\bin\publish\win-x64, the folder the script publishes to; else null.
    internal static string? FindSourceRepo(string baseDirectory)
    {
        var exeDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var repoPath = Path.GetFullPath(Path.Combine(exeDirectory, "..", "..", ".."));
        var publishDirectory = Path.Combine(repoPath, PublishFolder);
        if (!string.Equals(publishDirectory, exeDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return File.Exists(Path.Combine(repoPath, PublishScript)) ? repoPath : null;
    }

    // Returns null on success, else the error to show; every output line goes to the log as it arrives.
    internal static string? Publish(string repoPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = repoPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(repoPath, PublishScript) })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.OutputDataReceived += (_, e) => LogLine(e.Data);
            process.ErrorDataReceived += (_, e) => LogLine(e.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            // Timed, never the parameterless wait: MSBuild nodes and the compiler server outlive the build holding the pipes.
            if (!process.WaitForExit(PublishTimeout))
            {
                process.Kill(entireProcessTree: true);
                return $"the build took over {PublishTimeout.TotalMinutes:0} minutes";
            }

            return process.ExitCode == 0 ? null : $"publish.ps1 exited with {process.ExitCode}";
        }
        catch (Exception exception)
        {
            AppLogger.Error($"[rebuild] {exception}");
            return exception.Message;
        }
    }

    // Starts the freshly published exe, told to wait for this process; false (logged) when it could not start.
    internal static bool StartNewBuild(string repoPath)
    {
        var exePath = Path.Combine(repoPath, PublishFolder, "NoisLogTray.exe");
        var startInfo = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
        };
        startInfo.ArgumentList.Add(WaitForExitArgument);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        try
        {
            using var process = Process.Start(startInfo);
            return process != null;
        }
        catch (Exception exception)
        {
            AppLogger.Error($"[rebuild] could not start {exePath}: {exception.Message}");
            return false;
        }
    }

    // The process id after WaitForExitArgument, or null when the app was started normally.
    internal static int? PreviousInstanceId(string[] args)
    {
        if (args.Length != 2 || args[0] != WaitForExitArgument)
        {
            return null;
        }

        return int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var processId) ? processId : null;
    }

    // A rebuild starts this copy before the old one quits; until it has, the mutex would turn this copy away.
    internal static void WaitForPreviousInstance(string[] args)
    {
        var processId = PreviousInstanceId(args);
        if (processId == null)
        {
            return;
        }

        try
        {
            using var previous = Process.GetProcessById(processId.Value);
            if (!previous.WaitForExit(PreviousInstanceTimeout))
            {
                AppLogger.Error($"[rebuild] the previous instance ({processId}) is still running after {PreviousInstanceTimeout.TotalSeconds}s");
            }
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    private static void LogLine(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            AppLogger.Info($"[rebuild] {line}");
        }
    }
}
