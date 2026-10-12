using System.Drawing;
using NoisLogTray;

namespace NoisLogTray.Tests;

// Both row test classes write RowColumns (static, UI thread only) on first use, so they must not run in parallel.
[Collection("TicketRowControls")]
public class TaskRowTests
{
    private const string JiraUrl = "https://example.atlassian.net";

    private static TaskRow NewRow(TempDirectory temp, JiraTicket ticket, TicketSection section, bool isDevPrMerged = false,
        int width = 900, string prSummary = "") =>
        new(ticket, section, false, JiraUrl, new TicketBoard(new TicketStore(temp.Path)), (_, _) => { }, _ => { }, _ => { },
            isDevPrMerged, _ => { }, null, width, prSummary);

    [Fact]
    public void Buttons_AreTintedByAction()
    {
        using var temp = new TempDirectory();
        using var inProgress = NewRow(temp, new JiraTicket("MDP-1", "S", null, null, Status: "PR & Code Review"), TicketSection.InProgress, isDevPrMerged: true);
        using var pending = NewRow(temp, new JiraTicket("MDP-2", "S", null, null), TicketSection.New);

        Assert.Equal(new Color?[] { null, Theme.ActionDone, Theme.Accent, Theme.ActionReadyToTest }, inProgress.Controls.OfType<MacButton>().Select(mb => mb.Tint));
        Assert.Equal(new Color?[] { null, Theme.ActionStart }, pending.Controls.OfType<MacButton>().Select(mb => mb.Tint));
    }

    [Fact]
    public void ShortDate_FormatsAJiraDate_AndKeepsOtherText()
    {
        var formatted = TaskRow.ShortDate("2026-10-15");
        Assert.True(formatted == "Oct 15" || formatted == "15/10", formatted);
        Assert.Equal("-", TaskRow.ShortDate(null));
        Assert.Equal("soon", TaskRow.ShortDate("soon"));
    }

    [Fact]
    public void Row_PutsTheButtonsOnTheKeyLine_AndThePrsUnderTheFacts()
    {
        using var temp = new TempDirectory();
        using var row = NewRow(temp, new JiraTicket("MDP-1", "S", "2026-10-14", "2026-10-15"), TicketSection.InProgress, prSummary: "BE main ●");
        var facts = row.Controls.OfType<FlowLayoutPanel>().Single();
        var prLine = row.Controls.OfType<Label>().Single(l => l.Text.StartsWith("PR  "));

        Assert.All(row.Controls.OfType<MacButton>(), mb => Assert.True(mb.Bottom <= facts.Top));
        Assert.True(prLine.Top >= facts.Bottom);
    }

    [Fact]
    public void Row_PutsTheButtonsUnderThePrs_WhenNarrow()
    {
        using var temp = new TempDirectory();
        using var row = NewRow(temp, new JiraTicket("MDP-1", "S", null, null), TicketSection.InProgress, width: 300, prSummary: "BE main ●");
        var prLine = row.Controls.OfType<Label>().Single(l => l.Text.StartsWith("PR  "));

        Assert.All(row.Controls.OfType<MacButton>(), mb => Assert.True(mb.Top >= prLine.Bottom));
    }

    [Fact]
    public void Row_HasNoPrLine_WithoutPrs()
    {
        using var temp = new TempDirectory();
        using var row = NewRow(temp, new JiraTicket("MDP-1", "S", null, null), TicketSection.InProgress);

        Assert.DoesNotContain(row.Controls.OfType<Label>(), l => l.Text.StartsWith("PR  "));
    }

    [Fact]
    public void Row_HidesThePrLine_WhileBusy()
    {
        using var temp = new TempDirectory();
        using var row = new TaskRow(new JiraTicket("MDP-1", "S", null, null), TicketSection.InProgress, false, JiraUrl,
            new TicketBoard(new TicketStore(temp.Path)), (_, _) => { }, _ => { }, _ => { }, false, _ => { }, "Working...", 900, "BE main ●");

        Assert.DoesNotContain(row.Controls.OfType<Label>(), l => l.Text.StartsWith("PR  "));
    }

    [Fact]
    public void Row_StopsTheSummaryBeforeTheButtons()
    {
        var summaryText = new string('x', 200);
        using var temp = new TempDirectory();
        using var row = NewRow(temp, new JiraTicket("MDP-1", summaryText, null, null), TicketSection.InProgress);
        var summary = row.Controls.OfType<Label>().Single(l => l.Text == summaryText);

        Assert.True(summary.Right <= row.Controls.OfType<MacButton>().Min(mb => mb.Left));
    }

    [Fact]
    public void ButtonsOnOwnLine_WhenButtonsOverHalf()
    {
        Assert.True(TaskRow.ButtonsOnOwnLine(400, 250));
        Assert.False(TaskRow.ButtonsOnOwnLine(600, 250));
    }

    [Fact]
    public void ButtonLines_WrapsWhenTooWide()
    {
        var lines = TaskRow.ButtonLines(381, new[] { 240, 70, 70, 70 }, 12, 6);

        Assert.Equal(new[] { 0, 0, 1, 1 }, lines);
    }

    [Fact]
    public void ButtonLines_OversizedButtonGetsOwnLine()
    {
        var lines = TaskRow.ButtonLines(200, new[] { 300, 50 }, 12, 6);

        Assert.Equal(new[] { 0, 1 }, lines);
    }

    [Fact]
    public void ButtonLines_SingleLineWhenFits()
    {
        var lines = TaskRow.ButtonLines(600, new[] { 70, 70, 70 }, 12, 6);

        Assert.Equal(new[] { 0, 0, 0 }, lines);
    }
}
