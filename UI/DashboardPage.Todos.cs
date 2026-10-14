using System.Drawing;

namespace NoisLogTray;

// The to-do card: DashboardTodos decides what is on it, these rows word it and wire each suggestion's buttons.
internal sealed partial class DashboardPage
{
    // force: theme, language, width or a ticket's stage changed, which the signature does not cover.
    private void RenderTodos(bool force = false)
    {
        if (IsDisposed) return;
        if (!Visible)
        {
            _todosPending = true;
            return;
        }
        _todosPending = false;
        var todos = _data == null
            ? new List<TodoItem>()
            : DashboardTodos.Build(_data, _claudeWaits, _week, _cleanupKeys, _busyTickets.Keys.ToHashSet(), Hcm.Now(), CurrentLogTime(),
                _isTodayWritten, _draining);
        _todoTitle.Text = todos.Count == 0 ? Lang.T("TO DO", "VIỆC CẦN LÀM") : Lang.T($"TO DO ({todos.Count})", $"VIỆC CẦN LÀM ({todos.Count})");
        var signature = DashboardTodos.Signature(todos);
        if (!force && signature == _renderedTodoSignature && _todoList.ClientSize.Width == _todoRenderedWidth) return;
        _renderedTodoSignature = signature;
        _todoRenderedWidth = _todoList.ClientSize.Width;
        var rowWidth = RowWidth(_todoList);
        FillList(_todoList, todos.Select((ti, index) => TodoRowFor(ti, rowWidth, index % 2 == 1)).ToList(),
            Lang.T("Nothing to do right now", "Không có việc gì cần làm"));
    }

