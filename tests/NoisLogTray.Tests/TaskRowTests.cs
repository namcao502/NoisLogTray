using System.Drawing;
using NoisLogTray;

namespace NoisLogTray.Tests;

// Both row test classes write RowColumns (static, UI thread only) on first use, so they must not run in parallel.
[Collection("TicketRowControls")]
public class TaskRowTests
{
    private const string JiraUrl = "https://example.atlassian.net";

    private static TaskRow NewRow(TempDirectory temp, JiraTicket ticket, TicketSection section, bool isDevPrMerged = false,
        int width = 900, IReadOnlyList<TicketPr>? prs = null) =>
        new(ticket, section, false, JiraUrl, new TicketBoard(new TicketStore(temp.Path)), (_, _) => { }, _ => { }, _ => { },
            isDevPrMerged, _ => { }, null, width, prs);

    private static TicketPr Pr(string repository, string branch, int pullRequestId, string status) =>
        new(repository, branch, pullRequestId, status, 0);

    private static Control Actions(TaskRow row) => row.Controls[TaskRow.ActionsName]!;

    private static List<MacButton> MacButtons(TaskRow row) => Actions(row).Controls.OfType<MacButton>().ToList();

    [Fact]
    public void Buttons_AreTintedByAction()
    {
        using var temp = new TempDirectory();
        using var inProgress = NewRow(temp, new JiraTicket("MDP-1", "S", null, null, Status: "PR & Code Review"), TicketSection.InProgress, isDevPrMerged: true);
        using var pending = NewRow(temp, new JiraTicket("MDP-2", "S", null, null), TicketSection.New);

        // Left to right, the order Tab walks them.
        Assert.Equal(new Color?[] { Theme.ActionReadyToTest, Theme.Accent, Theme.ActionDone }, MacButtons(inProgress).Select(mb => mb.Tint));
        Assert.Equal(new Color?[] { Theme.ActionStart }, MacButtons(pending).Select(mb => mb.Tint));
    }

