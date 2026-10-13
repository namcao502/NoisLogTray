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

    // A hook's transcript lives in the project folder of the directory claude was started in, so a claude the user
    // started elsewhere in the terminal (after a cd) is told apart from one of this folder.
    internal static bool IsTranscriptOfFolder(string? transcriptPath, string folderPath)
    {
        var transcriptDirectory = Path.GetFileName(Path.GetDirectoryName(transcriptPath));
        return string.Equals(transcriptDirectory, GetClaudeProjectDirectoryName(folderPath), StringComparison.OrdinalIgnoreCase);
    }

    // Claude Code stores sessions under ~/.claude/projects/<path with non-alphanumerics replaced by '-'>.
    internal static string GetClaudeProjectDirectoryName(string folderPath)
    {
        return Regex.Replace(folderPath, "[^a-zA-Z0-9]", "-");
    }

    // Stripped from what we launch (VS Code, the embedded claude): inherited from a Claude Code session, they make a
    // new Claude think it is a child session and turn transcript saving off. ANTHROPIC_* come back from ~/.claude/settings.json.
    // Claude Code also sets ToolShellVariables in its tool shells; inherited, the embedded claude loses its colors and updates.
    internal static bool IsClaudeSessionVariable(string name)
    {
        return name.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase)
            || ToolShellVariables.Contains(name);
    }

    private static readonly HashSet<string> ToolShellVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "NO_COLOR",
        "AI_AGENT",
        "DISABLE_AUTOUPDATER",
    };

    internal static void OpenVsCode(string targetPath, int? line = null)
    {
        using var process = Process.Start(VsCodeStartInfo(targetPath, line));
    }

    // For a file the user clicked: a failure is only logged.
    internal static void TryOpenVsCode(string filePath, int? line = null)
    {
        try
        {
            OpenVsCode(filePath, line);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[vscode] Opening {filePath} in VS Code failed: {ex.Message}");
        }
    }

    // `code` is a batch shim (code.cmd), unreliable as a direct process target: go through cmd.exe /c, hidden. The path
    // rides in a variable because cmd expands %NAME% on its line, even in quotes, but not inside an expanded value.
    internal static ProcessStartInfo VsCodeStartInfo(string targetPath, int? line = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = line == null ? "/c code \"%NOIS_VSCODE_TARGET%\"" : "/c code -g \"%NOIS_VSCODE_TARGET%\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var variableName in startInfo.Environment.Keys.Where(IsClaudeSessionVariable).ToList())
        {
            startInfo.Environment.Remove(variableName);
        }
        startInfo.Environment["NOIS_VSCODE_TARGET"] = line == null ? targetPath : $"{targetPath}:{line}";
        return startInfo;
    }
}
