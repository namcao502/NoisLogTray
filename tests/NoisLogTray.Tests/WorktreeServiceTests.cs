namespace NoisLogTray.Tests;

// Runs real git against a throwaway clone whose origin is a local bare repo,
// so `git fetch origin` works offline.
public class WorktreeServiceTests : IDisposable
{
    private readonly GitTestRepo _repo = new();

    public void Dispose()
    {
        _repo.Dispose();
    }

    [Fact]
    public void TryCreate_CreatesWorktreeOnNewBranch_WhenBranchDoesNotExist()
    {
        var worktreePath = Path.Combine(_repo.RepoPath, ".claude", "worktrees", "MDP-1");

        var isCreated = WorktreeService.TryCreate("MDP-1", _repo.RepoPath, worktreePath);

        Assert.True(isCreated);
        Assert.Equal("MDP-1", GitTestRepo.RunGit(worktreePath, "branch", "--show-current"));
    }

    [Fact]
    public void TryCreate_ReusesExistingBranch_WhenBranchExistsWithoutWorktree()
    {
        _repo.Git("branch", "MDP-2");
        var worktreePath = Path.Combine(_repo.RepoPath, ".claude", "worktrees", "MDP-2");

        var isCreated = WorktreeService.TryCreate("MDP-2", _repo.RepoPath, worktreePath);

        Assert.True(isCreated);
        Assert.Equal("MDP-2", GitTestRepo.RunGit(worktreePath, "branch", "--show-current"));
    }

    [Fact]
    public void TryCreate_ReportsFetchThenCreate_WhileRunning()
    {
        var progress = new RecordingProgress<StartStage>();
        var worktreePath = Path.Combine(_repo.RepoPath, ".claude", "worktrees", "MDP-4");

        WorktreeService.TryCreate("MDP-4", _repo.RepoPath, worktreePath, progress);

        Assert.Equal(new[] { StartStage.Fetching, StartStage.CreatingWorktree }, progress.Reports);
    }

    [Fact]
    public void TryCreate_ReturnsFalse_WhenBranchIsAlreadyCheckedOutInAnotherWorktree()
    {
        var firstWorktreePath = Path.Combine(_repo.RepoPath, ".claude", "worktrees", "MDP-3");
        WorktreeService.TryCreate("MDP-3", _repo.RepoPath, firstWorktreePath);
        var secondWorktreePath = Path.Combine(_repo.RepoPath, ".claude", "worktrees", "MDP-3-copy");

        var isCreated = WorktreeService.TryCreate("MDP-3", _repo.RepoPath, secondWorktreePath);

        Assert.False(isCreated);
    }

    [Fact]
    public void TryCreate_ReturnsFalse_WhenFetchFails()
    {
        _repo.Git("remote", "set-url", "origin", Path.Combine(_repo.RootPath, "missing.git"));
        var progress = new RecordingProgress<StartStage>();
        var worktreePath = Path.Combine(_repo.RepoPath, ".claude", "worktrees", "MDP-5");

        var isCreated = WorktreeService.TryCreate("MDP-5", _repo.RepoPath, worktreePath, progress);

        Assert.False(isCreated);
        Assert.Equal(new[] { StartStage.Fetching }, progress.Reports);
    }
}
