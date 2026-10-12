namespace NoisLogTray;

// The fields of a Claude Code hook payload this app reads; the rest is ignored.
internal sealed record ClaudeHookEvent(string EventName, string? ToolName, string? NotificationType, string? AgentId,
    string? SessionId = null);
