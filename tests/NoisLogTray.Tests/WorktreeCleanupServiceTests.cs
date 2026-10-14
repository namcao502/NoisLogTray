using System.Diagnostics;

namespace NoisLogTray.Tests;

// Real git against a throwaway clone; worktrees sit under <repo>\.claude\worktrees like the dev repo's.
public class WorktreeCleanupServiceTests : IDisposable
{
    private const string TicketKey = "MDP-1";
    private static readonly Dictionary<string, TrackedPr> NoPrTracking = new();
    private static readonly HashSet<string> NoKeys = [];
    private static readonly DateTime OlderWriteUtc = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NewerWriteUtc = new(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);

    private readonly GitTestRepo _repo = new();

    public void Dispose()
    {
        _repo.Dispose();
    }

    [Fact]
    public void LoadRow_ReturnsReady_WhenMergedToMainAndOnlyLocalOnlyFileIsModified()
    {
        _repo.WriteFile("src/docker-compose.yml", "services: {}");
        _repo.Commit("add docker-compose.yml");
        _repo.Git("push", "origin", "main");
        var worktreePath = AddShippedWorktree();
        File.WriteAllText(Path.Combine(worktreePath, "src", "docker-compose.yml"), "services: { local: {} }");

        var row = WorktreeCleanupService.LoadRow(_repo.RepoPath, TicketKey, NoPrTracking, NoKeys, NoKeys, NoKeys, NoKeys);

        Assert.Equal(CleanupVerdict.Ready, row.Verdict);
        Assert.Equal(0, row.DirtyFiles);
        Assert.Equal(worktreePath, Assert.Single(row.Worktrees).Path);
    }

    [Fact]
    public void LoadRow_ReturnsCheck_WhenWorktreeHasUntrackedFile()
    {
        var worktreePath = AddShippedWorktree();
        File.WriteAllText(Path.Combine(worktreePath, "scratch.txt"), "notes");

        var row = WorktreeCleanupService.LoadRow(_repo.RepoPath, TicketKey, NoPrTracking, NoKeys, NoKeys, NoKeys, NoKeys);

        Assert.Equal(CleanupVerdict.Check, row.Verdict);
        Assert.Equal(1, row.DirtyFiles);
    }

    [Fact]
    public void LoadRow_ReturnsKeep_WhenTicketBranchHasUnpushedCommitWhileHeadIsOnDevBranch()
    {
        var worktreePath = AddWorktree(TicketKey);
        GitTestRepo.RunGit(worktreePath, "branch", $"{TicketKey}-dev");
        GitTestRepo.RunGit(worktreePath, "push", "origin", $"{TicketKey}-dev");
        File.WriteAllText(Path.Combine(worktreePath, "feature.cs"), "work");
        GitTestRepo.RunGit(worktreePath, "add", "-A");
        GitTestRepo.RunGit(worktreePath, "commit", "-m", "MDP-1 not pushed");
        GitTestRepo.RunGit(worktreePath, "checkout", $"{TicketKey}-dev");

        var row = WorktreeCleanupService.LoadRow(_repo.RepoPath, TicketKey, NoPrTracking, NoKeys, NoKeys, NoKeys, NoKeys);

        Assert.Equal(CleanupVerdict.Keep, row.Verdict);
    }

    [Fact]
    public void LoadRow_ReturnsCheck_WhenBranchHasNoCommitsOfItsOwn()
    {
        // A merge naming MDP-10 must not count as MDP-1 shipping.
        _repo.Git("checkout", "-b", "MDP-10");
        _repo.WriteFile("other.cs", "other");
        _repo.Commit("MDP-10 other");
        _repo.Git("checkout", "main");
        _repo.Git("merge", "--no-ff", "-m", "Merged PR 2: MDP-10 y", "MDP-10");
        _repo.Git("push", "origin", "main");
        AddWorktree(TicketKey);

        var row = WorktreeCleanupService.LoadRow(_repo.RepoPath, TicketKey, NoPrTracking, NoKeys, NoKeys, NoKeys, NoKeys);

        Assert.Equal(CleanupVerdict.Check, row.Verdict);
    }

