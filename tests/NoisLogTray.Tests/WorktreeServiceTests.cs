using System.Diagnostics;

namespace NoisLogTray.Tests;

// Runs real git against a throwaway clone whose origin is a local bare repo,
// so `git fetch origin` works offline.
public class WorktreeServiceTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly string _repoPath;

    public WorktreeServiceTests()
    {
        var originPath = Path.Combine(_directory.Path, "origin.git");
        _repoPath = Path.Combine(_directory.Path, "repo");

        RunGit(_directory.Path, "init", "--bare", "--initial-branch=main", originPath);
        RunGit(_directory.Path, "clone", originPath, _repoPath);
        RunGit(_repoPath, "checkout", "-b", "main");
        File.WriteAllText(Path.Combine(_repoPath, "README.md"), "seed");
        RunGit(_repoPath, "add", "README.md");
        RunGit(_repoPath, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-m", "seed");
        RunGit(_repoPath, "push", "origin", "main");
    }

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact]
    public void TryCreate_CreatesWorktreeOnNewBranch_WhenBranchDoesNotExist()
    {
        var worktreePath = Path.Combine(_repoPath, ".claude", "worktrees", "MDP-1");

        var isCreated = WorktreeService.TryCreate("MDP-1", _repoPath, worktreePath);

        Assert.True(isCreated);
        Assert.Equal("MDP-1", RunGit(worktreePath, "branch", "--show-current"));
    }

    [Fact]
    public void TryCreate_ReusesExistingBranch_WhenBranchExistsWithoutWorktree()
    {
        RunGit(_repoPath, "branch", "MDP-2");
        var worktreePath = Path.Combine(_repoPath, ".claude", "worktrees", "MDP-2");

        var isCreated = WorktreeService.TryCreate("MDP-2", _repoPath, worktreePath);

        Assert.True(isCreated);
        Assert.Equal("MDP-2", RunGit(worktreePath, "branch", "--show-current"));
    }

    [Fact]
    public void TryCreate_ReportsFetchThenCreate_WhileRunning()
    {
        var progress = new RecordingProgress<StartStage>();
        var worktreePath = Path.Combine(_repoPath, ".claude", "worktrees", "MDP-4");

        WorktreeService.TryCreate("MDP-4", _repoPath, worktreePath, progress);

        Assert.Equal(new[] { StartStage.Fetching, StartStage.CreatingWorktree }, progress.Reports);
    }

    [Fact]
    public void TryCreate_ReturnsFalse_WhenBranchIsAlreadyCheckedOutInAnotherWorktree()
    {
        var firstWorktreePath = Path.Combine(_repoPath, ".claude", "worktrees", "MDP-3");
        WorktreeService.TryCreate("MDP-3", _repoPath, firstWorktreePath);
        var secondWorktreePath = Path.Combine(_repoPath, ".claude", "worktrees", "MDP-3-copy");

        var isCreated = WorktreeService.TryCreate("MDP-3", _repoPath, secondWorktreePath);

        Assert.False(isCreated);
    }

    [Fact]
    public void TryCreate_ReturnsFalse_WhenFetchFails()
    {
        RunGit(_repoPath, "remote", "set-url", "origin", Path.Combine(_directory.Path, "missing.git"));
        var progress = new RecordingProgress<StartStage>();
        var worktreePath = Path.Combine(_repoPath, ".claude", "worktrees", "MDP-5");

        var isCreated = WorktreeService.TryCreate("MDP-5", _repoPath, worktreePath, progress);

        Assert.False(isCreated);
        Assert.Equal(new[] { StartStage.Fetching }, progress.Reports);
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
        }

        return output.Trim();
    }
}
