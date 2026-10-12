namespace NoisLogTray.Tests;

// Runs real git against a throwaway clone; the ticket branch MDP-1 is cut from main.
public class GitChangesTests : IDisposable
{
    private const string TicketKey = "MDP-1";
    private readonly GitTestRepo _repo = new();

    public void Dispose()
    {
        _repo.Dispose();
    }

    [Fact]
    public void List_OnTicketBranch_ReportsCommittedUncommittedAndUntracked()
    {
        StartTicketBranch();
        _repo.WriteFile("committed.cs", "committed");
        _repo.Commit("add committed.cs");
        _repo.WriteFile("README.md", "edited");
        _repo.WriteFile("untracked.cs", "untracked");

        var set = GitChanges.List(_repo.RepoPath, TicketKey);

        Assert.NotNull(set);
        Assert.True(set.IsWorkingTree);
        Assert.Equal(
            new[]
            {
                new FileChange("committed.cs", FileChangeStatus.Added),
                new FileChange("README.md", FileChangeStatus.Modified),
                new FileChange("untracked.cs", FileChangeStatus.Added),
            },
            set.Files);
    }

    [Fact]
    public void List_OnDevBranch_ListsOnlyTicketCommits()
    {
        StartTicketBranch();
        _repo.WriteFile("ticket.cs", "ticket");
        _repo.Commit("add ticket.cs");
        _repo.Git("checkout", "-b", $"{TicketKey}-dev");
        _repo.WriteFile("development.cs", "development");
        _repo.Commit("add development.cs");

        var set = GitChanges.List(_repo.RepoPath, TicketKey);

        Assert.NotNull(set);
        Assert.False(set.IsWorkingTree);
        Assert.Equal($"refs/heads/{TicketKey}", set.Tip);
        Assert.Equal(new[] { new FileChange("ticket.cs", FileChangeStatus.Added) }, set.Files);
    }

    [Fact]
    public void List_OnDevBranch_IgnoresUncommittedEdits()
    {
        StartTicketBranch();
        _repo.WriteFile("ticket.cs", "ticket");
        _repo.Commit("add ticket.cs");
        _repo.Git("checkout", "-b", $"{TicketKey}-dev");
        _repo.WriteFile("README.md", "edited");
        _repo.WriteFile("untracked.cs", "untracked");

        var set = GitChanges.List(_repo.RepoPath, TicketKey);

        Assert.NotNull(set);
        Assert.Equal(new[] { new FileChange("ticket.cs", FileChangeStatus.Added) }, set.Files);
    }

    [Fact]
    public void List_ReportsDeletedFile()
    {
        StartTicketBranch();
        _repo.Git("rm", "README.md");
        _repo.Commit("remove README.md");

        var set = GitChanges.List(_repo.RepoPath, TicketKey);

        Assert.NotNull(set);
        Assert.Equal(new[] { new FileChange("README.md", FileChangeStatus.Deleted) }, set.Files);
    }

    [Fact]
    public void List_HandlesPathsWithSpacesAndUnicode()
    {
        StartTicketBranch();
        _repo.WriteFile("thư mục/tệp mới.cs", "committed");
        _repo.Commit("add a file with spaces and unicode");
        _repo.WriteFile("thư mục/tệp chưa thêm.cs", "untracked");

        var set = GitChanges.List(_repo.RepoPath, TicketKey);

        Assert.NotNull(set);
        Assert.Equal(
            new[]
            {
                new FileChange("thư mục/tệp chưa thêm.cs", FileChangeStatus.Added),
                new FileChange("thư mục/tệp mới.cs", FileChangeStatus.Added),
            },
            set.Files);
    }

    [Fact]
    public void List_SkipsLocalDockerCompose()
    {
        _repo.WriteFile("src/docker-compose.yml", "original");
        _repo.Commit("add docker-compose.yml");
        _repo.Git("push", "origin", "main");
        StartTicketBranch();
        _repo.WriteFile("src/docker-compose.yml", "local edit");

        var set = GitChanges.List(_repo.RepoPath, TicketKey);

        Assert.NotNull(set);
        Assert.Empty(set.Files);
    }

    [Fact]
    public void List_ReturnsNull_WhenOriginMainMissing()
    {
        StartTicketBranch();
        _repo.Git("remote", "remove", "origin");

        var set = GitChanges.List(_repo.RepoPath, TicketKey);

        Assert.Null(set);
    }

