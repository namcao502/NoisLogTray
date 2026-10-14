using System.Drawing;
using NoisLogTray;

namespace NoisLogTray.Tests;

public class HeaderTabTests
{
    private static (HeaderTab Tab, List<string> Requests) NewTab(bool hasMenu = false)
    {
        var tab = new HeaderTab("MDP-9262", 28, hasMenu);
        var requests = new List<string>();
        tab.SelectRequested += () => requests.Add("select");
        tab.CloseRequested += () => requests.Add("close");
        tab.MenuRequested += () => requests.Add("menu");
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
        using var shortTab = new HeaderTab("A-1", 28);
        using var longTab = new HeaderTab("MDP-123456", 28);

        Assert.True(longTab.Width > shortTab.Width);
        Assert.Equal(28, longTab.Height);
        Assert.True(longTab.ClientRectangle.Contains(longTab.CloseBounds));
    }

    [Fact]
    public void Press_OnTheMenuArea_RequestsTheMenu_OnlyWithAMenu()
    {
        var (withMenu, menuRequests) = NewTab(hasMenu: true);
        var (plain, plainRequests) = NewTab();
        using (withMenu)
        using (plain)
        {
            var menu = withMenu.MenuBounds;
            withMenu.Press(new Point(menu.Left + menu.Width / 2, menu.Top + menu.Height / 2), MouseButtons.Left);
            plain.Press(new Point(menu.Left + menu.Width / 2, menu.Top + menu.Height / 2), MouseButtons.Left);

            Assert.Equal(new[] { "menu" }, menuRequests);
            Assert.Equal(Rectangle.Empty, plain.MenuBounds);
            Assert.DoesNotContain("menu", plainRequests);
        }
    }

    [Fact]
    public void Size_FitsTheMenuArea_WhenItHasOne()
    {
        using var plain = new HeaderTab("Backend-DotNet", 28);
        using var withMenu = new HeaderTab("Backend-DotNet", 28, hasMenu: true);

        Assert.True(withMenu.Width > plain.Width);
        Assert.True(withMenu.ClientRectangle.Contains(withMenu.MenuBounds));
        Assert.True(withMenu.MenuBounds.Right <= withMenu.CloseBounds.Left);
    }
}