    [Fact]
    public void LoadRow_ReturnsCheck_WhenTicketIsStarted()
    {
        AddShippedWorktree();
        var startedKeys = new HashSet<string> { TicketKey };

        var row = WorktreeCleanupService.LoadRow(_repo.RepoPath, TicketKey, NoPrTracking, startedKeys, NoKeys, NoKeys, NoKeys);

        Assert.Equal(CleanupVerdict.Check, row.Verdict);
    }

    [Fact]
    public void LoadRows_ReturnsMinedNoWorktreeRow_WhenOnlyTicketDocsExist()
    {
        _repo.WriteFile("docs/MDP-9/plan.md", "plan");
        _repo.WriteFile("docs/MDP-9/attachments/shot.png", new string('x', 100));
        _repo.WriteFile("docs/MDP-draft/notes.md", "not a ticket key");
        _repo.WriteFile("docs/local-config/.daily-knowledge-scan.log", "2026-10-14 09:00:00 Mining MDP-9 ...\n");

        var rows = WorktreeCleanupService.LoadRows(_repo.RepoPath, NoPrTracking, NoKeys, NoKeys);

        var row = Assert.Single(rows);
        Assert.Equal("MDP-9", row.TicketKey);
        Assert.Equal(CleanupVerdict.NoWorktree, row.Verdict);
        Assert.Empty(row.Worktrees);
        Assert.Empty(row.Reasons);
        Assert.True(row.IsMined);
        Assert.False(row.IsMerged);
        Assert.Equal(100, row.SlimBytes);
    }

    [Fact]
    public void LoadRows_MarksTicketMerged_WhenAMergeCommitOnOriginMainNamesIt()
    {
        AddShippedWorktree();

        var row = Assert.Single(WorktreeCleanupService.LoadRows(_repo.RepoPath, NoPrTracking, NoKeys, NoKeys));

        Assert.Equal(TicketKey, row.TicketKey);
        Assert.True(row.IsMerged);
    }

    [Fact]
    public void LoadRow_MarksTicketMerged_WhenItsTrackedDevPrCompleted()
    {
        _repo.WriteFile("docs/MDP-9/plan.md", "plan");
        var prTracking = new Dictionary<string, TrackedPr> { ["MDP-9|dev|MDP-9-dev"] = new("completed", 0) };

        var row = WorktreeCleanupService.LoadRow(_repo.RepoPath, "MDP-9", prTracking, NoKeys, NoKeys, NoKeys, NoKeys);

        Assert.True(row.IsMerged);
    }

    [Fact]
    public void Inspect_IgnoresScanFiles_WhenMainCheckoutScanLogIsNewer()
    {
        var worktreePath = AddShippedWorktree();
        var worktreeConfigPath = Path.Combine(worktreePath, "docs", "local-config");
        var mainConfigPath = Path.Combine(_repo.RepoPath, "docs", "local-config");
        WriteFile(Path.Combine(worktreeConfigPath, ".daily-knowledge-scan.log"), "old scan", OlderWriteUtc);
        WriteFile(Path.Combine(mainConfigPath, ".daily-knowledge-scan.log"), "old scan, new scan", NewerWriteUtc);
        WriteFile(Path.Combine(worktreeConfigPath, ".daily-knowledge-scan.retry"), "MDP-5 1", NewerWriteUtc);

        var facts = WorktreeCleanupService.Inspect(_repo.RepoPath, TicketKey, NoPrTracking, NoKeys, terminalRunning: false);

        Assert.Equal(0, facts.LocalConfigDiffs);
    }

