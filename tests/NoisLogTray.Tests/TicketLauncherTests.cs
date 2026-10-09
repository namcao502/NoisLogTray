using System.IO;
using System.Text.Json;

namespace NoisLogTray.Tests;

public class TicketLauncherTests
{
    [Fact]
    public void BuildClaudeTasksJson_OpensExtensionTerminalWithPrompt_OnFolderOpen()
    {
        using var doc = JsonDocument.Parse(TicketLauncher.BuildClaudeTasksJson("ship MDP-8961"));

        var input = doc.RootElement.GetProperty("inputs").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "claudeTerminal");
        Assert.Equal("command", input.GetProperty("type").GetString());
        Assert.Equal("claude-vscode.terminal.open", input.GetProperty("command").GetString());
        Assert.Equal("ship MDP-8961", input.GetProperty("args").GetString());

        var task = doc.RootElement.GetProperty("tasks")[0];
        Assert.Equal("folderOpen", task.GetProperty("runOptions").GetProperty("runOn").GetString());
        Assert.Contains("${input:", task.GetProperty("options").GetProperty("env").GetProperty("TASKWATCHER_CLAUDE").GetString());
    }

    [Fact]
    public void BuildClaudeTasksJson_DeletesItself_SoReopeningTheWorktreeDoesNotRunAgain()
    {
        using var doc = JsonDocument.Parse(TicketLauncher.BuildClaudeTasksJson("ship MDP-8961"));

        var args = doc.RootElement.GetProperty("tasks")[0].GetProperty("args")
            .EnumerateArray().Select(a => a.GetString()).ToList();

        Assert.Equal(new[] { "/d", "/c", "del", @".vscode\tasks.json" }, args);
    }

    [Fact]
    public void BuildClaudeTasksJson_OpensIdleClaude_WhenNoPrompt()
    {
        using var doc = JsonDocument.Parse(TicketLauncher.BuildClaudeTasksJson(null));

        var input = doc.RootElement.GetProperty("inputs").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "claudeTerminal");
        Assert.Equal("claude-vscode.terminal.open", input.GetProperty("command").GetString());
        Assert.False(input.TryGetProperty("args", out _));
    }

    [Fact]
    public void BuildClaudeTasksJson_JoinsAllGroupsBeforeOpeningClaude_SoRestoredEmptyGroupsGoAway()
    {
        using var doc = JsonDocument.Parse(TicketLauncher.BuildClaudeTasksJson(null));

        var inputs = doc.RootElement.GetProperty("inputs").EnumerateArray()
            .ToDictionary(i => i.GetProperty("id").GetString()!, i => i.GetProperty("command").GetString());
        Assert.Equal("workbench.action.joinAllGroups", inputs["joinEditorGroups"]);
        Assert.Equal("workbench.action.unlockEditorGroup", inputs["unlockEditorGroup"]);

        var env = doc.RootElement.GetProperty("tasks")[0].GetProperty("options").GetProperty("env")
            .GetProperty("TASKWATCHER_CLAUDE").GetString();
        Assert.Equal("${input:joinEditorGroups}${input:unlockEditorGroup}${input:claudeTerminal}", env);
    }

    [Fact]
    public void GetClaudeProjectDirectoryName_ReplacesEveryNonAlphanumeric_LikeClaudeCode()
    {
        var name = TicketLauncher.GetClaudeProjectDirectoryName(@"C:\Project\Backend-DotNet\.claude\worktrees\MDP-8961");

        Assert.Equal("C--Project-Backend-DotNet--claude-worktrees-MDP-8961", name);
    }

    [Fact]
    public void GetIdlePrompt_ReturnsContinue_WhenWorktreeHasSession()
    {
        var projectsRoot = Directory.CreateTempSubdirectory().FullName;
        var worktreePath = @"C:\Repo\.claude\worktrees\MDP-1";
        var sessionDirectory = Directory.CreateDirectory(Path.Combine(projectsRoot, TicketLauncher.GetClaudeProjectDirectoryName(worktreePath)));
        File.WriteAllText(Path.Combine(sessionDirectory.FullName, "session.jsonl"), "{}");

        Assert.Equal("--continue", TicketLauncher.GetIdlePrompt(worktreePath, projectsRoot));

        Directory.Delete(projectsRoot, true);
    }

    [Fact]
    public void GetIdlePrompt_ReturnsNull_WhenWorktreeHasNoSession()
    {
        var projectsRoot = Directory.CreateTempSubdirectory().FullName;
        var worktreePath = @"C:\Repo\.claude\worktrees\MDP-2";
        Directory.CreateDirectory(Path.Combine(projectsRoot, TicketLauncher.GetClaudeProjectDirectoryName(worktreePath)));

        Assert.Null(TicketLauncher.GetIdlePrompt(worktreePath, projectsRoot));
        Assert.Null(TicketLauncher.GetIdlePrompt(@"C:\Repo\.claude\worktrees\MDP-3", projectsRoot));

        Directory.Delete(projectsRoot, true);
    }

    [Theory]
    [InlineData("""{"version":"2.0.0","tasks":[{"label":"TaskWatcher: ship MDP-1"}]}""")]
    [InlineData("""{"version":"2.0.0","tasks":[{"label":"Resume Claude","command":"claude"}]}""")]
    public void IsTaskWatcherTaskFile_ReturnsTrue_ForFilesTaskWatcherWrote(string json)
    {
        Assert.True(TicketLauncher.IsTaskWatcherTaskFile(json));
    }

    [Theory]
    [InlineData("""{"version":"2.0.0","tasks":[{"label":"build"}]}""")]
    [InlineData("""{"version":"2.0.0","tasks":[{"label":"TaskWatcher: open Claude"},{"label":"build"}]}""")]
    [InlineData("""{"version":"2.0.0","tasks":[]}""")]
    [InlineData("{ not json")]
    public void IsTaskWatcherTaskFile_ReturnsFalse_ForUserOrUnreadableFiles(string json)
    {
        Assert.False(TicketLauncher.IsTaskWatcherTaskFile(json));
    }

    [Theory]
    [InlineData("CLAUDECODE")]
    [InlineData("CLAUDE_CODE_CHILD_SESSION")]
    [InlineData("CLAUDE_CODE_SESSION_ID")]
    [InlineData("ANTHROPIC_BASE_URL")]
    public void IsClaudeSessionVariable_ReturnsTrue_ForInheritedClaudeVariables(string name)
    {
        Assert.True(TicketLauncher.IsClaudeSessionVariable(name));
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("USERPROFILE")]
    public void IsClaudeSessionVariable_ReturnsFalse_ForOtherVariables(string name)
    {
        Assert.False(TicketLauncher.IsClaudeSessionVariable(name));
    }
}
