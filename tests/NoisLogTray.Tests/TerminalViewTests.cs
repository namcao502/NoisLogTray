using NoisLogTray;

namespace NoisLogTray.Tests;

public class TerminalViewTests
{
    [Theory]
    [InlineData(1, (int)ShellPage.Dashboard)]
    [InlineData(2, (int)ShellPage.Log)]
    [InlineData(3, (int)ShellPage.Tasks)]
    [InlineData(4, (int)ShellPage.Claude)]
    [InlineData(5, (int)ShellPage.Ticket)]
    public void PageForShortcut_MatchesTheShellShortcuts(int index, int expected)
    {
        Assert.Equal((ShellPage)expected, TerminalView.PageForShortcut(index));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(7)]
    public void PageForShortcut_IgnoresOtherDigits(int index)
    {
        Assert.Null(TerminalView.PageForShortcut(index));
    }
}
