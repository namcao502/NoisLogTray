using System.Diagnostics;
using System.Text;

namespace NoisLogTray;

// One git run with stdout + stderr captured as UTF-8.
internal static class GitCommand
{
    private static readonly TimeSpan StreamDrainTimeout = TimeSpan.FromSeconds(5);

    // Could not start, timed out, or its output could not be read; always logged here.
    internal const int NotRunExitCode = -1;

    internal sealed record Result(int ExitCode, string Output, string Error)
    {
        public bool Ok => ExitCode == 0;
    }

    // A real non-zero exit is the caller's to log.
    internal static Result Run(string workingDirectory, TimeSpan timeout, params string[] arguments)
    {
        var commandLabel = $"git {string.Join(' ', arguments)}";
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            // Never block on a terminal credential prompt; the credential-manager GUI still works.
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                AppLogger.Error($"[git] {commandLabel}: Process.Start returned null");
                return new Result(NotRunExitCode, "", "");
            }

            // Read both pipes without awaiting so a full pipe cannot block git while we wait for exit.
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var streamTasks = new Task[] { outputTask, errorTask };
            if (!process.WaitForExit(timeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception killException)
                {
                    // Throws if the process exited in the meantime; the timeout is still worth logging.
                    AppLogger.Error($"[git] {commandLabel}: kill failed: {killException.Message}");
                }

                AppLogger.Error($"[git] {commandLabel}: timed out after {timeout.TotalSeconds}s");
                try
                {
                    Task.WaitAll(streamTasks, StreamDrainTimeout);
                }
                catch (Exception drainException)
                {
                    AppLogger.Error($"[git] {commandLabel}: output read failed: {drainException.Message}");
                }

                return new Result(NotRunExitCode, "", "");
            }

            // Bounded so a grandchild holding a pipe open cannot block forever; an unread pipe is a failed
            // run, never an empty "no output" success.
            try
            {
                if (!Task.WaitAll(streamTasks, StreamDrainTimeout))
                {
                    AppLogger.Error($"[git] {commandLabel}: output not read within {StreamDrainTimeout.TotalSeconds}s");
                    return new Result(NotRunExitCode, "", "");
                }
            }
            catch (AggregateException readException)
            {
                AppLogger.Error($"[git] {commandLabel}: output read failed: {readException.InnerException?.Message}");
                return new Result(NotRunExitCode, "", "");
            }

            return new Result(process.ExitCode, outputTask.Result, errorTask.Result);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[git] {commandLabel}: {ex.Message}");
            return new Result(NotRunExitCode, "", ex.Message);
        }
    }
}
