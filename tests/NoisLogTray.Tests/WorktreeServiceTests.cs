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
    public void Create_CreatesWorktreeOnNewBranch_WhenBranchDoesNotExist()
    {
        var worktreePath = Path.Combine(_repo.RepoPath, ".claude", "worktrees", "MDP-1");

        var result = WorktreeService.Create("MDP-1", _repo.RepoPath, worktreePath);

        Assert.Equal(WorktreeCreateResult.Created, result);
        Assert.Equal("MDP-1", GitTestRepo.RunGit(worktreePath, "branch", "--show-current"));
    }

    [Fact]
    public void Create_ReusesExistingBranch_WhenBranchExistsWithoutWorktree()
    {
        _repo.Git("branch", "MDP-2");
        var worktreePath = Path.Combine(_repo.RepoPath, ".claude", "worktrees", "MDP-2");

        var result = WorktreeService.Create("MDP-2", _repo.RepoPath, worktreePath);

        Assert.Equal(WorktreeCreateResult.Created, result);
        Assert.Equal("MDP-2", GitTestRepo.RunGit(worktreePath, "branch", "--show-current"));
    }

    [Fact]
    public void Create_ReportsFetchThenCreate_WhileRunning()
    {
        var progress = new RecordingProgress<StartStage>();
        var worktreePath = Path.Combine(_repo.RepoPath, ".claude", "worktrees", "MDP-4");

        WorktreeService.Create("MDP-4", _repo.RepoPath, worktreePath, progress);

        Assert.Equal(new[] { StartStage.Fetching, StartStage.CreatingWorktree }, progress.Reports);
    }

    [Fact]
    public void Create_ReturnsFailed_WhenBranchIsAlreadyCheckedOutInAnotherWorktree()
    {
        var firstWorktreePath = Path.Combine(_repo.RepoPath, ".claude", "worktrees", "MDP-3");
        WorktreeService.Create("MDP-3", _repo.RepoPath, firstWorktreePath);
        var secondWorktreePath = Path.Combine(_repo.RepoPath, ".claude", "worktrees", "MDP-3-copy");

        var result = WorktreeService.Create("MDP-3", _repo.RepoPath, secondWorktreePath);

        Assert.Equal(WorktreeCreateResult.Failed, result);
    }

    [Fact]
    public void Create_ReturnsFailed_WhenFetchFails()
    {
        _repo.Git("remote", "set-url", "origin", Path.Combine(_repo.RootPath, "missing.git"));
        var progress = new RecordingProgress<StartStage>();
        var worktreePath = Path.Combine(_repo.RepoPath, ".claude", "worktrees", "MDP-5");

        var result = WorktreeService.Create("MDP-5", _repo.RepoPath, worktreePath, progress);

        Assert.Equal(WorktreeCreateResult.Failed, result);
        Assert.Equal(new[] { StartStage.Fetching }, progress.Reports);
    }

    [Fact]
    public void Create_MergesOriginMain_WhenBranchIsOlderThanMain()
    {
        _repo.Git("branch", "MDP-6");
        AdvanceMain("README.md", "newer");
        var worktreePath = Path.Combine(_repo.RepoPath, ".claude", "worktrees", "MDP-6");

        var result = WorktreeService.Create("MDP-6", _repo.RepoPath, worktreePath);

        Assert.Equal(WorktreeCreateResult.Created, result);
        GitTestRepo.RunGit(worktreePath, "merge-base", "--is-ancestor", "origin/main", "HEAD");
    }

    [Fact]
    public void Create_RecreatesLocalBranchWithItsCommit_WhenOnlyOriginBranchIsLeft()
    {
        _repo.Git("checkout", "-b", "MDP-7");
        _repo.WriteFile("old.txt", "old");
        _repo.Commit("old work");
        var oldCommit = _repo.Git("rev-parse", "HEAD");
        _repo.Git("push", "origin", "MDP-7");
        _repo.Git("checkout", "main");
        _repo.Git("branch", "-D", "MDP-7");
        var worktreePath = Path.Combine(_repo.RepoPath, ".claude", "worktrees", "MDP-7");

        var result = WorktreeService.Create("MDP-7", _repo.RepoPath, worktreePath);

        Assert.Equal(WorktreeCreateResult.Created, result);
        Assert.Equal("MDP-7", GitTestRepo.RunGit(worktreePath, "branch", "--show-current"));
        GitTestRepo.RunGit(worktreePath, "merge-base", "--is-ancestor", oldCommit, "HEAD");
    }

    [Fact]
    public void Create_ReturnsMainNotMergedAndLeavesNoMergeHead_WhenMergeConflicts()
    {
        _repo.Git("checkout", "-b", "MDP-8");
        _repo.WriteFile("README.md", "branch side");
        _repo.Commit("branch edit");
        _repo.Git("checkout", "main");
        AdvanceMain("README.md", "main side");
        var worktreePath = Path.Combine(_repo.RepoPath, ".claude", "worktrees", "MDP-8");

        var result = WorktreeService.Create("MDP-8", _repo.RepoPath, worktreePath);

        Assert.Equal(WorktreeCreateResult.MainNotMerged, result);
        var mergeHeadPath = Path.GetFullPath(GitTestRepo.RunGit(worktreePath, "rev-parse", "--git-path", "MERGE_HEAD"), worktreePath);
        Assert.False(File.Exists(mergeHeadPath));
    }

    [Fact]
    public void Create_ReportsFetchCreateThenMerge_WhenBranchExists()
    {
        _repo.Git("branch", "MDP-9");
        var progress = new RecordingProgress<StartStage>();
        var worktreePath = Path.Combine(_repo.RepoPath, ".claude", "worktrees", "MDP-9");

        WorktreeService.Create("MDP-9", _repo.RepoPath, worktreePath, progress);

        Assert.Equal(new[] { StartStage.Fetching, StartStage.CreatingWorktree, StartStage.MergingMain }, progress.Reports);
    }

    private void AdvanceMain(string relativePath, string content)
    {
        _repo.WriteFile(relativePath, content);
        _repo.Commit("main moves on");
        _repo.Git("push", "origin", "main");
    }
}
