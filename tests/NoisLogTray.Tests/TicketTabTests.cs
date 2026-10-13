using System.Drawing;
using NoisLogTray;

namespace NoisLogTray.Tests;

public class TicketTabTests
{
    private static (TicketTab Tab, List<string> Requests) NewTab()
    {
        var tab = new TicketTab("MDP-9262", 28);
        var requests = new List<string>();
        tab.SelectRequested += () => requests.Add("select");
        tab.CloseRequested += () => requests.Add("close");
        return (tab, requests);
    }

    [Fact]
    public void Press_OnTheKey_SelectsTheTab()
    {
        var (tab, requests) = NewTab();
        using (tab)
        {
            tab.Press(new Point(10, tab.Height / 2), MouseButtons.Left);

            Assert.Equal(new[] { "select" }, requests);
        }
    }

    [Fact]
    public void Press_OnTheCross_ClosesTheTab()
    {
        var (tab, requests) = NewTab();
        using (tab)
        {
            var cross = tab.CloseBounds;
            tab.Press(new Point(cross.Left + cross.Width / 2, cross.Top + cross.Height / 2), MouseButtons.Left);

            Assert.Equal(new[] { "close" }, requests);
        }
    }

    [Fact]
    public void Press_WithTheMiddleButton_ClosesTheTab_AndARightClickDoesNothing()
    {
        var (tab, requests) = NewTab();
        using (tab)
        {
            tab.Press(new Point(10, tab.Height / 2), MouseButtons.Middle);
            tab.Press(new Point(10, tab.Height / 2), MouseButtons.Right);

            Assert.Equal(new[] { "close" }, requests);
        }
    }

    [Fact]
    public void Size_FitsTheKeyAndTheCross_InsideOnePill()
    {
        using var shortTab = new TicketTab("A-1", 28);
        using var longTab = new TicketTab("MDP-123456", 28);

        Assert.True(longTab.Width > shortTab.Width);
        Assert.Equal(28, longTab.Height);
        Assert.True(longTab.ClientRectangle.Contains(longTab.CloseBounds));
    }
}
