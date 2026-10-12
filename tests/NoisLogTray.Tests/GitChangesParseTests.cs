namespace NoisLogTray.Tests;

// The parts of GitChanges that need no repository, kept apart so they skip the real-git fixture.
public class GitChangesParseTests
{
    [Fact]
    public void ParseNameStatus_ReadsStatusPathPairs()
    {
        var files = GitChanges.ParseNameStatus("M\0src/A.cs\0D\0b.txt\0");

        Assert.Equal(
            new[]
            {
                new FileChange("src/A.cs", FileChangeStatus.Modified),
                new FileChange("b.txt", FileChangeStatus.Deleted),
            },
            files);
    }

    [Fact]
    public void ParseNameStatus_ReadsOtherLettersAsModified()
    {
        var files = GitChanges.ParseNameStatus("T\0link\0A\0new.cs\0");

        Assert.Equal(
            new[]
            {
                new FileChange("link", FileChangeStatus.Modified),
                new FileChange("new.cs", FileChangeStatus.Added),
            },
            files);
    }

    [Fact]
    public void Displayable_RejectsBinaryAndOversized()
    {
        Assert.Equal("text", GitChanges.Displayable("text"));
        Assert.Null(GitChanges.Displayable("a\0b"));
        Assert.Null(GitChanges.Displayable(new string('x', GitChanges.MaxChars + 1)));
        Assert.NotNull(GitChanges.Displayable(new string('x', GitChanges.MaxChars)));
    }
}
