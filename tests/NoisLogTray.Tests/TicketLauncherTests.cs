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
    public void GetClaudeProjectDirectoryName_IgnoresATrailingSeparator_LikeClaudesWorkingDirectory()
    {
        Assert.Equal("C--Project-Backend-DotNet", TicketLauncher.GetClaudeProjectDirectoryName(@"C:\Project\Backend-DotNet\"));
        Assert.Equal("C--", TicketLauncher.GetClaudeProjectDirectoryName(@"C:\"));
        Assert.True(TicketLauncher.IsTranscriptOfFolder(@"C:\Users\me\.claude\projects\C--Project-Backend-DotNet\0b5f2b8e.jsonl",
            @"C:\Project\Backend-DotNet\"));
    }

    [Theory]
    [InlineData(@"C:\Users\me\.claude\projects\C--Project-Backend-DotNet\0b5f2b8e.jsonl", true)]
    [InlineData(@"C:\Users\me\.claude\projects\c--project-backend-dotnet\0b5f2b8e.jsonl", true)]
    [InlineData(@"C:\Users\me\.claude\projects\C--Project-Backend-DotNet-src\0b5f2b8e.jsonl", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsTranscriptOfFolder_MatchesTheFoldersProjectDirectory(string? transcriptPath, bool expected)
    {
        Assert.Equal(expected, TicketLauncher.IsTranscriptOfFolder(transcriptPath, @"C:\Project\Backend-DotNet"));
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

    [Fact]
    public void GetResumeArgument_ResumesTheSession_WhenItsTranscriptExists()
    {
        var projectsRoot = Directory.CreateTempSubdirectory().FullName;
        var folderPath = @"C:\Project\Backend-DotNet";
        var sessionId = "0b5f2b8e-3f3c-4a59-9a43-1d2f7c6e8a10";
        var sessionDirectory = Directory.CreateDirectory(Path.Combine(projectsRoot, TicketLauncher.GetClaudeProjectDirectoryName(folderPath)));
        File.WriteAllText(Path.Combine(sessionDirectory.FullName, $"{sessionId}.jsonl"), "{}");

        Assert.Equal($"--resume {sessionId}", TicketLauncher.GetResumeArgument(folderPath, sessionId, projectsRoot));

        Directory.Delete(projectsRoot, true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    [InlineData("0b5f2b8e-3f3c-4a59-9a43-1d2f7c6e8a10\" --dangerously-skip-permissions")]
    [InlineData("1c6a3c9f-0000-4000-8000-000000000000")]
    public void GetResumeArgument_ReturnsNull_WhenTheIdIsInvalidOrHasNoTranscript(string? sessionId)
    {
        var projectsRoot = Directory.CreateTempSubdirectory().FullName;
        var folderPath = @"C:\Project\Backend-DotNet";
        var sessionDirectory = Directory.CreateDirectory(Path.Combine(projectsRoot, TicketLauncher.GetClaudeProjectDirectoryName(folderPath)));
        File.WriteAllText(Path.Combine(sessionDirectory.FullName, "0b5f2b8e-3f3c-4a59-9a43-1d2f7c6e8a10.jsonl"), "{}");

        Assert.Null(TicketLauncher.GetResumeArgument(folderPath, sessionId, projectsRoot));

        Directory.Delete(projectsRoot, true);
    }

    [Theory]
    [InlineData("CLAUDECODE")]
    [InlineData("CLAUDE_CODE_CHILD_SESSION")]
    [InlineData("CLAUDE_CODE_SESSION_ID")]
    [InlineData("ANTHROPIC_BASE_URL")]
    [InlineData("NO_COLOR")]
    [InlineData("AI_AGENT")]
    [InlineData("DISABLE_AUTOUPDATER")]
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

    [Fact]
    public void VsCodeStartInfo_PassesThePathThroughAVariable_SoCmdKeepsItLiteral()
    {
        // A Jira attachment may be named anything; on the cmd line itself %USERNAME% would be expanded.
        const string path = @"C:\docs\MDP-1\%USERNAME% a & b.md";

        var startInfo = TicketLauncher.VsCodeStartInfo(path);

        Assert.Equal("/c code \"%NOIS_VSCODE_TARGET%\"", startInfo.Arguments);
        Assert.Equal(path, startInfo.Environment["NOIS_VSCODE_TARGET"]);
        Assert.DoesNotContain(startInfo.Environment.Keys, TicketLauncher.IsClaudeSessionVariable);
    }

    [Fact]
    public void VsCodeStartInfo_GoesToTheLine_WhenOneIsGiven()
    {
        var startInfo = TicketLauncher.VsCodeStartInfo(@"C:\wt\UI\a.cs", 42);

        Assert.Equal("/c code -g \"%NOIS_VSCODE_TARGET%\"", startInfo.Arguments);
        Assert.Equal(@"C:\wt\UI\a.cs:42", startInfo.Environment["NOIS_VSCODE_TARGET"]);
    }
}
