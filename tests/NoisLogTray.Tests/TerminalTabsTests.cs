using NoisLogTray;

namespace NoisLogTray.Tests;

public class TerminalTabsTests
{
    [Fact]
    public void NextName_StartsAtClaude_ThenNumbersTheRest()
    {
        Assert.Equal("claude", TerminalTabs.NextName(Array.Empty<string>()));
        Assert.Equal("claude2", TerminalTabs.NextName(new[] { "claude" }));
        Assert.Equal("claude2", TerminalTabs.NextName(new[] { "claude", "claude3" }));
        Assert.Equal("claude", TerminalTabs.NextName(new[] { "claude2" }));
    }

    [Theory]
    [InlineData("claude", true)]
    [InlineData("claude2", true)]
    [InlineData("claude12", true)]
    [InlineData("MDP-1", false)]
    [InlineData("claudex", false)]
    [InlineData("claude-2", false)]
    [InlineData("", false)]
    public void IsTerminalSession_TellsTerminalsFromTicketKeys(string sessionName, bool expected)
    {
        Assert.Equal(expected, TerminalTabs.IsTerminalSession(sessionName));
    }

    [Theory]
    [InlineData(@"C:\Project\Backend-DotNet", "Backend-DotNet")]
    [InlineData(@"C:\Project\Backend-DotNet\", "Backend-DotNet")]
    [InlineData(@"C:\", @"C:\")]
    public void Label_IsTheFolderName(string directory, string expected)
    {
        Assert.Equal(expected, TerminalTabs.Label(directory));
    }

    [Fact]
    public void MigrateSessionIds_MovesBareFolderKeysToTheFirstTab_AndKeepsNewKeys()
    {
        var sessionIds = new Dictionary<string, string>
        {
            [@"C:\Project\Backend-DotNet"] = "old",
            [@"claude2|C:\Project\Portal-Angular"] = "new",
        };

        var migrated = TerminalTabs.MigrateSessionIds(sessionIds);

        Assert.Equal(new Dictionary<string, string>
        {
            [@"claude|C:\Project\Backend-DotNet"] = "old",
            [@"claude2|C:\Project\Portal-Angular"] = "new",
        }, migrated);
        Assert.Equal(@"claude|C:\Project\Backend-DotNet", TerminalTabs.SessionKey("claude", @"C:\Project\Backend-DotNet"));
    }

    [Fact]
    public void ValidTabs_DropsOddAndRepeatedNames_AndKeepsAFolderThatIsGone()
    {
        var tabs = new List<TerminalTabSetting?>
        {
            new() { Name = "claude", Directory = @"C:\does-not-exist-anywhere" },
            new() { Name = "..\\evil" },
            null,
            new() { Name = null! }, // "name": null in settings.json
            new() { Name = "claude" },
            new() { Name = "claude2" },
        };

        var valid = TerminalTabs.ValidTabs(tabs);

        Assert.Equal(new[] { "claude", "claude2" }, valid.Select(tts => tts.Name));
        Assert.Equal(@"C:\does-not-exist-anywhere", valid[0].Directory);
    }

    [Fact]
    public void WithoutTab_DropsEveryFolderOfThatTab_Only()
    {
        var sessionIds = new Dictionary<string, string>
        {
            [@"claude2|C:\a"] = "1",
            [@"claude2|C:\b"] = "2",
            [@"claude20|C:\a"] = "3",
            [@"claude|C:\a"] = "4",
        };

        var remaining = TerminalTabs.WithoutTab(sessionIds, "claude2");

        Assert.Equal(new[] { @"claude20|C:\a", @"claude|C:\a" }, remaining.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }
}
