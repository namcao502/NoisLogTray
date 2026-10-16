using System.Text.Json;

namespace NoisLogTray;

internal static class ClaudeAttention
{
    // Null for anything that is not a JSON object with a string hook_event_name.
    internal static ClaudeHookEvent? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var eventName = ReadString(root, "hook_event_name");
            if (string.IsNullOrEmpty(eventName)) return null;
            var subagentsRunning = root.TryGetProperty("background_tasks", out var backgroundTasks)
                && backgroundTasks.ValueKind == JsonValueKind.Array
                && backgroundTasks.EnumerateArray().Any(bt => bt.ValueKind == JsonValueKind.Object
                    && ReadString(bt, "type") == "subagent" && ReadString(bt, "status") == "running");
            return new ClaudeHookEvent(eventName, ReadString(root, "tool_name"), ReadString(root, "notification_type"), ReadString(root, "agent_id"),
                ReadString(root, "session_id"), subagentsRunning, ReadString(root, "transcript_path"), ReadString(root, "reason"));
        }
        catch (Exception e) when (e is JsonException || e is InvalidOperationException)
        {
            return null; // InvalidOperationException: GetString on an invalid escape such as a lone surrogate
        }
    }

    // A subagent's tool call does not end the main turn's wait (background agents run on after a Stop), but it ends a
    // permission wait, usually because the user approved it (a parallel agent's tool can end it early). Notification is never skipped.
    internal static ClaudeWaitReason? Next(ClaudeWaitReason? current, ClaudeHookEvent hookEvent)
    {
        var isToolEvent = hookEvent.EventName == "PreToolUse" || hookEvent.EventName == "PostToolUse";
        if (isToolEvent && !string.IsNullOrEmpty(hookEvent.AgentId)) return current == ClaudeWaitReason.Permission ? null : current;
        return hookEvent.EventName switch
        {
            "UserPromptSubmit" => null,
            "PreToolUse" => hookEvent.ToolName == "AskUserQuestion" ? ClaudeWaitReason.Question : null,
            "PostToolUse" => null,
            // /exit (or /clear) leaves the terminal at its prompt, so nothing waits on that claude any more.
            "SessionEnd" => null,
            // Ended only to wait for background agents: claude starts another turn when they finish, and that Stop is the done.
            // Background shells and Monitors do not hold it back, since a dev server may run for good.
            "Stop" => hookEvent.SubagentsRunning ? current : ClaudeWaitReason.Finished,
            "Notification" => hookEvent.NotificationType switch
            {
                // AskUserQuestion is drawn as a permission dialog, so its prompt stays a question.
                "permission_prompt" => current == ClaudeWaitReason.Question ? current : ClaudeWaitReason.Permission,
                "worker_permission_prompt" => ClaudeWaitReason.Permission,
                "elicitation_dialog" or "elicitation_url_dialog" or "agent_needs_input" => ClaudeWaitReason.Question,
                _ => current, // idle_prompt follows a Stop that already set the wait
            },
            _ => current,
        };
    }

    // Looking at a finished turn is enough; a permission prompt or a question still blocks claude until answered.
    internal static bool ClearsWhenSeen(ClaudeWaitReason reason) => reason == ClaudeWaitReason.Finished;

    internal static string NoticeText(string sessionLabel, ClaudeWaitReason reason) => reason switch
    {
        ClaudeWaitReason.Permission => Lang.T($"{sessionLabel}: Claude needs your permission.", $"{sessionLabel}: Claude cần bạn cấp quyền."),
        ClaudeWaitReason.Question => Lang.T($"{sessionLabel}: Claude is asking you something.", $"{sessionLabel}: Claude đang hỏi bạn."),
        _ => Lang.T($"{sessionLabel}: Claude is done - your turn.", $"{sessionLabel}: Claude đã xong, tới lượt bạn."),
    };

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