    [Fact]
    public void Dismiss_IsAnXAtTheRightEdge_AndRestoreStaysAButton()
    {
        using var temp = new TempDirectory();
        using var row = NewRow(temp, new JiraTicket("MDP-1", "S", null, null), TicketSection.New);
        using var dismissed = new TaskRow(new JiraTicket("MDP-2", "S", null, null), TicketSection.New, true, JiraUrl,
            new TicketBoard(new TicketStore(temp.Path)), (_, _) => { }, _ => { }, _ => { }, false, _ => { }, null, 900);

        var dismiss = Actions(row).Controls.OfType<CloseButton>().Single();
        Assert.Equal("Dismiss MDP-1", dismiss.AccessibleName);
        Assert.True(dismiss.Left > MacButtons(row).Max(mb => mb.Left));
        Assert.Empty(Actions(dismissed).Controls.OfType<CloseButton>());
        Assert.Equal("Restore", MacButtons(dismissed).Single().Text);
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
        Assert.Equal(striped.BackColor, Actions(striped).BackColor);
        Assert.All(MacButtons(striped), mb => Assert.Equal(striped.BackColor, mb.Surface!()));
        Assert.Equal(striped.BackColor, Actions(striped).Controls.OfType<CloseButton>().Single().Surface());
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
    public void ShortDate_FormatsAJiraDate_AndKeepsOtherText()
    {
        var formatted = TaskRow.ShortDate("2026-10-15");
        Assert.True(formatted == "Oct 15" || formatted == "15/10", formatted);
        Assert.Equal("-", TaskRow.ShortDate(null));
        Assert.Equal("soon", TaskRow.ShortDate("soon"));
    }

    [Fact]
    public void Row_HidesItsButtons_UntilShowActions()
    {
        using var temp = new TempDirectory();
        using var row = NewRow(temp, new JiraTicket("MDP-1", "S", null, null), TicketSection.InProgress);

        Assert.NotEmpty(MacButtons(row));
        Assert.False(row.ActionsVisible);
        row.ShowActions(true);
        Assert.True(row.ActionsVisible);
        Assert.Equal(row.Width, Actions(row).Right);
    }

    [Fact]
    public void Row_KeepsItsButtons_WhileTheCursorIsStillInside()
    {
        using var temp = new TempDirectory();
        using var row = NewRow(temp, new JiraTicket("MDP-1", "S", null, null), TicketSection.InProgress);

        // Over a button: the row's own MouseLeave has fired, yet the cursor is still inside the row.
        row.RefreshActions(new Point(row.Width - 2, row.Height / 2), hasFocus: false);
        Assert.True(row.ActionsVisible);
        row.RefreshActions(new Point(row.Width + 5, row.Height / 2), hasFocus: false);
        Assert.False(row.ActionsVisible);
    }

    [Fact]
    public void Row_ShowsTheButtons_WhileItHoldsFocus()
    {
        using var temp = new TempDirectory();
        using var row = NewRow(temp, new JiraTicket("MDP-1", "S", null, null), TicketSection.InProgress);

        row.RefreshActions(new Point(-5, -5), hasFocus: true);

        Assert.True(row.ActionsVisible);
    }

    [Fact]
    public void Row_IsOneLineHigh_WhateverItsPrs()
    {
        using var temp = new TempDirectory();
        using var withPrs = NewRow(temp, new JiraTicket("MDP-1", "S", "2026-10-14", "2026-10-15", 2, "PR & Code Review", 3600),
            TicketSection.InProgress, prs: new[]
            {
                Pr("Backend-DotNet", "main", 1, "completed"), Pr("Backend-DotNet", "dev", 2, "active"), Pr("Portal-Angular", "dev", 3, "completed"),
            });
        using var plain = NewRow(temp, new JiraTicket("MDP-2", "S", null, null), TicketSection.New);

        Assert.Equal(plain.Height, withPrs.Height);
        Assert.Empty(withPrs.Controls.OfType<FlowLayoutPanel>());
    }

    [Fact]
    public void LayoutFor_ShowsTheStatusAndPrColumns_OnAWideBoard()
    {
        Assert.Contains(TaskTableColumn.PrDev, TaskRow.LayoutFor(1200).Columns.Keys);
        Assert.Contains(TaskTableColumn.Status, TaskRow.LayoutFor(900).Columns.Keys);
    }

    [Fact]
    public void TableLayoutFor_KeepsOnlyTheGivenColumns()
    {
        var columns = new[] { TaskTableColumn.Key, TaskTableColumn.Summary, TaskTableColumn.Status, TaskTableColumn.Due };

        Assert.Equal(columns.OrderBy(ttc => ttc), TicketTableRow.LayoutFor(900, columns).Columns.Keys.OrderBy(ttc => ttc));
    }

    [Fact]
    public void TipText_GivesACutCellsFullText_ThenItsOwnTip()
    {
        var cell = new TableCell(TaskTableColumn.Status, "Jira: QA Confirmed", TaskRow.Orange, "Done in Jira");

        Assert.Equal("Jira: QA Confirmed" + Environment.NewLine + "Done in Jira", TicketTableRow.TipText(cell, isCut: true));
        Assert.Equal("Done in Jira", TicketTableRow.TipText(cell, isCut: false));
        Assert.Equal("", TicketTableRow.TipText(cell with { ToolTip = null }, isCut: false));
        Assert.Equal("Jira: QA Confirmed", TicketTableRow.TipText(cell with { ToolTip = null }, isCut: true));
    }

    [Fact]
    public void Row_DescribesItsCellsToAScreenReader()
    {
        using var temp = new TempDirectory();
        using var row = NewRow(temp, new JiraTicket("MDP-1", "S", null, null, Status: "PR & Code Review"), TicketSection.InProgress);

        var keyLink = row.Controls.OfType<LinkLabel>().Single();
        Assert.Contains("PR & Code Review", keyLink.AccessibleDescription);
    }

    [Fact]
    public void PrCell_IsEmpty_WhenTheBranchHasNoPr()
    {
        var cell = TaskRow.PrCell(new[] { Pr("Backend-DotNet", "dev", 2, "active") }, TaskTableColumn.PrMain);

        Assert.Equal(new TableCell(TaskTableColumn.PrMain, TaskRow.EmptyCell, Theme.TextSecondary), cell);
        Assert.Equal(TaskRow.EmptyCell, TaskRow.PrCell(Array.Empty<TicketPr>(), TaskTableColumn.PrDev).Text);
    }

    [Fact]
    public void PrCell_IsGreen_OnceEveryRepoMerged_AndNamesEachPrInItsTip()
    {
        var cell = TaskRow.PrCell(new[]
        {
            Pr("Backend-DotNet", "dev", 12, "completed"), Pr("Portal-Angular", "dev", 34, "completed"), Pr("Backend-DotNet", "main", 11, "active"),
        }, TaskTableColumn.PrDev);

        Assert.Equal(TaskTableColumn.PrDev, cell.Column);
        Assert.Equal(WeekCard.Green, cell.Color);
        var tipLines = cell.ToolTip!.Split(Environment.NewLine);
        Assert.Equal(2, tipLines.Length);
        Assert.StartsWith("BE #12: ", tipLines[0]);
        Assert.StartsWith("FE Portal #34: ", tipLines[1]);
    }

    [Fact]
    public void PrCell_CountsMergedRepos_WhileSomeAreStillOpen()
    {
        var partly = TaskRow.PrCell(new[] { Pr("Backend-DotNet", "main", 1, "completed"), Pr("Portal-Angular", "main", 2, "active") },
            TaskTableColumn.PrMain);
        var open = TaskRow.PrCell(new[] { Pr("Backend-DotNet", "main", 1, "active") }, TaskTableColumn.PrMain);

        Assert.StartsWith("1/2 ", partly.Text);
        Assert.Equal(Theme.Accent, partly.Color);
        Assert.Equal(Theme.Accent, open.Color);
        Assert.Equal(TaskRow.PrStatusText("active"), open.Text);
    }

    [Fact]
    public void PrCell_IgnoresAnAbandonedPr_WhileAnotherRepoHasOne()
    {
        var withOther = TaskRow.PrCell(new[] { Pr("Backend-DotNet", "dev", 1, "completed"), Pr("Portal-Angular", "dev", 2, "abandoned") },
            TaskTableColumn.PrDev);
        var onlyAbandoned = TaskRow.PrCell(new[] { Pr("Backend-DotNet", "dev", 1, "abandoned") }, TaskTableColumn.PrDev);

        Assert.Equal(WeekCard.Green, withOther.Color);
        Assert.Contains("FE Portal #2", withOther.ToolTip);
        Assert.Equal(Theme.TextSecondary, onlyAbandoned.Color);
        Assert.NotEqual(TaskRow.EmptyCell, onlyAbandoned.Text);
    }

    [Fact]
    public void Cell_ColorsOnlyWhatNeedsAction()
    {
        var overdue = new JiraTicket("MDP-1", "S", "2020-01-01", null, 1, "In Progress");
        var later = new JiraTicket("MDP-2", "S", "2099-01-01", null, 5, "In Progress");

        Assert.Equal(TaskRow.Red, TaskRow.Cell(overdue, TicketSection.InProgress, TaskTableColumn.Due).Color);
        Assert.Equal(TaskRow.Red, TaskRow.Cell(overdue, TicketSection.InProgress, TaskTableColumn.Priority).Color);
        Assert.Equal("1", TaskRow.Cell(overdue, TicketSection.InProgress, TaskTableColumn.Priority).Text);
        var laterDue = TaskRow.Cell(later, TicketSection.InProgress, TaskTableColumn.Due).Color;
        Assert.NotEqual(TaskRow.Red, laterDue);
        Assert.NotEqual(TaskRow.Orange, laterDue);
        Assert.Equal(TaskRow.EmptyCell, TaskRow.Cell(later, TicketSection.InProgress, TaskTableColumn.Eta).Text);
    }

    [Fact]
    public void Cell_FlagsAStatusJiraAlreadyFinished()
    {
        var finished = new JiraTicket("MDP-1", "S", null, null, Status: "QA Confirmed", StatusCategory: "done");

        Assert.Equal(("QA Confirmed", TaskRow.Orange), TaskRow.Cell(finished, TicketSection.InProgress, TaskTableColumn.Status));
    }

    [Fact]
    public void Row_ShowsTheStageInTheStatusCell_WhileBusy()
    {
        using var temp = new TempDirectory();
        using var row = new TaskRow(new JiraTicket("MDP-1", "S", null, null, Status: "In Progress"), TicketSection.InProgress, false, JiraUrl,
            new TicketBoard(new TicketStore(temp.Path)), (_, _) => { }, _ => { }, _ => { }, false, _ => { }, "Working...", 900,
            new[] { Pr("Backend-DotNet", "main", 1, "active") });

        Assert.Equal("Working...", row.StatusText);
        Assert.All(MacButtons(row), mb => Assert.False(mb.Enabled));
        Assert.Contains($"MAIN {TaskRow.EmptyCell}", row.Controls.OfType<LinkLabel>().Single().AccessibleDescription);
    }

    [Fact]
    public void Row_MarksTheStatus_WhenReadyToTestWaits()
    {
        using var temp = new TempDirectory();
        using var waiting = NewRow(temp, new JiraTicket("MDP-1", "S", null, null, Status: "PR & Code Review"), TicketSection.InProgress, isDevPrMerged: true);
        using var plain = NewRow(temp, new JiraTicket("MDP-2", "S", null, null, Status: "PR & Code Review"), TicketSection.InProgress);

        Assert.True(waiting.HasStatusDot);
        Assert.False(plain.HasStatusDot);
        Assert.Equal("PR & Code Review", plain.StatusText);
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

        var buttons = MacButtons(row);
        var log = buttons.Single(mb => mb.Text == "Log");
        Assert.Equal(buttons.Min(mb => mb.Left), log.Left);
        Assert.Equal(Icons.Add, log.Glyph);
        Assert.True(log.Enabled);
        row.ShowActions(true); // a hidden button cannot be clicked
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

        var inEntry = MacButtons(row).Single(mb => mb.Text == "In entry");
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

        Assert.Equal("Restore", MacButtons(dismissed).Single().Text);
    }
}
