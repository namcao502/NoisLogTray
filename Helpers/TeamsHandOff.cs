namespace NoisLogTray;

// Pure parts of "Hand to Claude": the line typed into the ticket's terminal and how it gets there.
internal static class TeamsHandOff
{
    private const string PasteStart = "\u001b[200~";
    private const string PasteEnd = "\u001b[201~";

    internal static string Line(string taskPath)
    {
        return $"Read {taskPath} and do the investigation it asks for.";
    }

    internal static TeamsHandOffTarget Target(bool isRunning, bool claudeEndedInShell)
    {
        if (!isRunning)
        {
            return TeamsHandOffTarget.NotRunning;
        }
        return claudeEndedInShell ? TeamsHandOffTarget.ShellPrompt : TeamsHandOffTarget.ClaudePrompt;
    }

    // No Enter: the caller sends it after a pause, so claude has taken the paste first.
    internal static string PasteInput(string line)
    {
        return PasteStart + line + PasteEnd;
    }

    internal static string ShellCommand(string? idlePrompt, string line)
    {
        var flag = idlePrompt == "--continue" ? "--continue " : "";
        return $"claude {flag}{PowerShellQuote(line)}\r";
    }

    // A "--" start goes bare through BuildCommandLine, a plain line gets quoted there. The line holds no double quote.
    internal static string StartArgument(string? idlePrompt, string line)
    {
        return idlePrompt == "--continue" ? $"--continue {PowerShellQuote(line)}" : line;
    }

    // SessionEnd with reason clear (or none) can leave a claude running, so only another reason means the shell prompt is back.
    internal static bool EndsClaude(ClaudeHookEvent hookEvent)
    {
        return hookEvent.EventName == "SessionEnd" && hookEvent.Reason != null && hookEvent.Reason != "clear";
    }

    private static string PowerShellQuote(string text)
    {
        return "'" + text.Replace("'", "''") + "'";
    }
}
