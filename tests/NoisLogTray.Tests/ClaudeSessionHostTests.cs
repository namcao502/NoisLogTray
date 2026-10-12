namespace NoisLogTray.Tests;

public class ClaudeSessionHostTests
{
    [Fact]
    public void BuildCommandLine_QuotesPromptsButNotFlags()
    {
        const string executable = @"C:\x\claude.exe";

        Assert.Equal("\"C:\\x\\claude.exe\"", ClaudeSessionHost.BuildCommandLine(executable, null, null));
        Assert.Equal("\"C:\\x\\claude.exe\"", ClaudeSessionHost.BuildCommandLine(executable, null, ""));
        Assert.Equal("\"C:\\x\\claude.exe\" --continue", ClaudeSessionHost.BuildCommandLine(executable, null, "--continue"));
        Assert.Equal("\"C:\\x\\claude.exe\" \"ship MDP-1\"", ClaudeSessionHost.BuildCommandLine(executable, null, "ship MDP-1"));
    }

    [Fact]
    public void BuildCommandLine_PutsTheQuotedSettingsFileFirst_WhenGiven()
    {
        const string executable = @"C:\x\claude.exe";
        const string settingsFile = @"C:\a b\s.json";

        Assert.Equal("\"C:\\x\\claude.exe\" --settings \"C:\\a b\\s.json\" --continue", ClaudeSessionHost.BuildCommandLine(executable, settingsFile, "--continue"));
        Assert.Equal("\"C:\\x\\claude.exe\" --settings \"C:\\a b\\s.json\" \"ship MDP-1\"", ClaudeSessionHost.BuildCommandLine(executable, settingsFile, "ship MDP-1"));
    }

    [Fact]
    public void Start_WithoutFallback_ThrowsForAMissingDirectory()
    {
        var missing = Path.Combine(Path.GetTempPath(), "NoisLogTray.Tests", Guid.NewGuid().ToString("N"));
        using var host = new ClaudeSessionHost(() => missing, fallBackToProfile: false);
        var started = false;
        host.Started += () => started = true;

        var error = Assert.Throws<DirectoryNotFoundException>(() => host.Start(80, 24));

        Assert.Contains(missing, error.Message);
        Assert.False(host.IsRunning);
        Assert.False(started);
    }

    [Fact]
    public void Start_RaisesStarting_EvenWhenItThrows()
    {
        var missing = Path.Combine(Path.GetTempPath(), "NoisLogTray.Tests", Guid.NewGuid().ToString("N"));
        using var host = new ClaudeSessionHost(() => missing, fallBackToProfile: false);
        var startingCount = 0;
        host.Starting += () => startingCount++;

        Assert.Throws<DirectoryNotFoundException>(() => host.Start(80, 24));

        Assert.Equal(1, startingCount);
        Assert.False(host.IsRunning);
    }
}
