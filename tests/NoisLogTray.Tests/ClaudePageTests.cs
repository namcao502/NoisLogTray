using NoisLogTray;

namespace NoisLogTray.Tests;

// The page is never shown here, so it builds no TerminalView (WebView2 does not run in tests) and asks for no host.
public class ClaudePageTests
{
    private static ClaudePage NewPage() =>
        new(_ => throw new InvalidOperationException("no host in tests"), () => Array.Empty<string>());

    [Fact]
    public void SetTabs_LabelsEachTabWithItsFolder_AndHasAnAddButton()
    {
        using var page = NewPage();

        page.SetTabs(new[] { ("claude", @"C:\Project\Backend-DotNet"), ("claude2", @"C:\Project\Portal-Angular") }, "claude2");

        var strip = (HeaderTabStrip)page.HeaderControl;
        Assert.Equal(new[] { "Backend-DotNet", "Portal-Angular" }, strip.Tabs.Select(ht => ht.Text));
        Assert.Equal(new[] { false, true }, strip.Tabs.Select(ht => ht.Selected));
        Assert.NotNull(strip.AddButton);
        Assert.True(page.HasOpenTabs);
    }

    [Fact]
    public void HasOpenTabs_IsFalse_WithNoTerminal()
    {
        using var page = NewPage();
        string? selection = "unset";
        page.SelectionChanged += name => selection = name;

        page.SetTabs(new[] { ("claude", @"C:\a") }, null);
        page.SetTabs(Array.Empty<(string, string)>(), null);

        Assert.False(page.HasOpenTabs);
        Assert.Null(selection);
    }

    [Fact]
    public void AddButton_RaisesAddRequested()
    {
        using var page = NewPage();
        var added = 0;
        page.AddRequested += () => added++;

        ((HeaderTabStrip)page.HeaderControl).AddButton!.PerformClick();

        Assert.Equal(1, added);
    }

    [Fact]
    public void SetWaiting_PutsADotOnThatTerminalsTab()
    {
        using var page = NewPage();
        page.SetTabs(new[] { ("claude", @"C:\a"), ("claude2", @"C:\b") }, "claude");

        page.SetWaiting(new[] { "claude2" });

        Assert.Equal(new[] { false, true }, ((HeaderTabStrip)page.HeaderControl).Tabs.Select(ht => ht.ShowDot));
    }
}
