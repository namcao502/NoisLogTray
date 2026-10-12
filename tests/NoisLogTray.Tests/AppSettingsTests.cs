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
        Assert.Equal("en", s.Language);
        Assert.Null(s.WindowX);
        Assert.Empty(s.Config);
    }

    [Fact]
    public void RoundTripsUiStateAndConfig()
    {
        var s = new AppSettings { Dark = false, Language = "vi", WindowX = 120, WindowY = 340, WindowWidth = 720, WindowHeight = 900 };
        s.Config["JIRA_EMAIL"] = "you@company.com";
        s.Config["LOG_TIME"] = "6:00 PM";
        AppSettings.Save(s, _path);

        var read = AppSettings.Load(_path);
        Assert.False(read.Dark);
        Assert.Equal("vi", read.Language);
        Assert.Equal(120, read.WindowX);
        Assert.Equal(340, read.WindowY);
        Assert.Equal(720, read.WindowWidth);
        Assert.Equal(900, read.WindowHeight);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void WindowState_RoundTrips(bool? maximized)
    {
        AppSettings.Save(new AppSettings { WindowMaximized = maximized, SidebarCollapsed = true }, _path);

        var read = AppSettings.Load(_path);
        Assert.Equal(maximized, read.WindowMaximized);
        Assert.True(read.SidebarCollapsed);
    }

    [Fact]
    public void UiZoomPercent_RoundTripsAndDefaultsWhenMissing()
    {
        File.WriteAllText(_path, "{ \"dark\": true }");
        Assert.Equal(UiScale.DefaultZoomPercent, AppSettings.Load(_path).UiZoomPercent);

        AppSettings.Save(new AppSettings { UiZoomPercent = 150 }, _path);
        Assert.Equal(150, AppSettings.Load(_path).UiZoomPercent);
    }

    [Fact]
    public void Load_NullOpenTicketTabs_BecomesEmpty()
    {
        File.WriteAllText(_path, "{ \"openTicketTabs\": null }");

        var read = AppSettings.LoadOrBackup(out var corrupt, _path);
        Assert.False(corrupt);
        Assert.Empty(read.OpenTicketTabs);
    }

    [Fact]
    public void Load_NullClaudeFolderLists_BecomeEmpty()
    {
        File.WriteAllText(_path, "{ \"recentClaudeDirectories\": null, \"claudeSessionIds\": null }");

        var read = AppSettings.LoadOrBackup(out var corrupt, _path);
        Assert.False(corrupt);
        Assert.Empty(read.RecentClaudeDirectories);
        Assert.Empty(read.ClaudeSessionIds);
    }

    [Fact]
    public void OpenTicketTabs_RoundTripInOrder()
    {
        AppSettings.Save(new AppSettings { OpenTicketTabs = new List<string> { "MDP-2", "MDP-1" } }, _path);

        Assert.Equal(new[] { "MDP-2", "MDP-1" }, AppSettings.Load(_path).OpenTicketTabs);
    }

    [Fact]
    public void Load_NullTeamsPostedAt_BecomesEmpty()
    {
        File.WriteAllText(_path, "{ \"teamsPostedAt\": null }");

        var read = AppSettings.LoadOrBackup(out var corrupt, _path);
        Assert.False(corrupt);
        Assert.Empty(read.TeamsPostedAt);
    }

    [Fact]
    public void TicketViewLayout_RoundTrips()
    {
        AppSettings.Save(new AppSettings { TicketHeaderCollapsed = true, DiffListWidthEm = 31.5, DiffListHidden = true, DiffInline = true }, _path);

        var read = AppSettings.Load(_path);
        Assert.True(read.TicketHeaderCollapsed);
        Assert.Equal(31.5, read.DiffListWidthEm);
        Assert.True(read.DiffListHidden);
        Assert.True(read.DiffInline);
    }

    [Fact]
    public void TeamsPostedAt_RoundTrips()
    {
        var postedAt = new DateTimeOffset(2026, 10, 12, 14, 30, 0, TimeSpan.FromHours(7));
        AppSettings.Save(new AppSettings { TeamsPostedAt = new Dictionary<string, DateTimeOffset> { ["MDP-1"] = postedAt } }, _path);

        Assert.Equal(postedAt, AppSettings.Load(_path).TeamsPostedAt["MDP-1"]);
    }
}
