namespace NoisLogTray;

// The fields of a Claude Code hook payload this app reads; the rest is ignored. SubagentsRunning: Stop's
// background_tasks still holds a running subagent. Reason: SessionEnd's why (clear, logout, prompt_input_exit, other).
internal sealed record ClaudeHookEvent(string EventName, string? ToolName, string? NotificationType, string? AgentId,
    string? SessionId = null, bool SubagentsRunning = false, string? TranscriptPath = null, string? Reason = null);
