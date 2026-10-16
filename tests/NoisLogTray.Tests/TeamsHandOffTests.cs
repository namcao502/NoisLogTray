namespace NoisLogTray.Tests;

public class TeamsHandOffTests
{
    private const string TaskLine = @"Read C:\Temp\NoisLogTray\teams-tasks\MDP-1-1.md and do the investigation it asks for.";

    [Fact]
    public void Target_ClaudePrompt_WhenRunningAndClaudeAlive()
    {
        Assert.Equal(TeamsHandOffTarget.ClaudePrompt, TeamsHandOff.Target(true, false));
    }

    [Fact]
    public void Target_ShellPrompt_WhenClaudeEnded()
    {
        Assert.Equal(TeamsHandOffTarget.ShellPrompt, TeamsHandOff.Target(true, true));
    }

    [Fact]
    public void Target_NotRunning_WhenStopped()
    {
        Assert.Equal(TeamsHandOffTarget.NotRunning, TeamsHandOff.Target(false, false));
        Assert.Equal(TeamsHandOffTarget.NotRunning, TeamsHandOff.Target(false, true));
    }

    [Fact]
    public void Line_NamesTheTaskFile()
    {
        Assert.Equal(TaskLine, TeamsHandOff.Line(@"C:\Temp\NoisLogTray\teams-tasks\MDP-1-1.md"));
    }

    [Fact]
    public void PasteInput_WrapsLineInBracketedPaste_WithoutEnter()
    {
        var input = TeamsHandOff.PasteInput("hello");

        Assert.Equal("\u001b[200~hello\u001b[201~", input);
        Assert.DoesNotContain('\r', input);
        Assert.DoesNotContain('\n', input);
    }

    [Fact]
    public void ShellCommand_UsesContinue_AndDoublesSingleQuotes()
    {
        Assert.Equal("claude --continue 'it''s here'\r", TeamsHandOff.ShellCommand("--continue", "it's here"));
        Assert.Equal("claude 'it''s here'\r", TeamsHandOff.ShellCommand(null, "it's here"));
        Assert.Equal("claude 'x'\r", TeamsHandOff.ShellCommand("something else", "x"));
    }

    [Fact]
    public void StartArgument_StartsWithDashes_WhenContinue_ElsePlainLine()
    {
        Assert.Equal("--continue 'line'", TeamsHandOff.StartArgument("--continue", "line"));
        Assert.Equal("line", TeamsHandOff.StartArgument(null, "line"));
    }

    [Theory]
    [InlineData("--continue")]
    [InlineData(null)]
    public void StartArgument_IsAcceptedByBuildCommandLine_WithoutDoubleQuotes(string? idlePrompt)
    {
        var argument = TeamsHandOff.StartArgument(idlePrompt, TaskLine);

        var commandLine = ClaudeSessionHost.BuildCommandLine(null, @"C:\claude.exe", null, argument);

        Assert.DoesNotContain('"', TaskLine);
        var script = commandLine.Substring(commandLine.IndexOf("-Command \"", StringComparison.Ordinal) + "-Command \"".Length);
        Assert.Equal(1, script.Count(c => c == '"'));
        Assert.EndsWith("\"", commandLine);
        Assert.Contains(idlePrompt == null ? $" '{TaskLine}'" : $" --continue '{TaskLine}'", commandLine);
    }

    [Fact]
    public void EndsClaude_False_ForClear()
    {
        Assert.False(TeamsHandOff.EndsClaude(new ClaudeHookEvent("SessionEnd", null, null, null, Reason: "clear")));
    }

    [Fact]
    public void EndsClaude_False_WhenReasonMissing()
    {
        Assert.False(TeamsHandOff.EndsClaude(new ClaudeHookEvent("SessionEnd", null, null, null)));
    }

    [Fact]
    public void EndsClaude_True_ForPromptInputExit()
    {
        Assert.True(TeamsHandOff.EndsClaude(new ClaudeHookEvent("SessionEnd", null, null, null, Reason: "prompt_input_exit")));
        Assert.False(TeamsHandOff.EndsClaude(new ClaudeHookEvent("Stop", null, null, null, Reason: "prompt_input_exit")));
    }
}
