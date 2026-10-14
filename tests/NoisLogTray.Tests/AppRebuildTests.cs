namespace NoisLogTray.Tests;

public class AppRebuildTests
{
    [Fact]
    public void FindSourceRepo_ReturnsTheRepo_WhenRunningFromItsPublishFolder()
    {
        using var repo = new TempDirectory();
        var publishDirectory = Path.Combine(repo.Path, "bin", "publish", "win-x64");
        Directory.CreateDirectory(publishDirectory);
        Directory.CreateDirectory(Path.Combine(repo.Path, "scripts"));
        File.WriteAllText(Path.Combine(repo.Path, "scripts", "publish.ps1"), "");

        Assert.Equal(repo.Path, AppRebuild.FindSourceRepo(publishDirectory + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void FindSourceRepo_ReturnsNull_ForADebugBuild()
    {
        using var repo = new TempDirectory();
        var debugDirectory = Path.Combine(repo.Path, "bin", "Debug", "net10.0-windows");
        Directory.CreateDirectory(debugDirectory);
        Directory.CreateDirectory(Path.Combine(repo.Path, "scripts"));
        File.WriteAllText(Path.Combine(repo.Path, "scripts", "publish.ps1"), "");

        Assert.Null(AppRebuild.FindSourceRepo(debugDirectory));
    }

    [Fact]
    public void FindSourceRepo_ReturnsNull_ForAShippedCopyWithNoScript()
    {
        using var repo = new TempDirectory();
        var publishDirectory = Path.Combine(repo.Path, "bin", "publish", "win-x64");
        Directory.CreateDirectory(publishDirectory);

        Assert.Null(AppRebuild.FindSourceRepo(publishDirectory));
    }

    [Theory]
    [InlineData(new[] { "--wait-for-exit", "1234" }, 1234)]
    [InlineData(new string[0], null)]
    [InlineData(new[] { "--wait-for-exit" }, null)]
    [InlineData(new[] { "--wait-for-exit", "-5" }, null)]
    [InlineData(new[] { "--other", "1234" }, null)]
    public void PreviousInstanceId_ReadsOnlyTheWaitArgument(string[] args, int? expectedProcessId)
    {
        Assert.Equal(expectedProcessId, AppRebuild.PreviousInstanceId(args));
    }
}