    private TodoRow TodoRowFor(TodoItem item, int width, bool striped)
    {
        var ticket = item.Ticket;
        var busy = ticket != null && _busyTickets.ContainsKey(ticket.Key);
        // A busy ticket's detail line shows its stage; its buttons wait.
        var detail = busy && _busyTickets.TryGetValue(ticket!.Key, out var stage) ? TasksPage.StageText(stage) : ticket?.Summary ?? string.Empty;
        var buttons = new List<(string Text, Color? Tint, bool Enabled, Action OnClick, string? Glyph)>();
        string title;
        string icon;
        Color tone;
        switch (item.Kind)
        {
            case TodoKind.ClaudeWaiting:
            {
                var wait = item.Wait!;
                var isTerminal = TerminalTabs.IsTerminalSession(wait.SessionName);
                var reasonText = wait.Reason switch
                {
                    ClaudeWaitReason.Permission => Lang.T("Claude needs your permission", "Claude cần bạn cấp quyền"),
                    ClaudeWaitReason.Question => Lang.T("Claude is asking you something", "Claude đang hỏi bạn"),
                    _ => Lang.T("Claude is done - your turn", "Claude đã xong, tới lượt bạn"),
                };
                title = isTerminal ? $"{wait.Label} · {reasonText}" : reasonText;
                // A ticket's summary when it is in progress here; else where to answer.
                var waitTicket = isTerminal ? null : _data?.InProgress.Select(ip => ip.Ticket).FirstOrDefault(jt => jt.Key == wait.SessionName);
                detail = waitTicket?.Summary ?? Lang.T("Open the tab to answer", "Mở tab để trả lời");
                icon = Icons.WindowConsole;
                tone = Theme.Accent;
                buttons.Add((Lang.T("Open", "Mở"), Theme.Accent, true, () => RaiseDeferred(() => ClaudeSessionRequested?.Invoke(wait.SessionName)), null));
                return new TodoRow(isTerminal ? null : wait.SessionName, isTerminal ? null : JiraUrl(wait.SessionName), icon, tone, title,
                    detail, buttons, width, striped);
            }
            case TodoKind.ReadyToTest:
            {
                title = Lang.T("dev PR merged", "PR dev đã merge");
                if (!busy) detail = $"{ticket!.Summary} · Jira: {ticket.Status}";
                icon = Icons.Beaker;
                tone = Theme.ActionReadyToTest;
                buttons.Add((ButtonWidths.ReadyToTestText, Theme.ActionReadyToTest, !busy,
                    () => RaiseTicketRequest(ReadyToTestRequested, ticket!.Key, "Ready to Test"), null));
                break;
            }
            case TodoKind.PrComments:
            {
                var count = item.CommentCount;
                title = Lang.T($"{count} new PR {(count == 1 ? "comment" : "comments")}", $"{count} comment PR mới");
                icon = Icons.Comment;
                tone = Theme.Accent;
                // Listed right to left: Seen at the edge, View PR before it.
                buttons.Add((Lang.T("Seen", "Đã xem"), null, !busy, () => RaiseTicketAction(CommentsSeen, ticket!.Key), null));
                var prUrl = TaskRow.WebUrl(item.PrUrl);
                if (prUrl != null)
                {
                    buttons.Add((Lang.T("View PR", "Xem PR"), Theme.Accent, !busy, () =>
                    {
                        TaskRow.OpenUrl(prUrl);
                        RaiseTicketAction(CommentsSeen, ticket!.Key);
                    }, null));
                }
                break;
            }
            case TodoKind.JiraDone:
            {
                title = $"Jira: {ticket!.Status}";
                icon = Icons.CheckmarkCircle;
                tone = Orange;
                buttons.Add((Lang.T("Done", "Xong"), Theme.ActionDone, !busy, () => RaiseTicketAction(MarkDoneRequested, ticket.Key), Icons.ArrowRight));
                break;
            }
            case TodoKind.Overdue:
            {
                var due = TaskRow.ShortDate(ticket!.DueDate);
                title = Lang.T($"overdue since {due}", $"quá hạn từ {due}");
                icon = Icons.Clock;
                tone = Red;
                buttons.Add(item.Section == TicketSection.New
                    ? (Lang.T("Start", "Bắt đầu"), Theme.ActionStart, !busy, () => RaiseTicketRequest(StartRequested, ticket.Key, "Start"), null)
                    : (Lang.T("Open", "Mở"), Theme.Accent, !busy, () => RaiseTicketRequest(OpenRequested, ticket.Key, "Open"), null));
                break;
            }
            case TodoKind.QueueToday:
            {
                var ticketKeys = item.TicketKeys!;
                title = Lang.T("Nothing queued for today", "Hôm nay chưa queue ticket nào");
                detail = Lang.T($"{ticketKeys.Count} in progress: {string.Join(", ", ticketKeys)}",
                    $"{ticketKeys.Count} ticket đang làm: {string.Join(", ", ticketKeys)}");
                icon = Icons.TaskList;
                tone = Theme.Accent;
                buttons.Add(("Queue", Theme.Accent, true, () => RaiseQueueRequest(ticketKeys), null));
                break;
            }
            case TodoKind.UnloggedDay:
            {
                var day = item.Day!;
                var dayText = day.Date.ToString(Lang.Vietnamese ? "ddd dd/MM" : "ddd MMM d", Lang.Culture);
                title = $"{dayText} · {MissingText(day)}";
                detail = Lang.T("Opens Work on that day", "Mở tab Work ở ngày này");
                icon = Icons.CalendarLtr;
                tone = WeekCard.Amber;
                buttons.Add(("Log", Theme.Accent, true, () => RaiseDeferred(() => LogDayRequested?.Invoke(day.Date)), null));
                break;
            }
            case TodoKind.WorktreeCleanup:
            {
                var ticketKeys = item.TicketKeys!;
                title = Lang.T($"{ticketKeys.Count} {(ticketKeys.Count == 1 ? "worktree" : "worktrees")} of shipped tickets",
                    $"{ticketKeys.Count} worktree của ticket đã ship");
                detail = string.Join(", ", ticketKeys);
                icon = Icons.Broom;
                tone = Theme.TextSecondary;
                buttons.Add((Lang.T("Clean up", "Dọn dẹp"), null, true, () => RaiseDeferred(() => CleanupRequested?.Invoke()), null));
                break;
            }
            default:
            {
                throw new ArgumentOutOfRangeException(nameof(item), item.Kind, null);
            }
        }
        return new TodoRow(ticket?.Key, ticket == null ? null : JiraUrl(ticket.Key), icon, tone, title, detail, buttons, width, striped);
    }

    // What a day still lacks: its OFF marker, or the TSC cell and the HRM hours that are short.
    private static string MissingText(DayCoverage day)
    {
        if (day.IsOff) return Lang.T("day off not marked OFF", "nghỉ chưa đánh dấu OFF");
        var (hrmColor, _) = WeekCard.HrmStatus(day, future: false);
        var (tscColor, _) = WeekCard.TscStatus(day, future: false);
        var missing = new List<string>();
        if (day.TscState == TscReadState.Read && tscColor != WeekCard.Green) missing.Add(Lang.T("TSC empty", "TSC trống"));
        if (hrmColor != WeekCard.Green) missing.Add(day.HrmHours == null ? Lang.T("HRM unknown", "HRM không rõ") : $"HRM {day.HrmHours:0.#}h / {FullDayHours:0}h");
        return string.Join(", ", missing);
    }

    // Deferred like the ticket requests: the queue write ends in a Reload that rebuilds the clicked row.
    private void RaiseQueueRequest(IReadOnlyList<string> ticketKeys)
    {
        var queueKeys = ticketKeys.Where(key => !_busyTickets.ContainsKey(key)).ToList();
        if (queueKeys.Count == 0) return;
        BeginInvoke(() => QueueTicketsRequested?.Invoke(queueKeys));
    }
}
