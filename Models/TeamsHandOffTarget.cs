namespace NoisLogTray;

// Where "Hand to Claude" puts the task line: claude's prompt, the PowerShell prompt after claude ended, or a terminal not running.
internal enum TeamsHandOffTarget
{
    ClaudePrompt,
    ShellPrompt,
    NotRunning,
}
