using System.Drawing;

namespace NoisLogTray;

// Row builders for the three ticket lists.
internal sealed partial class DashboardPage
{
    private string JiraUrl(string ticketKey) => $"{_jiraBaseUrl}/browse/{ticketKey}";

    private DashboardRow InProgressRow(JiraTicket ticket, string prSummary, int width)
    {
        var busy = _busyTickets.TryGetValue(ticket.Key, out var stage);
        var facts = new List<(string Text, Color Color)>();
        if (busy)
            facts.Add((TasksPage.StageText(stage), Theme.TextSecondary));
        else
            AddDueFact(facts, ticket);

        var queued = _data != null && _data.TodayQueuedKeys.Contains(ticket.Key);
        var queueText = queued ? Lang.T("Queued", "Đã queue") : "Queue";
        var buttons = new List<(string Text, Color? Tint, bool Enabled, Action OnClick)>
        {
            (queueText, null, !busy && !queued, () => RaiseTicketAction(QueueTicketRequested, ticket.Key)),
            (Lang.T("Open", "Mở"), Theme.Accent, !busy, () => RaiseTicketRequest(OpenRequested, ticket.Key, "Open")),
        };
        return new DashboardRow(ticket.Key, JiraUrl(ticket.Key), ticket.Summary, facts, busy ? "" : prSummary, buttons, width);
    }

    private DashboardRow AttentionRow(AttentionItem item, int width)
    {
        var ticket = item.Ticket;
        var busy = _busyTickets.TryGetValue(ticket.Key, out var stage);
        var facts = new List<(string Text, Color Color)>();
        var buttons = new List<(string Text, Color? Tint, bool Enabled, Action OnClick)>();
        var linkUrl = JiraUrl(ticket.Key);
        var status = string.IsNullOrEmpty(ticket.Status) ? "-" : ticket.Status;

        switch (item.Kind)
        {
            case AttentionKind.ReadyToTest:
                facts.Add((Lang.T($"dev PR merged · Jira: {status}", $"PR dev đã merge · Jira: {status}"), WeekCard.Green));
                buttons.Add((ButtonWidths.ReadyToTestText, Theme.ActionReadyToTest, !busy, () => RaiseTicketRequest(ReadyToTestRequested, ticket.Key, "Ready to Test")));
                break;
            case AttentionKind.Overdue:
                facts.Add((Lang.T($"Overdue {TaskRow.ShortDate(ticket.DueDate)}", $"Quá hạn {TaskRow.ShortDate(ticket.DueDate)}"), Red));
                break;
            case AttentionKind.PrComments:
                facts.Add((Lang.T($"{item.CommentCount} new PR {(item.CommentCount == 1 ? "comment" : "comments")}",
                    $"{item.CommentCount} comment PR mới"), Theme.Accent));
                if (!string.IsNullOrEmpty(item.PrUrl)) linkUrl = item.PrUrl;
                buttons.Add((Lang.T("Seen", "Đã xem"), null, !busy, () => RaiseTicketAction(CommentsSeen, ticket.Key)));
                break;
            case AttentionKind.JiraDone:
                facts.Add((Lang.T($"Jira: {status} -- move to Done?", $"Jira: {status} -- chuyển sang Done?"), Orange));
                buttons.Add((Lang.T("→ Done", "→ Xong"), Theme.ActionDone, !busy, () => RaiseTicketAction(MarkDoneRequested, ticket.Key)));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(item), item.Kind, null);
        }

        if (busy)
        {
            facts.Clear();
            facts.Add((TasksPage.StageText(stage), Theme.TextSecondary));
        }
        return new DashboardRow(ticket.Key, linkUrl, ticket.Summary, facts, "", buttons, width);
    }

    private DashboardRow NewTicketRow(JiraTicket ticket, int width)
    {
        var busy = _busyTickets.TryGetValue(ticket.Key, out var stage);
        var facts = new List<(string Text, Color Color)>();
        if (busy)
            facts.Add((TasksPage.StageText(stage), Theme.TextSecondary));
        else
            AddDueFact(facts, ticket);

        var buttons = new List<(string Text, Color? Tint, bool Enabled, Action OnClick)>
        {
            (Lang.T("Start", "Bắt đầu"), Theme.ActionStart, !busy, () => RaiseTicketRequest(StartRequested, ticket.Key, "Start")),
        };
        return new DashboardRow(ticket.Key, JiraUrl(ticket.Key), ticket.Summary, facts, "", buttons, width);
    }

    private static void AddDueFact(List<(string Text, Color Color)> facts, JiraTicket ticket)
    {
        var dueColor = ticket.GetDueUrgency(Hcm.Today()) switch
        {
            DueUrgency.Overdue => Red,
            DueUrgency.Soon => Orange,
            _ => Theme.TextSecondary,
        };
        facts.Add(($"{Lang.T("Due", "Hạn")} {TaskRow.ShortDate(ticket.DueDate)}", dueColor));
    }
}
