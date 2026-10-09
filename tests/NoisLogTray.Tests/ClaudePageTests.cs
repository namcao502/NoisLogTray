using NoisLogTray;

namespace NoisLogTray.Tests;

public class ClaudePageTests
{
    [Theory]
    [InlineData(1, (int)ShellPage.Dashboard)]
    [InlineData(2, (int)ShellPage.Log)]
    [InlineData(3, (int)ShellPage.Tasks)]
    [InlineData(4, (int)ShellPage.Week)]
    [InlineData(5, (int)ShellPage.Claude)]
    public void PageForShortcut_MatchesTheShellShortcuts(int index, int expected)
    {
        Assert.Equal((ShellPage)expected, ClaudePage.PageForShortcut(index));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void PageForShortcut_IgnoresOtherDigits(int index)
    {
        Assert.Null(ClaudePage.PageForShortcut(index));
    }
}
