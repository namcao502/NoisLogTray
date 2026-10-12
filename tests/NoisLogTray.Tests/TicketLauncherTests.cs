using System.IO;

namespace NoisLogTray.Tests;

public class TicketLauncherTests
{
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
