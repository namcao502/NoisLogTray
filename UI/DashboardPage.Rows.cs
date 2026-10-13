using System.Drawing;

namespace NoisLogTray;

// Row builders for the three ticket lists.
internal sealed partial class DashboardPage
{
    private string JiraUrl(string ticketKey) => $"{_jiraBaseUrl}/browse/{ticketKey}";

    private DashboardRow InProgressRow(JiraTicket ticket, string prSummary, int width, bool striped)
    {
        var busy = _busyTickets.TryGetValue(ticket.Key, out var stage);
        var facts = new List<(string Text, Color Color)>();
        if (busy)
            facts.Add((TasksPage.StageText(stage), Theme.Accent));
        else
            facts.Add(TaskRow.DueFact(ticket));

        var queued = _data != null && _data.TodayQueuedKeys.Contains(ticket.Key);
        var queueText = queued ? Lang.T("Queued", "Đã queue") : "Queue";
        var buttons = new List<(string Text, Color? Tint, bool Enabled, Action OnClick, string? Glyph)>
        {
            (queueText, null, !busy && !queued, () => RaiseTicketAction(QueueTicketRequested, ticket.Key), null),
            (Lang.T("Open", "Mở"), Theme.Accent, !busy, () => RaiseTicketRequest(OpenRequested, ticket.Key, "Open"), null),
        };
        return new DashboardRow(ticket.Key, JiraUrl(ticket.Key), ticket.Summary, facts, busy ? "" : prSummary, buttons, width, striped);
    }

    private DashboardRow AttentionRow(AttentionItem item, int width, bool striped)
    {
        var ticket = item.Ticket;
        var busy = _busyTickets.TryGetValue(ticket.Key, out var stage);
        var facts = new List<(string Text, Color Color)>();
        var buttons = new List<(string Text, Color? Tint, bool Enabled, Action OnClick, string? Glyph)>();
        var linkUrl = JiraUrl(ticket.Key);
        var status = string.IsNullOrEmpty(ticket.Status) ? "-" : ticket.Status;

        switch (item.Kind)
        {
            case AttentionKind.ReadyToTest:
                facts.Add((Lang.T($"dev PR merged · Jira: {status}", $"PR dev đã merge · Jira: {status}"), WeekCard.Green));
                buttons.Add((ButtonWidths.ReadyToTestText, Theme.ActionReadyToTest, !busy, () => RaiseTicketRequest(ReadyToTestRequested, ticket.Key, "Ready to Test"), null));
                break;
            case AttentionKind.Overdue:
                facts.Add((Lang.T($"Overdue {TaskRow.ShortDate(ticket.DueDate)}", $"Quá hạn {TaskRow.ShortDate(ticket.DueDate)}"), Red));
                break;
            case AttentionKind.PrComments:
                facts.Add((Lang.T($"{item.CommentCount} new PR {(item.CommentCount == 1 ? "comment" : "comments")}",
                    $"{item.CommentCount} comment PR mới"), Theme.Accent));
                if (!string.IsNullOrEmpty(item.PrUrl)) linkUrl = item.PrUrl;
                buttons.Add((Lang.T("Seen", "Đã xem"), null, !busy, () => RaiseTicketAction(CommentsSeen, ticket.Key), null));
                break;
            case AttentionKind.JiraDone:
                facts.Add((Lang.T($"Jira: {status} -- move to Done?", $"Jira: {status} -- chuyển sang Done?"), Orange));
                buttons.Add((Lang.T("Done", "Xong"), Theme.ActionDone, !busy, () => RaiseTicketAction(MarkDoneRequested, ticket.Key), Icons.ArrowRight));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(item), item.Kind, null);
        }

        if (busy)
        {
            facts.Clear();
            facts.Add((TasksPage.StageText(stage), Theme.Accent));
        }
        return new DashboardRow(ticket.Key, linkUrl, ticket.Summary, facts, "", buttons, width, striped);
    }

    private DashboardRow NewTicketRow(JiraTicket ticket, int width, bool striped)
    {
        var busy = _busyTickets.TryGetValue(ticket.Key, out var stage);
        var facts = new List<(string Text, Color Color)>();
        if (busy)
            facts.Add((TasksPage.StageText(stage), Theme.Accent));
        else
            facts.Add(TaskRow.DueFact(ticket));

        var buttons = new List<(string Text, Color? Tint, bool Enabled, Action OnClick, string? Glyph)>
        {
            (Lang.T("Start", "Bắt đầu"), Theme.ActionStart, !busy, () => RaiseTicketRequest(StartRequested, ticket.Key, "Start"), null),
        };
        return new DashboardRow(ticket.Key, JiraUrl(ticket.Key), ticket.Summary, facts, "", buttons, width, striped);
    }
}
