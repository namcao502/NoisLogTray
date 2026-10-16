using System.Drawing;
using NoisLogTray;

namespace NoisLogTray.Tests;

[Collection("TicketRowControls")]
public class TodoRowTests
{
    private static readonly TeamsAttentionItem TeamsMessage = new("MDP-1", "chat|1", "19:abc@thread.v2", "MDP-1 Cart", "An",
        new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.FromHours(7)), false);

    private static TodoRow TeamsRow(DashboardPage page)
    {
        return page.TodoRowFor(new TodoItem(TodoKind.TeamsMessage, TeamsMessage: TeamsMessage), 600, false);
    }

    private static TodoRow NewRow(IReadOnlyList<(string, Color?, bool, Action<Control>, string?)> buttons, string? ticketKey = "MDP-1",
        bool striped = false) =>
        new(ticketKey, ticketKey == null ? null : "https://x/browse/" + ticketKey, Icons.Clock, TaskRow.Red, "overdue since Oct 1",
            "Fix the export", buttons, 600, striped);

    private static List<MacButton> MacButtons(TodoRow row) => row.Controls.OfType<MacButton>().ToList();

    [Fact]
    public void Buttons_SitAtTheRightEdge_LeftToRight_WithTheirTintAndState()
    {
        using var row = NewRow(new (string, Color?, bool, Action<Control>, string?)[]
        {
            ("Seen", null, false, _ => { }, null),
            ("View PR", Theme.Accent, true, _ => { }, null),
        });

        var buttons = MacButtons(row);
        // Listed right-to-left; added leftmost first, so Tab walks them left to right.
        Assert.Equal(new[] { "View PR", "Seen" }, buttons.Select(mb => mb.Text));
        Assert.Equal(new Color?[] { Theme.Accent, null }, buttons.Select(mb => mb.Tint));
        Assert.Equal(new[] { true, false }, buttons.Select(mb => mb.Enabled));
        Assert.True(buttons[0].Right < buttons[1].Left);
        Assert.True(buttons[1].Right <= row.Width && buttons[1].Right > row.Width - UiScale.S(20));
    }

    [Fact]
    public void TodoRow_TeamsMessage_ShowsOpenAndSeen()
    {
        using var page = new DashboardPage(null, null, "https://x", () => @"C:\missing", () => Array.Empty<string>());
        using var row = TeamsRow(page);

        var buttons = MacButtons(row);
        Assert.Equal(2, buttons.Count);
        Assert.Contains(buttons, mb => mb.Text == "Open" || mb.Text == "Mở");
        Assert.Contains(buttons, mb => mb.Text == "Seen" || mb.Text == "Đã xem");
        Assert.Contains("09:30", row.AccessibleName);
    }

    [Fact]
    public void TodoRow_TeamsMessage_RaisesSeenWithConversationKey()
    {
        using var page = new DashboardPage(null, null, "https://x", () => @"C:\missing", () => Array.Empty<string>());
        _ = page.Handle;
        string? seenKey = null;
        DateTimeOffset? seenAt = null;
        page.TeamsSeenRequested += (conversationKey, sentAt) =>
        {
            seenKey = conversationKey;
            seenAt = sentAt;
        };
        using var row = TeamsRow(page);

        MacButtons(row).Single(mb => mb.Text == "Seen" || mb.Text == "Đã xem").PerformClick();
        Application.DoEvents();

        Assert.Equal("chat|1", seenKey);
        Assert.Equal(TeamsMessage.SentAt, seenAt);
    }

    [Fact]
    public void Row_ThrowsOnMoreThanTwoButtons()
    {
        var buttons = Enumerable.Range(0, 3).Select(index => ($"B{index}", (Color?)null, true, (Action<Control>)(_ => { }), (string?)null)).ToList();

        Assert.Throws<ArgumentException>(() => NewRow(buttons));
    }

    [Fact]
    public void Row_Striped_FillsItsBackground_AndItsButtonsBlendIntoIt()
    {
        using var row = NewRow(new (string, Color?, bool, Action<Control>, string?)[] { ("Open", Theme.Accent, true, _ => { }, null) }, striped: true);

        Assert.Equal(Theme.RowStripe(Theme.CardSurface), row.BackColor);
        Assert.Equal(row.BackColor, MacButtons(row).Single().Surface!());
    }

    [Fact]
    public void Row_LinksTheTicketKey_OnlyWhenItHasOne()
    {
        using var ticketRow = NewRow(Array.Empty<(string, Color?, bool, Action<Control>, string?)>());
        using var dayRow = NewRow(Array.Empty<(string, Color?, bool, Action<Control>, string?)>(), ticketKey: null);

        Assert.Equal("MDP-1", ticketRow.Controls.OfType<LinkLabel>().Single().Text);
        Assert.Empty(dayRow.Controls.OfType<LinkLabel>());
        Assert.Contains("overdue since Oct 1", ticketRow.AccessibleName);
        Assert.Contains("Fix the export", ticketRow.AccessibleName);
    }
}
