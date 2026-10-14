using System.Text;

namespace NoisLogTray.Tests;

public class ClaudeSessionHostTests
{
    [Fact]
    public void BuildCommandLine_RunsClaudeInWindowsPowerShell_QuotingPromptsButNotFlags()
    {
        const string executable = @"C:\x\claude.exe";
        const string prefix = @"powershell.exe -NoLogo -NoExit -Command ""function global:claude { & 'C:\x\claude.exe' @args }; claude";

        Assert.Equal(prefix + "\"", ClaudeSessionHost.BuildCommandLine(null, executable, null, null));
        Assert.Equal(prefix + "\"", ClaudeSessionHost.BuildCommandLine(null, executable, null, ""));
        Assert.Equal(prefix + " --continue\"", ClaudeSessionHost.BuildCommandLine(null, executable, null, "--continue"));
        Assert.Equal(prefix + " 'ship MDP-1'\"", ClaudeSessionHost.BuildCommandLine(null, executable, null, "ship MDP-1"));
    }

    [Fact]
    public void BuildCommandLine_PutsTheSettingsFileInTheClaudeFunction_WhenGiven()
    {
        const string executable = @"C:\x\claude.exe";
        const string settingsFile = @"C:\a b\s.json";

        Assert.Equal(
            @"powershell.exe -NoLogo -NoExit -Command ""function global:claude { & 'C:\x\claude.exe' --settings 'C:\a b\s.json' @args }; claude --continue""",
            ClaudeSessionHost.BuildCommandLine(null, executable, settingsFile, "--continue"));
    }

    [Fact]
    public void BuildCommandLine_RunsPowerShell7_WhenInstalled_AndDoublesSingleQuotes()
    {
        Assert.Equal(
            @"""C:\Program Files\PowerShell\7\pwsh.exe"" -NoLogo -NoExit -Command ""function global:claude { & 'C:\o''neil\claude.exe' @args }; claude""",
            ClaudeSessionHost.BuildCommandLine(@"C:\Program Files\PowerShell\7\pwsh.exe", @"C:\o'neil\claude.exe", null, null));
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

    [Fact]
    public void Start_RunsClaudeInAShell_ThatStaysAtItsPromptAfterClaudeExits()
    {
        // An existing folder: the shell keeps a new one locked for a moment after it is stopped, so it could not be deleted.
        var folder = Environment.CurrentDirectory;
        var output = new StringBuilder();
        // hostname.exe stands in for claude: it prints the machine name and exits at once.
        using var host = new ClaudeSessionHost(() => folder, claudeExecutable: Path.Combine(Environment.SystemDirectory, "hostname.exe"));
        host.Attach(bytes =>
        {
            lock (output) output.Append(Encoding.UTF8.GetString(bytes));
        });

        host.Start(200, 30);
        // Concatenated in the shell, so only the command's output (not the typed line) holds the joined text.
        host.Write(Encoding.ASCII.GetBytes("Write-Output ('shell' + '-ok:' + (Get-Command claude).CommandType + ':' + (Get-Location).Path)\r"));

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && !OutputText(output).Contains("shell-ok:")) Thread.Sleep(100);
        Assert.Contains(Environment.MachineName, OutputText(output), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("shell-ok:Function:" + folder, OutputText(output));
        Assert.True(host.IsRunning);
    }

    [Fact]
    public void Start_ReportsWhetherThisStartHasHookSettings()
    {
        var folder = Environment.CurrentDirectory;
        var standInExecutable = Path.Combine(Environment.SystemDirectory, "hostname.exe");
        string? settingsFile = null;
        using var host = new ClaudeSessionHost(() => folder, settingsFile: () => settingsFile, claudeExecutable: standInExecutable);

        host.Start(80, 24);
        Assert.False(host.HasHooks);

        settingsFile = Path.Combine(folder, "hooks.json");
        host.Start(80, 24);
        Assert.True(host.HasHooks);
    }

    private static string OutputText(StringBuilder output)
    {
        lock (output) return output.ToString();
    }
}
