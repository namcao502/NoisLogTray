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
        AppSettings.Save(new AppSettings
        {
            TicketHeaderCollapsed = true, DiffListWidthEm = 31.5, DiffListHidden = true, DiffInline = true,
            FilesListWidthEm = 24.25, FilesListHidden = true,
        }, _path);

        var read = AppSettings.Load(_path);
        Assert.True(read.TicketHeaderCollapsed);
        Assert.Equal(31.5, read.DiffListWidthEm);
        Assert.True(read.DiffListHidden);
        Assert.True(read.DiffInline);
        Assert.Equal(24.25, read.FilesListWidthEm);
        Assert.True(read.FilesListHidden);
    }

    [Fact]
    public void TeamsPostedAt_RoundTrips()
    {
        var postedAt = new DateTimeOffset(2026, 10, 12, 14, 30, 0, TimeSpan.FromHours(7));
        AppSettings.Save(new AppSettings { TeamsPostedAt = new Dictionary<string, DateTimeOffset> { ["MDP-1"] = postedAt } }, _path);

        Assert.Equal(postedAt, AppSettings.Load(_path).TeamsPostedAt["MDP-1"]);
    }

    [Fact]
    public void AppSettings_RoundTripsTeamsRecentChannelIds()
    {
        AppSettings.Save(new AppSettings { TeamsRecentChannelIds = new List<string> { "c2", "c1" } }, _path);

        Assert.Equal(new[] { "c2", "c1" }, AppSettings.Load(_path).TeamsRecentChannelIds);
    }

    [Fact]
    public void Load_DefaultsTeamsRecentChannelIds_WhenMissing()
    {
        File.WriteAllText(_path, "{ \"teamsRecentChannelIds\": null }");

        var read = AppSettings.LoadOrBackup(out var corrupt, _path);
        Assert.False(corrupt);
        Assert.Empty(read.TeamsRecentChannelIds);
    }

    [Fact]
    public void AppSettings_RoundTripsTeamsPeople()
    {
        var people = new List<TeamsPerson> { new("8:orgid:a", "Ann"), new("8:orgid:b", "Bob") };
        AppSettings.Save(new AppSettings { TeamsPeople = people }, _path);

        Assert.Equal(people, AppSettings.Load(_path).TeamsPeople);
    }

    [Fact]
    public void Load_DefaultsTeamsPeople_WhenMissing()
    {
        File.WriteAllText(_path, "{ \"teamsPeople\": null }");

        var read = AppSettings.LoadOrBackup(out var corrupt, _path);
        Assert.False(corrupt);
        Assert.Empty(read.TeamsPeople);
    }

    [Fact]
    public void Load_DefaultsTeamsPostLeadToNull()
    {
        File.WriteAllText(_path, "{ }");

        var read = AppSettings.LoadOrBackup(out var corrupt, _path);
        Assert.False(corrupt);
        Assert.Null(read.TeamsPostLead);

        AppSettings.Save(new AppSettings { TeamsPostLead = new TeamsPerson("8:a", "Ann") }, _path);
        Assert.Equal(new TeamsPerson("8:a", "Ann"), AppSettings.Load(_path).TeamsPostLead);
    }

    [Fact]
    public void TerminalTabs_KeepNullApartFromEmpty()
    {
        AppSettings.Save(new AppSettings(), _path);
        Assert.Null(AppSettings.Load(_path).TerminalTabs);

        AppSettings.Save(new AppSettings { TerminalTabs = new List<TerminalTabSetting>() }, _path);
        Assert.NotNull(AppSettings.Load(_path).TerminalTabs);
        Assert.Empty(AppSettings.Load(_path).TerminalTabs!);

        AppSettings.Save(new AppSettings
        {
            TerminalTabs = new List<TerminalTabSetting> { new() { Name = "claude2", Directory = @"C:\a" } },
            SelectedTerminal = "claude2",
        }, _path);
        var read = AppSettings.Load(_path);
        Assert.Equal("claude2", read.TerminalTabs!.Single().Name);
        Assert.Equal(@"C:\a", read.TerminalTabs!.Single().Directory);
        Assert.Equal("claude2", read.SelectedTerminal);
    }

    [Fact]
    public void Load_DefaultsTeamsSeenAndNotifiedMaps_WhenMissing()
    {
        File.WriteAllText(_path, "{ \"teamsSeenAt\": null, \"teamsNotifiedAt\": null }");

        var read = AppSettings.LoadOrBackup(out var corrupt, _path);
        Assert.False(corrupt);
        Assert.Empty(read.TeamsSeenAt);
        Assert.Empty(read.TeamsNotifiedAt);
        Assert.False(read.TeamsAttentionBaselineDone);
    }
}