    [Fact]
    public void Inspect_CountsLocalConfigFiles_WhenMissingInMainCheckoutOrNewerAndDifferent()
    {
        var worktreePath = AddShippedWorktree();
        var worktreeConfigPath = Path.Combine(worktreePath, "docs", "local-config");
        var mainConfigPath = Path.Combine(_repo.RepoPath, "docs", "local-config");
        WriteFile(Path.Combine(worktreeConfigPath, "tokens.env"), "TOKEN=new", NewerWriteUtc);
        WriteFile(Path.Combine(mainConfigPath, "tokens.env"), "TOKEN=old", OlderWriteUtc);
        WriteFile(Path.Combine(worktreeConfigPath, "src", "extra.json"), "{}", OlderWriteUtc);
        WriteFile(Path.Combine(worktreeConfigPath, "README.md"), "same", NewerWriteUtc);
        WriteFile(Path.Combine(mainConfigPath, "README.md"), "same", OlderWriteUtc);
        WriteFile(Path.Combine(worktreeConfigPath, "run-local.sh"), "older edit", OlderWriteUtc);
        WriteFile(Path.Combine(mainConfigPath, "run-local.sh"), "newer edit", NewerWriteUtc);

        var facts = WorktreeCleanupService.Inspect(_repo.RepoPath, TicketKey, NoPrTracking, NoKeys, terminalRunning: false);

        Assert.Equal(2, facts.LocalConfigDiffs);
    }

    [Fact]
    public void Inspect_ReportsBroken_WhenWorktreeIsNoLongerRegistered()
    {
        var worktreePath = AddShippedWorktree();
        File.Delete(Path.Combine(worktreePath, ".git"));
        _repo.Git("worktree", "prune");
        // Untracked in the main checkout: read there by mistake, it would count as dirty.
        _repo.WriteFile("main-only.txt", "main");

        var facts = WorktreeCleanupService.Inspect(_repo.RepoPath, TicketKey, NoPrTracking, NoKeys, terminalRunning: false);

        Assert.True(facts.IsBroken);
        Assert.Equal(0, facts.DirtyFiles);
    }

    [Fact]
    public void Inspect_ReportsOtherBranch_WhenWorktreeIsOnAnotherTicketsBranch()
    {
        var worktreePath = AddShippedWorktree();
        GitTestRepo.RunGit(worktreePath, "checkout", "-b", "MDP-2");

        var facts = WorktreeCleanupService.Inspect(_repo.RepoPath, TicketKey, NoPrTracking, NoKeys, terminalRunning: false);

        Assert.Equal("MDP-2", facts.OtherBranch);
        Assert.Equal(CleanupVerdict.Check, WorktreeCleanup.Classify(facts).Verdict);
    }

    [Fact]
    public void Inspect_ReadsPrFacts_WhenMainPrIsMergedAndDevPrIsOpen()
    {
        AddShippedWorktree();
        var prTracking = new Dictionary<string, TrackedPr>
        {
            [$"{TicketKey}|main|1"] = new TrackedPr("completed", 0),
            [$"{TicketKey}|dev|2"] = new TrackedPr("active", 0),
            ["MDP-10|main|3"] = new TrackedPr("active", 0),
        };

        var facts = WorktreeCleanupService.Inspect(_repo.RepoPath, TicketKey, prTracking, NoKeys, terminalRunning: false);

        Assert.True(facts.HasTrackedPrs);
        Assert.True(facts.MainPrMerged);
        Assert.True(facts.AnyPrCompleted);
        Assert.False(facts.AllPrsClosed);
        Assert.Equal(CleanupVerdict.Check, WorktreeCleanup.Classify(facts).Verdict);
    }

    [Fact]
    public void Inspect_ReportsInspectFailed_NotBroken_WhenGitCannotListWorktrees()
    {
        using var notARepo = new TempDirectory();
        Directory.CreateDirectory(TicketWorktrees.BackendPath(notARepo.Path, TicketKey));

        var facts = WorktreeCleanupService.Inspect(notARepo.Path, TicketKey, NoPrTracking, NoKeys, terminalRunning: false);

        Assert.True(facts.InspectFailed);
        Assert.False(facts.IsBroken);
        Assert.Equal(CleanupVerdict.Keep, WorktreeCleanup.Classify(facts).Verdict);
    }

