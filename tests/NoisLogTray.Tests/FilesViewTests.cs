namespace NoisLogTray.Tests;

public class FilesViewTests
{
    [Fact]
    public void FullPath_ReturnsTheListedFile_AndNullOtherwise()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoisLogTray.Tests", "files");
        var roots = new Dictionary<string, string> { ["docs"] = root };
        var files = new HashSet<(string Group, string Path)> { ("docs", "a/b.md") };

        Assert.Equal(Path.Combine(root, "a", "b.md"), FilesView.FullPath(roots, files, "docs", "a/b.md"));
        Assert.Null(FilesView.FullPath(roots, files, "other", "a/b.md"));
        Assert.Null(FilesView.FullPath(roots, files, "docs", "../x"));
        Assert.Null(FilesView.FullPath(roots, files, "docs", "c.md"));
    }

    [Fact]
    public void RootsFor_PutsTheDocsFolderAndEachWorktreeCopyOfIt_BeforeTheWorktrees()
    {
        var worktrees = new[]
        {
            new TicketWorktree("Backend-DotNet", @"C:\dev\.claude\worktrees\MDP-1"),
            new TicketWorktree("Portal-Angular", @"C:\Portal-Angular\.claude\worktrees\MDP-1"),
        };

        var roots = FilesView.RootsFor("MDP-1", @"C:\dev\docs\MDP-1", worktrees);

        Assert.Equal(new[]
        {
            ("docs", @"C:\dev\docs\MDP-1"),
            ("docs", @"C:\dev\.claude\worktrees\MDP-1\docs\MDP-1"),
            ("docs", @"C:\Portal-Angular\.claude\worktrees\MDP-1\docs\MDP-1"),
            ("Backend-DotNet", @"C:\dev\.claude\worktrees\MDP-1"),
            ("Portal-Angular", @"C:\Portal-Angular\.claude\worktrees\MDP-1"),
        }, roots);
    }

    [Fact]
    public void RootsFor_HasNoDocsRoot_WhenTheTicketHasNoDocs()
    {
        var roots = FilesView.RootsFor("MDP-1", null, new[] { new TicketWorktree("Backend-DotNet", @"C:\wt") });

        Assert.Equal(new[] { ("Backend-DotNet", @"C:\wt") }, roots);
    }

    [Theory]
    [InlineData(@"C:\dev\docs\MDP-1\attachments\a.png", "docs", "attachments/a.png")]
    [InlineData(@"C:\DEV\Docs\MDP-1\plan.md", "docs", "plan.md")]
    [InlineData(@"C:\wt\docs\MDP-1\plan.md", "docs", "plan.md")]
    [InlineData(@"C:\wt\src\Foo.cs", "Backend-DotNet", "src/Foo.cs")]
    public void Locate_GivesTheGroupAndListPath_OfTheFirstRootHoldingTheFile(string fullPath, string group, string path)
    {
        var roots = new[] { ("docs", @"C:\dev\docs\MDP-1"), ("docs", @"C:\wt\docs\MDP-1"), ("Backend-DotNet", @"C:\wt") };

        Assert.Equal((group, path), FilesView.Locate(fullPath, roots));
    }

    [Fact]
    public void ShowsSameContent_TrueForTheSameFile_OrAByteEqualCopy()
    {
        using var temp = new TempDirectory();
        var devRepoCopy = Path.Combine(temp.Path, "dev-plan.md");
        var worktreeCopy = Path.Combine(temp.Path, "wt-plan.md");
        File.WriteAllText(devRepoCopy, "# Plan\nline 2");
        File.WriteAllText(worktreeCopy, "# Plan\nline 2");

        Assert.True(FilesView.ShowsSameContent(devRepoCopy, devRepoCopy.ToUpperInvariant()));
        Assert.True(FilesView.ShowsSameContent(devRepoCopy, worktreeCopy));
    }

    [Fact]
    public void ShowsSameContent_FalseWhenTheCopiesDiffer_OrOneIsMissing()
    {
        using var temp = new TempDirectory();
        var devRepoCopy = Path.Combine(temp.Path, "dev-plan.md");
        var worktreeCopy = Path.Combine(temp.Path, "wt-plan.md");
        File.WriteAllText(devRepoCopy, "# Plan\nline 2");
        File.WriteAllText(worktreeCopy, "# Plan\nline X");

        Assert.False(FilesView.ShowsSameContent(devRepoCopy, worktreeCopy));
        Assert.False(FilesView.ShowsSameContent(Path.Combine(temp.Path, "missing.md"), worktreeCopy));
    }

    [Theory]
    [InlineData(@"C:\wt2\src\Foo.cs")]
    [InlineData(@"C:\other\Foo.cs")]
    [InlineData(@"D:\wt\src\Foo.cs")]
    [InlineData(@"C:\wt")]
    public void Locate_ReturnsNull_OutsideEveryRoot(string fullPath)
    {
        Assert.Null(FilesView.Locate(fullPath, new[] { ("Backend-DotNet", @"C:\wt") }));
    }
}
