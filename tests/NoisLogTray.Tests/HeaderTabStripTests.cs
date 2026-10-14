using NoisLogTray;

namespace NoisLogTray.Tests;

public class HeaderTabStripTests
{
    private static readonly IReadOnlyList<(string Key, string Text, string? ToolTip)> TwoTabs = new List<(string, string, string?)>
    {
        ("claude", "Backend-DotNet", @"C:\Project\Backend-DotNet"),
        ("claude2", "Portal-Angular", @"C:\Project\Portal-Angular"),
    };

    [Fact]
    public void SetTabs_BuildsOneTabPerEntry_InOrder_WithTheSelectedOneMarked()
    {
        using var strip = new HeaderTabStrip(hasMenus: false, hasAddButton: false);

        strip.SetTabs(TwoTabs, "claude2", new HashSet<string>());

        Assert.Equal(new[] { "Backend-DotNet", "Portal-Angular" }, strip.Tabs.Select(ht => ht.Text));
        Assert.Equal(new[] { false, true }, strip.Tabs.Select(ht => ht.Selected));
        Assert.True(strip.Tabs[1].Left > strip.Tabs[0].Left);
        Assert.Null(strip.AddButton);
    }

    [Fact]
    public void SetTabs_PutsADotOnWaitingTabs()
    {
        using var strip = new HeaderTabStrip(hasMenus: false, hasAddButton: false);

        strip.SetTabs(TwoTabs, "claude", new HashSet<string> { "claude2" });

        Assert.Equal(new[] { false, true }, strip.Tabs.Select(ht => ht.ShowDot));
        strip.SetWaiting(new HashSet<string>());
        Assert.All(strip.Tabs, ht => Assert.False(ht.ShowDot));
    }

    [Fact]
    public void AddButton_IsThere_OnlyWhenAsked_AndRaisesAddRequested()
    {
        using var strip = new HeaderTabStrip(hasMenus: true, hasAddButton: true) { Size = new System.Drawing.Size(1000, 40) };
        var added = 0;
        strip.AddRequested += () => added++;

        strip.SetTabs(TwoTabs, "claude", new HashSet<string>());
        strip.AddButton!.PerformClick();

        Assert.Equal(1, added);
        Assert.True(strip.AddButton.Left >= strip.Tabs[^1].Right);
    }

    [Fact]
    public void MenuRequested_PassesTheTabKey()
    {
        using var strip = new HeaderTabStrip(hasMenus: true, hasAddButton: false);
        var requests = new List<string>();
        strip.MenuRequested += (key, _) => requests.Add(key);
        strip.SelectRequested += key => requests.Add("select " + key);
        strip.CloseRequested += key => requests.Add("close " + key);
        strip.SetTabs(TwoTabs, "claude", new HashSet<string>());

        var menu = strip.Tabs[1].MenuBounds;
        strip.Tabs[1].Press(new System.Drawing.Point(menu.Left + 2, menu.Top + 2), MouseButtons.Left);
        strip.Tabs[0].Press(new System.Drawing.Point(4, 4), MouseButtons.Left);
        strip.Tabs[0].Press(new System.Drawing.Point(4, 4), MouseButtons.Middle);

        Assert.Equal(new[] { "claude2", "select claude", "close claude" }, requests);
    }
}
