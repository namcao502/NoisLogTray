using System.Diagnostics;
using System.Text;

namespace NoisLogTray;

// Runs `claude -p` once and collects its output; shared by the knowledge miner and the Teams reply suggestions.
internal static class ClaudeCli
{
    private static readonly TimeSpan StreamDrainTimeout = TimeSpan.FromSeconds(5);

    // Exit -1 with the reason in Error on a timeout, a cancel or a failed start.
    internal static (int ExitCode, string Output, string Error) Run(string workingDirectory, IReadOnlyList<string> arguments,
        string? standardInput, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(ClaudeSessionHost.DefaultClaudeExecutable())
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = standardInput != null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (standardInput != null)
        {
            startInfo.StandardInputEncoding = new UTF8Encoding(false);
        }

        foreach (var argument in arguments)
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
            using var cancelRegistration = cancellationToken.Register(() => KillTree(process));
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (standardInput != null)
            {
                process.StandardInput.Write(standardInput);
                process.StandardInput.Close();
            }

            if (!process.WaitForExit(timeout))
            {
                process.Kill(entireProcessTree: true);
                return (-1, "", $"timed out after {timeout.TotalMinutes:0} minutes");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return (-1, "", "cancelled");
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
            AppLogger.Error($"[claude] could not run claude: {exception}");
            return (-1, "", cancellationToken.IsCancellationRequested ? "cancelled" : exception.Message);
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
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
}