    [Fact]
    public void Inspect_FindsWorktreeRegistered_WhenDevRepoPathGoesThroughJunction()
    {
        AddShippedWorktree();
        // git lists the junction's target, never the path as typed.
        var junctionRepoPath = Path.Combine(_repo.RootPath, "repo-junction");
        CreateJunction(junctionRepoPath, _repo.RepoPath);

        try
        {
            var facts = WorktreeCleanupService.Inspect(junctionRepoPath, TicketKey, NoPrTracking, NoKeys, terminalRunning: false);

            Assert.False(facts.IsBroken);
            Assert.True(facts.BranchMerged);
        }
        finally
        {
            // The link only: TempDirectory's recursive delete fails on a junction.
            Directory.Delete(junctionRepoPath);
        }
    }

    [Fact]
    public void Remove_CopiesNewerDocsToMainCheckout_BeforeRemovingWorktree()
    {
        var worktreePath = AddShippedWorktree();
        WriteFile(Path.Combine(worktreePath, "docs", TicketKey, "note.md"), "from worktree", NewerWriteUtc);
        WriteFile(Path.Combine(_repo.RepoPath, "docs", TicketKey, "note.md"), "from main", OlderWriteUtc);

        var error = WorktreeCleanupService.Remove(_repo.RepoPath, TicketKey);

        Assert.Null(error);
        Assert.False(Directory.Exists(worktreePath));
        Assert.Equal("from worktree", File.ReadAllText(Path.Combine(_repo.RepoPath, "docs", TicketKey, "note.md")));
    }

    [Fact]
    public void Remove_KeepsJunctionTarget_AndDeletesMergedBranch()
    {
        var worktreePath = AddShippedWorktree();
        var targetPath = Path.Combine(_repo.RootPath, "shared-modules");
        Directory.CreateDirectory(targetPath);
        File.WriteAllText(Path.Combine(targetPath, "sentinel.txt"), "keep me");
        CreateJunction(Path.Combine(worktreePath, "node_modules"), targetPath);

        var error = WorktreeCleanupService.Remove(_repo.RepoPath, TicketKey);

        Assert.Null(error);
        Assert.False(Directory.Exists(worktreePath));
        Assert.True(File.Exists(Path.Combine(targetPath, "sentinel.txt")));
        Assert.Equal("", _repo.Git("branch", "--list", TicketKey));
    }

    [Fact]
    public void Remove_DeletesFolder_WhenRegistrationWasPruned()
    {
        var worktreePath = AddShippedWorktree();
        File.Delete(Path.Combine(worktreePath, ".git"));
        _repo.Git("worktree", "prune");

        var error = WorktreeCleanupService.Remove(_repo.RepoPath, TicketKey);

        Assert.Null(error);
        Assert.False(Directory.Exists(worktreePath));
    }

    [Fact]
    public void RemoveChecked_ReturnsError_WhenMoreDirtyFilesThanShown()
    {
        var worktreePath = AddShippedWorktree();
        File.WriteAllText(Path.Combine(worktreePath, "scratch.txt"), "notes");

        var error = WorktreeCleanupService.RemoveChecked(_repo.RepoPath, TicketKey, NoPrTracking, NoKeys, false, dirtyFilesShown: 0);

        Assert.NotNull(error);
        Assert.True(Directory.Exists(worktreePath));
    }

    [Fact]
    public void RemoveChecked_RemovesWorktree_WhenDirtyFilesMatchWhatWasShown()
    {
        var worktreePath = AddShippedWorktree();
        File.WriteAllText(Path.Combine(worktreePath, "scratch.txt"), "notes");

        var error = WorktreeCleanupService.RemoveChecked(_repo.RepoPath, TicketKey, NoPrTracking, NoKeys, false, dirtyFilesShown: 1);

        Assert.Null(error);
        Assert.False(Directory.Exists(worktreePath));
    }