    [Fact]
    public void ReadOriginal_ReturnsNull_ForBinary()
    {
        _repo.WriteFile("data.bin", "a\0b");
        _repo.Commit("add data.bin");
        _repo.Git("push", "origin", "main");
        StartTicketBranch();
        _repo.WriteFile("data.bin", "a\0c");
        var set = GitChanges.List(_repo.RepoPath, TicketKey);
        Assert.NotNull(set);
        var file = Assert.Single(set.Files);

        var original = GitChanges.ReadOriginal(_repo.RepoPath, set, file);

        Assert.Null(original);
    }

    [Fact]
    public void ReadOriginalAndModified_ReturnBothSidesAsUtf8_WhenTheFileHasUnicodeText()
    {
        _repo.WriteFile("ghi chú.txt", "bản gốc");
        _repo.Commit("add ghi chú.txt");
        _repo.Git("push", "origin", "main");
        StartTicketBranch();
        _repo.WriteFile("ghi chú.txt", "bản sửa");
        var set = GitChanges.List(_repo.RepoPath, TicketKey);
        Assert.NotNull(set);
        var file = Assert.Single(set.Files);

        Assert.Equal("bản gốc", GitChanges.ReadOriginal(_repo.RepoPath, set, file));
        Assert.Equal("bản sửa", GitChanges.ReadModified(_repo.RepoPath, set, file));
    }

    [Fact]
    public void List_WithoutTicketBranch_ComparesHeadWithItsWorkingTree()
    {
        _repo.WriteFile("untracked.cs", "untracked");

        var set = GitChanges.List(_repo.RepoPath, TicketKey);

        Assert.NotNull(set);
        Assert.True(set.IsWorkingTree);
        Assert.Equal("HEAD", set.Tip);
        Assert.Equal(new[] { new FileChange("untracked.cs", FileChangeStatus.Added) }, set.Files);
    }

    [Fact]
    public void List_ReportsOneModifiedFile_WhenAPathIsDeletedFromIndexAndUntracked()
    {
        StartTicketBranch();
        _repo.Git("rm", "--cached", "README.md");

        var set = GitChanges.List(_repo.RepoPath, TicketKey);

        Assert.NotNull(set);
        Assert.Equal(new[] { new FileChange("README.md", FileChangeStatus.Modified) }, set.Files);
    }

    [Fact]
    public void ReadModified_OnDevBranch_ReadsTheTicketBranchNotTheWorkingFile()
    {
        StartTicketBranch();
        _repo.WriteFile("README.md", "ticket");
        _repo.Commit("edit README.md");
        _repo.Git("checkout", "-b", $"{TicketKey}-dev");
        _repo.WriteFile("README.md", "uncommitted on dev");
        var set = GitChanges.List(_repo.RepoPath, TicketKey);
        Assert.NotNull(set);
        var file = Assert.Single(set.Files);

        Assert.Equal("ticket", GitChanges.ReadModified(_repo.RepoPath, set, file));
    }

    [Fact]
    public void ReadModified_ReturnsEmpty_ForDeletedFile()
    {
        StartTicketBranch();
        _repo.Git("rm", "README.md");
        _repo.Commit("remove README.md");
        var set = GitChanges.List(_repo.RepoPath, TicketKey);
        Assert.NotNull(set);
        var file = Assert.Single(set.Files);

        Assert.Equal("", GitChanges.ReadModified(_repo.RepoPath, set, file));
        Assert.Equal("seed", GitChanges.ReadOriginal(_repo.RepoPath, set, file));
    }

    [Fact]
    public void ReadOriginal_ReturnsNull_ForOversizedBlob()
    {
        _repo.WriteFile("big.txt", new string('x', GitChanges.MaxChars + 1));
        _repo.Commit("add big.txt");
        _repo.Git("push", "origin", "main");
        StartTicketBranch();
        _repo.WriteFile("big.txt", "small now");
        var set = GitChanges.List(_repo.RepoPath, TicketKey);
        Assert.NotNull(set);
        var file = Assert.Single(set.Files);

        Assert.Null(GitChanges.ReadOriginal(_repo.RepoPath, set, file));
        Assert.Equal("small now", GitChanges.ReadModified(_repo.RepoPath, set, file));
    }

    private void StartTicketBranch()
    {
        _repo.Git("checkout", "-b", TicketKey);
    }
}
