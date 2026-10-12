namespace NoisLogTray.Tests;

// Theory data names the ClaudeWaitReason members as strings (null = no wait) because the enum is internal
// and cannot appear in a public test signature.
public class ClaudeAttentionTests
{
    [Fact]
    public void Parse_ReadsTheFieldsThisAppUses_WhenThePayloadHasThem()
    {
        const string json = """
            {
              "session_id": "abc",
              "cwd": "C:\\repo",
              "hook_event_name": "Stop",
              "last_assistant_message": "Done.",
              "tool_name": "Bash",
              "notification_type": "idle_prompt",
              "agent_id": "agent-1"
            }
            """;

        var hookEvent = ClaudeAttention.Parse(json);

        Assert.Equal(new ClaudeHookEvent("Stop", "Bash", "idle_prompt", "agent-1", "abc"), hookEvent);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"hook_event_name":5}""")]
    [InlineData("""{"hook_event_name":""}""")]
    [InlineData("""{"hook_event_name":"Stop","tool_name":"\ud800"}""")]
    public void Parse_ReturnsNull_WhenNotAHookObject(string json)
    {
        Assert.Null(ClaudeAttention.Parse(json));
    }

    [Theory]
    [InlineData(null, "Stop", null, null, "Finished")]
    [InlineData(null, "Notification", null, "permission_prompt", "Permission")]
    [InlineData("Finished", "Notification", null, "permission_prompt", "Permission")]
    [InlineData("Question", "Notification", null, "permission_prompt", "Question")]
    [InlineData(null, "Notification", null, "worker_permission_prompt", "Permission")]
    [InlineData(null, "Notification", null, "elicitation_dialog", "Question")]
    [InlineData(null, "Notification", null, "elicitation_url_dialog", "Question")]
    [InlineData(null, "Notification", null, "agent_needs_input", "Question")]
    [InlineData("Finished", "Notification", null, "idle_prompt", "Finished")]
    [InlineData(null, "Notification", null, "idle_prompt", null)]
    [InlineData("Finished", "Notification", null, "auth_success", "Finished")]
    [InlineData(null, "PreToolUse", "AskUserQuestion", null, "Question")]
    [InlineData("Finished", "PreToolUse", "Bash", null, null)]
    [InlineData("Permission", "PostToolUse", "Bash", null, null)]
    [InlineData("Question", "PostToolUse", "AskUserQuestion", null, null)]
    [InlineData("Permission", "Stop", null, null, "Finished")]
    [InlineData("Finished", "UserPromptSubmit", null, null, null)]
    [InlineData("Finished", "SubagentStop", null, null, "Finished")]
    public void Next_MapsEachEvent_ToTheExpectedWait(string? current, string eventName, string? toolName, string? notificationType, string? expected)
    {
        var hookEvent = new ClaudeHookEvent(eventName, toolName, notificationType, null);

        Assert.Equal(ParseWaitReason(expected), ClaudeAttention.Next(ParseWaitReason(current), hookEvent));
    }

    [Theory]
    [InlineData("PreToolUse", "Finished")]
    [InlineData("PostToolUse", "Finished")]
    [InlineData("PreToolUse", "Question")]
    [InlineData("PostToolUse", "Question")]
    public void Next_KeepsCurrent_WhenASubagentUsesATool(string eventName, string current)
    {
        var hookEvent = new ClaudeHookEvent(eventName, "Bash", null, "agent-1");

        Assert.Equal(ParseWaitReason(current), ClaudeAttention.Next(ParseWaitReason(current), hookEvent));
    }

    [Fact]
    public void Next_EndsAPermissionWait_WhenASubagentToolRuns()
    {
        var hookEvent = new ClaudeHookEvent("PostToolUse", "Bash", null, "agent-1");

        Assert.Null(ClaudeAttention.Next(ClaudeWaitReason.Permission, hookEvent));
    }

    [Fact]
    public void Next_SetsPermission_WhenASubagentAsks()
    {
        var hookEvent = new ClaudeHookEvent("Notification", null, "permission_prompt", "agent-1");

        Assert.Equal(ClaudeWaitReason.Permission, ClaudeAttention.Next(null, hookEvent));
    }

    private static ClaudeWaitReason? ParseWaitReason(string? waitReasonName) =>
        waitReasonName == null ? null : Enum.Parse<ClaudeWaitReason>(waitReasonName);
}