    [Fact]
    public void SlimDocs_DeletesTargetsInMainCheckoutAndInOtherTicketsWorktree()
    {
        var worktreePath = AddWorktree(TicketKey);
        WriteFile(Path.Combine(_repo.RepoPath, "docs", "MDP-9", "plan-review.md"), "review", OlderWriteUtc);
        WriteFile(Path.Combine(_repo.RepoPath, "docs", "MDP-9", "plan.md"), "plan", OlderWriteUtc);
        WriteFile(Path.Combine(worktreePath, "docs", "MDP-9", "attachments", "shot.png"), "png", OlderWriteUtc);
        WriteFile(Path.Combine(worktreePath, "docs", "MDP-9", "plan.md"), "plan", OlderWriteUtc);

        var error = WorktreeCleanupService.SlimDocs(_repo.RepoPath, "MDP-9");

        Assert.Null(error);
        Assert.False(File.Exists(Path.Combine(_repo.RepoPath, "docs", "MDP-9", "plan-review.md")));
        Assert.True(File.Exists(Path.Combine(_repo.RepoPath, "docs", "MDP-9", "plan.md")));
        Assert.False(Directory.Exists(Path.Combine(worktreePath, "docs", "MDP-9", "attachments")));
        Assert.True(File.Exists(Path.Combine(worktreePath, "docs", "MDP-9", "plan.md")));
    }

    [Fact]
    public void SlimDocs_ReturnsError_WhenTicketStillHasWorktree()
    {
        AddWorktree("MDP-9");
        WriteFile(Path.Combine(_repo.RepoPath, "docs", "MDP-9", "plan-review.md"), "review", OlderWriteUtc);

        var error = WorktreeCleanupService.SlimDocs(_repo.RepoPath, "MDP-9");

        Assert.NotNull(error);
        Assert.True(File.Exists(Path.Combine(_repo.RepoPath, "docs", "MDP-9", "plan-review.md")));
    }

    [Fact]
    public void Remove_ChangesNothing_WhenGitCannotListWorktrees()
    {
        using var notARepo = new TempDirectory();
        var worktreePath = TicketWorktrees.BackendPath(notARepo.Path, TicketKey);
        Directory.CreateDirectory(worktreePath);
        File.WriteAllText(Path.Combine(worktreePath, "work.cs"), "uncommitted");
        var junctionTargetPath = Path.Combine(notARepo.Path, "real-node-modules");
        Directory.CreateDirectory(junctionTargetPath);
        var junctionPath = Path.Combine(worktreePath, "node_modules");
        CreateJunction(junctionPath, junctionTargetPath);

        try
        {
            var error = WorktreeCleanupService.Remove(notARepo.Path, TicketKey);

            Assert.NotNull(error);
            Assert.True(File.Exists(Path.Combine(worktreePath, "work.cs")));
            Assert.True(Directory.Exists(junctionPath));
        }
        finally
        {
            // The link only: TempDirectory's recursive delete fails on a junction.
            if (Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath);
            }
        }
    }

    [Theory]
    [InlineData("..")]
    [InlineData(@"MDP-1\..\..")]
    public void RemoveAndSlimDocs_RefuseAnythingButATicketKey(string ticketKey)
    {
        Assert.NotNull(WorktreeCleanupService.Remove(_repo.RepoPath, ticketKey));
        Assert.NotNull(WorktreeCleanupService.SlimDocs(_repo.RepoPath, ticketKey));
    }

    private static void CreateJunction(string junctionPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", junctionPath, targetPath })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var mklink = Process.Start(startInfo)!;
        mklink.WaitForExit();
    }

    private string AddWorktree(string ticketKey)
    {
        var worktreePath = TicketWorktrees.BackendPath(_repo.RepoPath, ticketKey);
        _repo.Git("worktree", "add", worktreePath, "-b", ticketKey);
        return worktreePath;
    }

    // MDP-1 committed on its own branch, pushed, then merged into main the way Azure DevOps does.
    private string AddShippedWorktree()
    {
        var worktreePath = AddWorktree(TicketKey);
        File.WriteAllText(Path.Combine(worktreePath, "feature.cs"), "work");
        GitTestRepo.RunGit(worktreePath, "add", "-A");
        GitTestRepo.RunGit(worktreePath, "commit", "-m", "MDP-1 feature");
        GitTestRepo.RunGit(worktreePath, "push", "origin", TicketKey);
        _repo.Git("merge", "--no-ff", "-m", "Merged PR 1: MDP-1 x", TicketKey);
        _repo.Git("push", "origin", "main");
        return worktreePath;
    }

    private static void WriteFile(string path, string content, DateTime lastWriteUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
    }
}
