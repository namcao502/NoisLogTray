using System.Drawing;
using NoisLogTray;

namespace NoisLogTray.Tests;

// LogPage builds Queue rows, which write RowColumns (static, UI thread only) on first use: no parallel row tests.
[Collection("TicketRowControls")]
public class WorkPageTests
{
    [Fact]
    public void Wide_PutsTheLogColumnRightOfTheBoard()
    {
        using var logPage = new LogPage(null, null);
        using var work = new WorkPage(logPage, null);

        work.Size = UiScale.S(new Size(1400, 900));

        Assert.Equal(UiScale.S(new Rectangle(920, 0, ShellLayout.LogColumnWidth, 900)), logPage.Bounds);
        var tasksArea = logPage.Parent!.Controls.OfType<Label>().Single();
        Assert.Equal(UiScale.S(new Rectangle(0, 0, 920, 900)), tasksArea.Bounds);
    }

    [Fact]
    public void Narrow_StacksTheLogColumnOverTheBoard()
    {
        using var logPage = new LogPage(null, null);
        using var work = new WorkPage(logPage, null);

        work.Size = UiScale.S(new Size(1000, 900));

        var tasksArea = logPage.Parent!.Controls.OfType<Label>().Single();
        Assert.Equal(0, logPage.Top);
        Assert.Equal(UiScale.S(LogPage.NaturalHeight), logPage.Height);
        Assert.Equal(logPage.Bottom, tasksArea.Top);
        Assert.Equal(logPage.Width, tasksArea.Width);
        Assert.True(logPage.Width < work.ClientSize.Width); // clear of the page's scroll bar
    }

    [Fact]
    public void AddTicketToInput_RaisesTheTypedTickets()
    {
        using var logPage = new LogPage(null, null);
        IReadOnlyList<string>? typed = null;
        logPage.EntryTicketsChanged += ticketKeys => typed = ticketKeys;

        logPage.AddTicketToInput("MDP-1", "One");
        logPage.AddTicketToInput("MDP-2", "Two");
        logPage.AddTicketToInput("MDP-1", "One");

        Assert.Equal(new[] { "MDP-1", "MDP-2" }, typed);
    }
}
