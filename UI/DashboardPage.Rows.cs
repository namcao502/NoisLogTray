using System.Drawing;

namespace NoisLogTray;

// Row builders for the two ticket tables, each with its own columns.
internal sealed partial class DashboardPage
{
    private static readonly TaskTableColumn[] InProgressColumns =
    {
        TaskTableColumn.Key, TaskTableColumn.Summary, TaskTableColumn.Status, TaskTableColumn.Due, TaskTableColumn.PrMain,
        TaskTableColumn.PrDev,
    };
    private static readonly TaskTableColumn[] NewTicketColumns =
    {
        TaskTableColumn.Key, TaskTableColumn.Summary, TaskTableColumn.Status, TaskTableColumn.Due, TaskTableColumn.Eta,
        TaskTableColumn.Priority,
    };

    private string JiraUrl(string ticketKey) => $"{_jiraBaseUrl}/browse/{ticketKey}";

    // The ticket's own cell, or the stage while a Start / Open / Ready to Test runs on it.
    private TableCell StatusCell(JiraTicket ticket, TicketSection section)
    {
        if (_busyTickets.TryGetValue(ticket.Key, out var stage)) return new TableCell(TaskTableColumn.Status, TasksPage.StageText(stage), Theme.Accent);
        var (text, color) = TaskRow.Cell(ticket, section, TaskTableColumn.Status);
        var isDoneInJira = section == TicketSection.InProgress && ticket.IsStatusDone;
        return new TableCell(TaskTableColumn.Status, text, color,
            isDoneInJira ? Lang.T("Done in Jira - move to Done", "Jira đã Done - chuyển sang Xong") : null);
    }

    private static TableCell TicketCell(JiraTicket ticket, TaskTableColumn column)
    {
        var (text, color) = TaskRow.Cell(ticket, TicketSection.InProgress, column);
        return new TableCell(column, text, color);
    }

    private DashboardRow InProgressRow(JiraTicket ticket, IReadOnlyList<TicketPr> prs, int width, bool striped)
    {
        var busy = _busyTickets.ContainsKey(ticket.Key);
        var cells = new List<TableCell> { StatusCell(ticket, TicketSection.InProgress), TicketCell(ticket, TaskTableColumn.Due) };
        foreach (var column in new[] { TaskTableColumn.PrMain, TaskTableColumn.PrDev })
        {
            cells.Add(busy ? new TableCell(column, TaskRow.EmptyCell, Theme.TextSecondary) : TaskRow.PrCell(prs, column));
        }

        var queued = _data != null && _data.TodayQueuedKeys.Contains(ticket.Key);
        var queueText = queued ? Lang.T("Queued", "Đã queue") : "Queue";
        var buttons = new List<(string Text, Color? Tint, bool Enabled, Action OnClick, string? Glyph)>
        {
            (queueText, null, !busy && !queued, () => RaiseQueueRequest(new[] { ticket.Key }), null),
            (Lang.T("Open", "Mở"), Theme.Accent, !busy, () => RaiseTicketRequest(OpenRequested, ticket.Key, "Open"), null),
        };
        return new DashboardRow(ticket.Key, JiraUrl(ticket.Key), ticket.Summary, InProgressColumns, cells, buttons, width, striped);
    }

    private DashboardRow NewTicketRow(JiraTicket ticket, int width, bool striped)
    {
        var busy = _busyTickets.ContainsKey(ticket.Key);
        var cells = new List<TableCell>
        {
            StatusCell(ticket, TicketSection.New),
            TicketCell(ticket, TaskTableColumn.Due),
            TicketCell(ticket, TaskTableColumn.Eta),
            TicketCell(ticket, TaskTableColumn.Priority),
        };
        var buttons = new List<(string Text, Color? Tint, bool Enabled, Action OnClick, string? Glyph)>
        {
            (Lang.T("Start", "Bắt đầu"), Theme.ActionStart, !busy, () => RaiseTicketRequest(StartRequested, ticket.Key, "Start"), null),
        };
        return new DashboardRow(ticket.Key, JiraUrl(ticket.Key), ticket.Summary, NewTicketColumns, cells, buttons, width, striped);
    }
}
