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

        Assert.Equal(new Color?[] { Theme.ActionDone, Theme.Accent, Theme.ActionReadyToTest }, inProgress.Controls.OfType<MacButton>().Select(mb => mb.Tint));
        Assert.Equal(new Color?[] { Theme.ActionStart }, pending.Controls.OfType<MacButton>().Select(mb => mb.Tint));
    }

    [Fact]
    public void Dismiss_IsAnXAtTheRightEdge_AndRestoreStaysAButton()
    {
        using var temp = new TempDirectory();
        using var row = NewRow(temp, new JiraTicket("MDP-1", "S", null, null), TicketSection.New);
        using var dismissed = new TaskRow(new JiraTicket("MDP-2", "S", null, null), TicketSection.New, true, JiraUrl,
            new TicketBoard(new TicketStore(temp.Path)), (_, _) => { }, _ => { }, _ => { }, false, _ => { }, null, 900);

        var dismiss = row.Controls.OfType<CloseButton>().Single();
        Assert.Equal("Dismiss MDP-1", dismiss.AccessibleName);
        Assert.True(dismiss.Left > row.Controls.OfType<MacButton>().Max(mb => mb.Left));
        Assert.Empty(dismissed.Controls.OfType<CloseButton>());
        Assert.Equal("Restore", dismissed.Controls.OfType<MacButton>().Single().Text);
    }

    [Fact]
    public void Row_Striped_FillsItsBackground_AndItsButtonsBlendIntoIt()
    {
        using var temp = new TempDirectory();
        using var plain = NewRow(temp, new JiraTicket("MDP-1", "S", null, null), TicketSection.InProgress);
        using var striped = new TaskRow(new JiraTicket("MDP-2", "S", null, null), TicketSection.InProgress, false, JiraUrl,
            new TicketBoard(new TicketStore(temp.Path)), (_, _) => { }, _ => { }, _ => { }, false, _ => { }, null, 900, striped: true);

        Assert.Equal(Color.Transparent, plain.BackColor);
        Assert.Equal(Theme.RowStripe(Theme.CardSurface), striped.BackColor);
        Assert.All(striped.Controls.OfType<MacButton>(), mb => Assert.Equal(striped.BackColor, mb.Surface!()));
        Assert.Equal(striped.BackColor, striped.Controls.OfType<CloseButton>().Single().Surface());
    }

    [Fact]
    public void RowStripe_IsANearShadeOfTheSurface()
    {
        var stripe = Theme.RowStripe(Theme.CardSurface);

        Assert.NotEqual(Theme.CardSurface.ToArgb(), stripe.ToArgb());
        Assert.True(Math.Abs(stripe.R - Theme.CardSurface.R) <= 16);
        Assert.Equal(255, stripe.A);
    }

    [Fact]
    public void Facts_GiveEachFactItsOwnTone_AndGrayWhenItIsEmpty()
    {
        var worked = new JiraTicket("MDP-1", "S", "2099-01-01", "2099-01-02", 3, "PR & Code Review", 3600);
        var empty = new JiraTicket("MDP-2", "S", null, null);

        var workedTones = TaskRow.Facts(worked, TicketSection.InProgress).Select(f => f.Color).ToList();
        var emptyTones = TaskRow.Facts(empty, TicketSection.New).Select(f => f.Color).ToList();

        Assert.Equal(workedTones.Count, workedTones.Distinct().Count());
        Assert.All(emptyTones, tone => Assert.Equal(TaskRow.Gray, tone));
    }

    [Fact]
    public void Facts_LineUpInColumnsAcrossRows_WithTheStatusLast()
    {
        using var temp = new TempDirectory();
        // Wide enough that the facts beside the buttons hold the long status on one line.
        using var full = NewRow(temp, new JiraTicket("MDP-1", "S", "2026-05-28", "2026-09-14", 2.5, "PR & Code Review", 9000), TicketSection.InProgress, width: 1200);
        using var empty = NewRow(temp, new JiraTicket("MDP-2", "S", null, null, Status: "Hold"), TicketSection.InProgress, width: 1200);

        var fullTags = FactTags(full);
        var emptyTags = FactTags(empty);

        Assert.Equal(fullTags.Select(ft => ft.Left), emptyTags.Select(ft => ft.Left));
        Assert.Equal("PR & Code Review", fullTags[^1].Text);
        Assert.Equal("Hold", emptyTags[^1].Text);
    }

    private static List<FactTag> FactTags(TaskRow taskRow) =>
        taskRow.Controls.OfType<FlowLayoutPanel>().Single(flp => flp.Name == TaskRow.FactsName).Controls.OfType<FactTag>().ToList();

    [Theory]
    [InlineData("BE main ✓ · dev ✕", "green")]
    [InlineData("BE main ✓ · dev ●", "blue")]
    [InlineData("FE Portal dev ?", "gray")]
    public void PrTone_IsGreenOnceClosed_BlueWhileOpen_GrayWhenUnknown(string repoSummary, string expected)
    {
        var tones = new Dictionary<string, Color> { ["green"] = WeekCard.Green, ["blue"] = Theme.Accent, ["gray"] = TaskRow.Gray };

        Assert.Equal(tones[expected], TaskRow.PrTone(repoSummary));
    }

    [Fact]
    public void PrTags_HoldOneTagPerRepo()
    {
        using var tags = TaskRow.PrTags("BE main ✓ · dev ● | FE Portal dev ✓", new Point(0, 0), 600);

        Assert.Equal(new[] { "BE main ✓ · dev ●", "FE Portal dev ✓" }, tags.Controls.OfType<FactTag>().Select(ft => ft.Text));
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
    public void Row_CentersTheButtonsOnItsHeight_RightOfTheLines_AndThePrsUnderTheFacts()
    {
        using var temp = new TempDirectory();
        using var row = NewRow(temp, new JiraTicket("MDP-1", "S", "2026-10-14", "2026-10-15"), TicketSection.InProgress, prSummary: "BE main ●");
        var facts = row.Controls.OfType<FlowLayoutPanel>().Single(flp => flp.Name == TaskRow.FactsName);
        var prLine = row.Controls.OfType<FlowLayoutPanel>().Single(flp => flp.Name == TaskRow.PrTagsName);

        Assert.All(row.Controls.OfType<Button>(), b =>
        {
            Assert.InRange(b.Top + b.Height / 2, row.Height / 2 - 1, row.Height / 2 + 1);
            Assert.True(b.Left >= facts.Right && b.Left >= prLine.Right);
        });
        Assert.True(prLine.Top >= facts.Bottom);
    }

    [Fact]
    public void Row_PutsTheButtonsUnderThePrs_WhenNarrow()
    {
        using var temp = new TempDirectory();
        using var row = NewRow(temp, new JiraTicket("MDP-1", "S", null, null), TicketSection.InProgress, width: 300, prSummary: "BE main ●");
        var prLine = row.Controls.OfType<FlowLayoutPanel>().Single(flp => flp.Name == TaskRow.PrTagsName);

        Assert.All(row.Controls.OfType<MacButton>(), mb => Assert.True(mb.Top >= prLine.Bottom));
    }

    [Fact]
    public void Row_HasNoPrLine_WithoutPrs()
    {
        using var temp = new TempDirectory();
        using var row = NewRow(temp, new JiraTicket("MDP-1", "S", null, null), TicketSection.InProgress);

        Assert.DoesNotContain(row.Controls.OfType<FlowLayoutPanel>(), flp => flp.Name == TaskRow.PrTagsName);
    }

    [Fact]
    public void Row_HidesThePrLine_WhileBusy()
    {
        using var temp = new TempDirectory();
        using var row = new TaskRow(new JiraTicket("MDP-1", "S", null, null), TicketSection.InProgress, false, JiraUrl,
            new TicketBoard(new TicketStore(temp.Path)), (_, _) => { }, _ => { }, _ => { }, false, _ => { }, "Working...", 900, "BE main ●");

        Assert.DoesNotContain(row.Controls.OfType<FlowLayoutPanel>(), flp => flp.Name == TaskRow.PrTagsName);
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

    [Theory]
    [InlineData("https://dev.azure.com/org/_git/repo/pullrequest/54989", "https://dev.azure.com/org/_git/repo/pullrequest/54989")]
    [InlineData("http://example.com", "http://example.com/")]
    public void WebUrl_KeepsAnHttpOrHttpsUrl(string text, string expected)
    {
        Assert.Equal(expected, TaskRow.WebUrl(text));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData("C:\\Windows\\notepad.exe")]
    [InlineData("docs/plan.md")]
    [InlineData("")]
    [InlineData(null)]
    public void WebUrl_RejectsAnythingButAWebLink(string? text)
    {
        Assert.Null(TaskRow.WebUrl(text));
    }

    [Fact]
    public void LogButton_IsLeftmost_AndHandsOverTheKeyAndSummary()
    {
        using var temp = new TempDirectory();
        (string Key, string Summary)? logged = null;
        using var row = new TaskRow(new JiraTicket("MDP-1", "Fix it", null, null), TicketSection.InProgress, false, JiraUrl,
            new TicketBoard(new TicketStore(temp.Path)), (_, _) => { }, _ => { }, _ => { }, false, _ => { }, null, 900,
            onLog: (key, summary) => logged = (key, summary));

        var buttons = row.Controls.OfType<MacButton>().ToList();
        var log = buttons.Single(mb => mb.Text == "Log");
        Assert.Equal(buttons.Min(mb => mb.Left), log.Left);
        Assert.Equal(Icons.Add, log.Glyph);
        Assert.True(log.Enabled);
        log.PerformClick();
        Assert.Equal(("MDP-1", "Fix it"), logged);
        Assert.Null(row.Controls[TaskRow.InEntryBarName]);
    }

    [Fact]
    public void InEntry_DisablesTheLogButton_AndMarksTheRow()
    {
        using var temp = new TempDirectory();
        using var row = new TaskRow(new JiraTicket("MDP-1", "S", null, null), TicketSection.New, false, JiraUrl,
            new TicketBoard(new TicketStore(temp.Path)), (_, _) => { }, _ => { }, _ => { }, false, _ => { }, null, 900,
            onLog: (_, _) => { }, isInEntry: true);

        var inEntry = row.Controls.OfType<MacButton>().Single(mb => mb.Text == "In entry");
        Assert.False(inEntry.Enabled);
        Assert.Equal(Icons.Checkmark, inEntry.Glyph);
        Assert.NotNull(row.Controls[TaskRow.InEntryBarName]);
    }

    [Fact]
    public void LogButton_IsNotOnDismissedRows()
    {
        using var temp = new TempDirectory();
        using var dismissed = new TaskRow(new JiraTicket("MDP-1", "S", null, null), TicketSection.New, true, JiraUrl,
            new TicketBoard(new TicketStore(temp.Path)), (_, _) => { }, _ => { }, _ => { }, false, _ => { }, null, 900,
            onLog: (_, _) => { });

        Assert.Equal("Restore", dismissed.Controls.OfType<MacButton>().Single().Text);
    }
}
