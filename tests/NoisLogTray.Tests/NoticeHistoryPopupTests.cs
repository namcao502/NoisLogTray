using NoisLogTray;

namespace NoisLogTray.Tests;

public class NoticeHistoryPopupTests
{
    private static readonly DateTime Morning = new(2026, 10, 13, 9, 5, 0);

    [Fact]
    public void Row_WrapsALongMessage_ButStopsAtThreeLines()
    {
        using var shortRow = new NoticeHistoryRow(Morning, "Settings saved.", NoticeKind.Success, clickable: false, width: 380);
        using var longRow = new NoticeHistoryRow(Morning, string.Join(" ", Enumerable.Repeat("word", 40)), NoticeKind.Info, clickable: false, width: 380);
        using var hugeRow = new NoticeHistoryRow(Morning, string.Join(" ", Enumerable.Repeat("word", 400)), NoticeKind.Info, clickable: false, width: 380);

        Assert.True(longRow.Height > shortRow.Height);
        Assert.Equal(longRow.Height, hugeRow.Height);
        Assert.True(hugeRow.IsCut);
        Assert.False(shortRow.IsCut);
    }

    [Fact]
    public void Row_CentersItsIcon_OnTheWholeNotice()
    {
        using var oneLine = new NoticeHistoryRow(Morning, "Settings saved.", NoticeKind.Success, clickable: false, width: 380);
        using var twoLines = new NoticeHistoryRow(Morning, string.Join(" ", Enumerable.Repeat("word", 20)), NoticeKind.Info, clickable: false, width: 380);

        foreach (var row in new[] { oneLine, twoLines })
            Assert.Equal(row.Height / 2, row.IconBounds.Top + row.IconBounds.Height / 2);
    }

    [Fact]
    public void Row_PressesOnlyWhenItHasATarget()
    {
        var presses = 0;
        using var clickable = new NoticeHistoryRow(Morning, "MDP-1: Claude is done.", NoticeKind.Info, clickable: true, width: 380);
        using var plain = new NoticeHistoryRow(Morning, "Settings saved.", NoticeKind.Success, clickable: false, width: 380);
        clickable.Pressed += () => presses++;
        plain.Pressed += () => presses++;

        clickable.Press();
        plain.Press();

        Assert.Equal(1, presses);
        Assert.True(clickable.TabStop);
        Assert.False(plain.TabStop);
    }

    [Fact]
    public void SetNotices_ListsNewestFirst_AndAPressRunsThatNoticesTarget()
    {
        var opened = new List<string>();
        using var popup = new NoticeHistoryPopup();
        popup.SetNotices(new (DateTime, string, NoticeKind, Action?)[]
        {
            (Morning, "first", NoticeKind.Info, () => opened.Add("first")),
            (Morning.AddMinutes(1), "second", NoticeKind.Error, () => opened.Add("second")),
        }, width: 380);

        var rows = popup.Rows;
        Assert.Equal(new[] { "second", "first" }, rows.Select(nhr => nhr.Text));
        Assert.Equal(new[] { false, true }, rows.Select(nhr => nhr.Striped));
        rows[1].Press();
        Assert.Equal(new[] { "first" }, opened);
    }

    [Fact]
    public void SetNotices_SaysSoWhenThereAreNone()
    {
        using var popup = new NoticeHistoryPopup();
        popup.SetNotices(Array.Empty<(DateTime, string, NoticeKind, Action?)>(), width: 380);

        Assert.Empty(popup.Rows);
        Assert.True(popup.ShowsEmptyState);
    }
}
