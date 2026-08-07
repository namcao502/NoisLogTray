using NoisLogTray;

namespace NoisLogTray.Tests;

// Covers AppSettings persistence: round-trip of UI state + the Config map, defaults on
// a missing file, and the corrupt-file backup (never silently reset).
public class AppSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"noislog-settings-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { /* ignore */ }
        try { File.Delete(_path + ".bad"); } catch { /* ignore */ }
    }

    [Fact]
    public void MissingFileYieldsDefaults()
    {
        var s = AppSettings.LoadOrBackup(out var corrupt, _path);
        Assert.False(corrupt);
        Assert.True(s.Dark);
        Assert.Null(s.WindowX);
        Assert.Empty(s.Config);
    }

    [Fact]
    public void RoundTripsUiStateAndConfig()
    {
        var s = new AppSettings { Dark = false, WindowX = 120, WindowY = 340 };
        s.Config["JIRA_EMAIL"] = "you@company.com";
        s.Config["LOG_TIME"] = "6:00 PM";
        AppSettings.Save(s, _path);

        var read = AppSettings.Load(_path);
        Assert.False(read.Dark);
        Assert.Equal(120, read.WindowX);
        Assert.Equal(340, read.WindowY);
        Assert.Equal("you@company.com", read.Config["JIRA_EMAIL"]);
        Assert.Equal("6:00 PM", read.Config["LOG_TIME"]);
    }

    [Fact]
    public void CorruptFileIsBackedUpAndNotOverwritten()
    {
        File.WriteAllText(_path, "{ not valid json");

        var s = AppSettings.LoadOrBackup(out var corrupt, _path);
        Assert.True(corrupt);
        Assert.True(s.Dark); // defaults returned
        Assert.True(File.Exists(_path + ".bad"));
        Assert.Equal("{ not valid json", File.ReadAllText(_path + ".bad")); // raw content preserved
    }
}
