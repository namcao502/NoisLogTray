namespace NoisLogTray.Tests;

public class ClaudeFoldersTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("NoisLogTray.ClaudeFolders").FullName;

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    [Fact]
    public void MenuEntries_ListsTheDevRepoFirst_ThenExistingRecentFoldersOnce()
    {
        var devRepo = Directory.CreateDirectory(Path.Combine(_root, "Backend-DotNet")).FullName;
        var app = Directory.CreateDirectory(Path.Combine(_root, "NoisLogTray")).FullName;
        var missing = Path.Combine(_root, "Gone");

        var entries = ClaudeFolders.MenuEntries(devRepo, new[] { app + Path.DirectorySeparatorChar, missing, devRepo.ToUpperInvariant() });

        Assert.Equal(new[] { devRepo, app }, entries);
    }

    [Fact]
    public void WithRecent_MovesTheFolderToTheFront_AndKeepsAtMostSix()
    {
        var recents = Enumerable.Range(1, 6).Select(folderNumber => $@"C:\Folder{folderNumber}").ToList();

        var updated = ClaudeFolders.WithRecent(recents, @"c:\folder3\");

        Assert.Equal(new[] { @"c:\folder3", @"C:\Folder1", @"C:\Folder2", @"C:\Folder4", @"C:\Folder5", @"C:\Folder6" }, updated);
    }

    [Fact]
    public void WithRecent_DropsTheOldest_WhenANewFolderIsAdded()
    {
        var recents = Enumerable.Range(1, 6).Select(folderNumber => $@"C:\Folder{folderNumber}").ToList();

        var updated = ClaudeFolders.WithRecent(recents, @"C:\New");

        Assert.Equal(ClaudeFolders.MaxRecent, updated.Count);
        Assert.Equal(@"C:\New", updated[0]);
        Assert.DoesNotContain(@"C:\Folder6", updated);
    }
}
