namespace NoisLogTray.Tests;

public class TicketFilesTests
{
    private const string TicketKey = "MDP-1";

    [Fact]
    public void DocsPath_PrefersTheDevRepo_ThenTheWorktree_ElseNull()
    {
        using var directory = new TempDirectory();
        Assert.Null(TicketFiles.DocsPath(directory.Path, TicketKey));

        var worktreeDocs = Path.Combine(TicketWorktrees.BackendPath(directory.Path, TicketKey), "docs", TicketKey);
        Directory.CreateDirectory(worktreeDocs);
        Assert.Equal(worktreeDocs, TicketFiles.DocsPath(directory.Path, TicketKey));

        var devRepoDocs = Path.Combine(directory.Path, "docs", TicketKey);
        Directory.CreateDirectory(devRepoDocs);
        Assert.Equal(devRepoDocs, TicketFiles.DocsPath(directory.Path, TicketKey));
    }

    [Fact]
    public void ListDocs_ReturnsRelativeSlashPaths_SortedByName()
    {
        using var directory = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "attachments"));
        File.WriteAllText(Path.Combine(directory.Path, "plan.md"), "plan");
        File.WriteAllText(Path.Combine(directory.Path, "attachments", "a.png"), "png");

        var files = TicketFiles.ListDocs(directory.Path);

        Assert.Equal(new[] { "attachments/a.png", "plan.md" }, files);
    }

    [Fact]
    public void ListWorktree_ListsTrackedAndUntracked_ButNotIgnoredOrDeleted()
    {
        using var repo = new GitTestRepo();
        repo.WriteFile(".gitignore", "bin/\n");
        repo.WriteFile("gone.cs", "gone");
        repo.WriteFile("kept.cs", "kept");
        repo.Commit("add files");

        // Created after the last Commit, which runs `git add -A` and would track sub as a gitlink.
        repo.WriteFile("bin/x.dll", "dll");
        File.Delete(Path.Combine(repo.RepoPath, "gone.cs"));
        repo.WriteFile("untracked.cs", "untracked");
        repo.WriteFile("sub/inner.cs", "inner");
        var subPath = Path.Combine(repo.RepoPath, "sub");
        GitTestRepo.RunGit(subPath, "init");
        GitTestRepo.RunGit(subPath, "add", "-A");
        GitTestRepo.RunGit(subPath, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-m", "inner");

        var files = TicketFiles.ListWorktree(repo.RepoPath);

        Assert.Equal(new[] { ".gitignore", "kept.cs", "README.md", "untracked.cs" }, files);
    }

    [Fact]
    public void ListWorktree_ReturnsNull_WhenNotARepo()
    {
        using var directory = new TempDirectory();

        Assert.Null(TicketFiles.ListWorktree(directory.Path));
    }

    [Fact]
    public void Read_ClassifiesTextMarkdownImageBinaryAndTooLarge()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "a.cs"), "class A {}");
        File.WriteAllText(Path.Combine(directory.Path, "b.MD"), "# title");
        File.WriteAllBytes(Path.Combine(directory.Path, "c.png"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(directory.Path, "d.bin"), "a\0b");
        File.WriteAllText(Path.Combine(directory.Path, "e.txt"), new string('x', GitChanges.MaxChars + 1));

        var text = TicketFiles.Read(Path.Combine(directory.Path, "a.cs"));
        var markdown = TicketFiles.Read(Path.Combine(directory.Path, "b.MD"));
        var image = TicketFiles.Read(Path.Combine(directory.Path, "c.png"));
        var binary = TicketFiles.Read(Path.Combine(directory.Path, "d.bin"));
        var tooLarge = TicketFiles.Read(Path.Combine(directory.Path, "e.txt"));

        Assert.Equal(new TicketFileContent(TicketFileKind.Text, "class A {}"), text);
        Assert.Equal(new TicketFileContent(TicketFileKind.Markdown, "# title"), markdown);
        Assert.Equal(TicketFileKind.Image, image.Kind);
        Assert.StartsWith("data:image/png;base64,", image.Content);
        Assert.Equal(new TicketFileContent(TicketFileKind.Binary, null), binary);
        Assert.Equal(new TicketFileContent(TicketFileKind.TooLarge, null), tooLarge);
    }
}
