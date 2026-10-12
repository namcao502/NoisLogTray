using System.Diagnostics;
using System.Text.RegularExpressions;

namespace NoisLogTray;

internal static class TicketLauncher
{
    internal static readonly string ClaudeProjectsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    // "--continue" only when the worktree has a session: `claude --continue` with none just exits.
    internal static string? GetIdlePrompt(string worktreePath, string claudeProjectsRoot)
    {
        var sessionDirectory = Path.Combine(claudeProjectsRoot, GetClaudeProjectDirectoryName(worktreePath));

        // Lazy enumeration so Any() stops at the first session file.
        if (Directory.Exists(sessionDirectory) && Directory.EnumerateFiles(sessionDirectory, "*.jsonl").Any())
        {
            return "--continue";
        }

        return null;
    }

    // "--resume <id>" for a session the app itself ran in that folder, when its transcript is still there. Not
    // --continue: the user runs other Claude sessions in the dev repo, and --continue would pick the newest of theirs.
    internal static string? GetResumeArgument(string folderPath, string? sessionId, string claudeProjectsRoot)
    {
        if (!Guid.TryParse(sessionId, out var sessionGuid)) return null;
        var transcript = Path.Combine(claudeProjectsRoot, GetClaudeProjectDirectoryName(folderPath), $"{sessionGuid:D}.jsonl");
        return File.Exists(transcript) ? $"--resume {sessionGuid:D}" : null;
    }

    // Claude Code stores sessions under ~/.claude/projects/<path with non-alphanumerics replaced by '-'>.
    internal static string GetClaudeProjectDirectoryName(string folderPath)
    {
        return Regex.Replace(folderPath, "[^a-zA-Z0-9]", "-");
    }

    // Stripped from what we launch (VS Code, the embedded claude): inherited from a Claude Code session, they make a
    // new Claude think it is a child session and turn transcript saving off. ANTHROPIC_* come back from ~/.claude/settings.json.
    internal static bool IsClaudeSessionVariable(string name)
    {
        return name.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase);
    }

    internal static void OpenVsCode(string targetPath)
    {
        // `code` is a batch shim (code.cmd), unreliable as a direct process target: go through cmd.exe /c, hidden.
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c code \"{targetPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var variableName in startInfo.Environment.Keys.Where(IsClaudeSessionVariable).ToList())
        {
            startInfo.Environment.Remove(variableName);
        }

        using var process = Process.Start(startInfo);
    }
}
