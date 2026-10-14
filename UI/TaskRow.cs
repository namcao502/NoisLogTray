using System.Diagnostics;
using System.Drawing;
using System.Globalization;

namespace NoisLogTray;

// One ticket on the Work board: a TicketTableRow with every board column, the section's buttons ("+ Log" leftmost,
// Dismiss an X) and, for a ticket already in New entry, an accent bar and "In entry" in place of "+ Log".
internal sealed class TaskRow : TicketTableRow
{
    internal static readonly Color Red = Color.FromArgb(230, 76, 76);
    internal static readonly Color Orange = Color.FromArgb(255, 140, 50);
    internal static readonly Color Gray = Color.FromArgb(150, 150, 156);

    internal const string InEntryBarName = "inEntry";
    internal const string EmptyCell = "–";

    private static readonly Font FactFont = UiScale.Font("Segoe UI", 8.5F);

    private static readonly ThemedToolTip StepsTip = new();

    // Every column, left to right; the Dashboard's tables show fewer.
    internal static readonly IReadOnlyList<TaskTableColumn> BoardColumns = Enum.GetValues<TaskTableColumn>();

    // What the status cell draws: the Jira status, or the stage while the row is busy.
    internal string StatusText { get; }

    // The row still needs action (Ready to Test, or Jira already Done) though its buttons are hidden.
    internal bool HasStatusDot { get; }

    // isDismissed overrides section (Restore only); onMoved's flag says a keyboard press triggered the move.
    // A non-null busyText disables every button and takes the status cell.
    // onLog (key, summary) adds the "+ Log" button, which isInEntry turns into a disabled "In entry".
    internal TaskRow(JiraTicket ticket, TicketSection section, bool isDismissed, string jiraBaseUrl,
        TicketBoard board, Action<string, bool> onMoved, Action<string> onStart, Action<string> onOpen,
        bool isDevPrMerged, Action<string> onReadyToTest, string? busyText, int width, IReadOnlyList<TicketPr>? prs = null, bool striped = false,
        Action<string, string>? onLog = null, bool isInEntry = false)
        : base(ticket.Key, $"{jiraBaseUrl}/browse/{ticket.Key}", ticket.Summary, BoardColumns, width, striped)
    {
        var surface = Surface;
        var buttons = BuildButtons(ticket, section, isDismissed, board, onMoved, onStart, onOpen, surface);
        var readyToTestLabel = isDismissed ? null : ReadyToTestLabel(ticket, section, isDevPrMerged);
        if (readyToTestLabel != null)
        {
            // Last in the right-to-left list, so it sits leftmost.
            // Fixed label and width; the Jira steps it will walk are in the tooltip.
            var readyButton = MacButton.Tinted(ButtonWidths.ReadyToTestText, Theme.ActionReadyToTest);
            readyButton.Surface = () => surface;
            readyButton.Size = new Size(ButtonWidths.ReadyToTestWidth(readyButton.Font), ButtonH);
            readyButton.AccessibleName = $"{readyToTestLabel} {ticket.Key}";
            StepsTip.SetUntilDisposed(readyButton, readyToTestLabel);
            readyButton.Click += (_, _) => onReadyToTest(ticket.Key);
            buttons.Add(readyButton);
        }
        MacButton? logButton = null;
        if (onLog != null && !isDismissed)
        {
            logButton = MacButton.Secondary(isInEntry ? Lang.T("In entry", "Đã thêm") : "Log");
            logButton.Glyph = isInEntry ? Icons.Checkmark : Icons.Add;
            logButton.Surface = () => surface;
            logButton.Size = new Size(ButtonWidths.ActionWidth(logButton.Font), ButtonH);
            logButton.AccessibleName = isInEntry ? $"{ticket.Key} {Lang.T("is in New entry", "đã có trong ô Nhập mới")}" : $"Log {ticket.Key}";
            StepsTip.SetUntilDisposed(logButton, Lang.T($"Put {ticket.Key} into New entry", $"Đưa {ticket.Key} vào ô Nhập mới"));
            logButton.Click += (_, _) => onLog(ticket.Key, ticket.Summary);
            buttons.Add(logButton); // last in the right-to-left list: leftmost
        }

        var isDoneInJira = section == TicketSection.InProgress && ticket.IsStatusDone;
        HasStatusDot = busyText == null && (readyToTestLabel != null || isDoneInJira);
        var (statusText, statusColor) = busyText == null ? Cell(ticket, section, TaskTableColumn.Status) : (busyText, Theme.Accent);
        StatusText = statusText;
        var cells = new List<TableCell>
        {
            new(TaskTableColumn.Status, statusText, statusColor,
                busyText == null && isDoneInJira ? Lang.T("Done in Jira - move to Done", "Jira đã Done - chuyển sang Xong") : null),
        };
        foreach (var column in new[] { TaskTableColumn.Due, TaskTableColumn.Eta, TaskTableColumn.Priority, TaskTableColumn.Logged })
        {
            var (text, color) = Cell(ticket, section, column);
            cells.Add(new TableCell(column, text, color));
        }
        foreach (var column in new[] { TaskTableColumn.PrMain, TaskTableColumn.PrDev })
        {
            cells.Add(busyText != null ? new TableCell(column, EmptyCell, Theme.TextSecondary) : PrCell(prs ?? Array.Empty<TicketPr>(), column));
        }
        SetCells(cells, HasStatusDot ? TaskTableColumn.Status : null);

        foreach (var button in buttons) button.Enabled = busyText == null;
        SetActions(buttons);
        if (isInEntry && logButton != null)
        {
            logButton.Enabled = false;
            Controls.Add(new Panel { Name = InEntryBarName, Bounds = new Rectangle(0, RowPadY, UiScale.S(3), Height - 2 * RowPadY), BackColor = Theme.Accent });
        }
    }

    // The board's columns at a row width; the board's header uses it too, so titles and cells line up.
    internal static TaskTableLayout LayoutFor(int rowWidth) => LayoutFor(rowWidth, BoardColumns);

    internal static string? ReadyToTestLabel(JiraTicket ticket, TicketSection section, bool isDevPrMerged)
        => JiraStatusFlow.ReadyToTestLabel(ticket, section, isDevPrMerged);

    private static List<Button> BuildButtons(JiraTicket ticket, TicketSection section, bool isDismissed,
        TicketBoard board, Action<string, bool> onMoved, Action<string> onStart, Action<string> onOpen, Color surface)
    {
        var key = ticket.Key;
        // Listed right-to-left: the first entry sits at the right edge. IsMove = a board move
        // that is followed by onMoved; Start/Open only hand the key to the host.
        var actions = new List<(string Text, Action<string> Action, bool IsMove, Color? Tint, string? Glyph)>();
        var buttons = new List<Button>();
        if (isDismissed)
            actions.Add((Lang.T("Restore", "Khôi phục"), board.Restore, true, null, null));
        else
        {
            // An X at the right edge rather than a word, asked first; Restore under Dismissed brings the ticket back.
            var dismissText = Lang.T("Dismiss", "Ẩn");
            var dismiss = new CloseButton { Surface = () => surface, Size = new Size(ButtonH, ButtonH), AccessibleName = $"{dismissText} {key}" };
            StepsTip.SetUntilDisposed(dismiss, dismissText);
            OnMoveClick(dismiss, key, board.Dismiss, onMoved, () => ConfirmDialog.Ask(dismiss.FindForm(),
                Lang.T($"Dismiss {key}?", $"Ẩn {key}?"),
                Lang.T($"{key} {ticket.Summary} moves to Dismissed and leaves New and In progress. Restore there brings it back.",
                    $"{key} {ticket.Summary} sẽ chuyển sang Đã ẩn, không còn ở Mới và Đang làm. Bấm Khôi phục ở đó để đưa lại."),
                dismissText, destructive: true));
            buttons.Add(dismiss);
            if (section == TicketSection.New) actions.Add((Lang.T("Start", "Bắt đầu"), onStart, false, Theme.ActionStart, null));
            if (section == TicketSection.InProgress)
            {
                // Done in Jira already: the arrow says "move it to Done".
                actions.Add((Lang.T("Done", "Xong"), board.MarkDone, true, Theme.ActionDone, ticket.IsStatusDone ? Icons.ArrowRight : null));
                actions.Add((Lang.T("Open", "Mở"), onOpen, false, Theme.Accent, null));
            }
            // Back to In progress for more work (a failed QA, a reworked PR); Open there recreates a removed worktree.
            if (section == TicketSection.Done)
            {
                actions.Add((Lang.T("Reopen", "Mở lại"), board.Reopen, true, null, null));
            }
        }

        foreach (var (text, run, isMove, tint, glyph) in actions)
        {
            var button = tint == null ? MacButton.Secondary(text) : MacButton.Tinted(text, tint.Value);
            button.Glyph = glyph;
            button.Surface = () => surface;
            button.Size = new Size(ButtonWidths.ActionWidth(button.Font), ButtonH);
            button.AccessibleName = $"{text} {key}";
            if (isMove) OnMoveClick(button, key, run, onMoved);
            else button.Click += (_, _) => run(key);
            buttons.Add(button);
        }
        return buttons;
    }

    // A board move followed by onMoved, once confirm (when given) says yes.
    private static void OnMoveClick(Button button, string key, Action<string> run, Action<string, bool> onMoved, Func<bool>? confirm = null)
    {
        button.Click += (_, _) =>
        {
            // A mouse click always lands inside the button; a keyboard press usually does not. Read before a dialog.
            var fromKeyboard = !button.ClientRectangle.Contains(button.PointToClient(Cursor.Position));
            if (confirm != null && !confirm()) return;
            try
            {
                run(key);
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[tasks] Moving {key} failed: {ex.Message}");
            }
            // Reload even after a failure: the board may have saved part of the move.
            onMoved(key, fromKeyboard);
        };
    }

    // One table cell of a ticket: plain text, red/orange only when it needs action, "–" in gray when empty. The status is
    // purple, orange once Jira finished a ticket still In progress here.
    internal static (string Text, Color Color) Cell(JiraTicket ticket, TicketSection section, TaskTableColumn column)
    {
        switch (column)
        {
            case TaskTableColumn.Status:
            {
                if (string.IsNullOrEmpty(ticket.Status)) return (EmptyCell, Theme.TextSecondary);
                var isDoneInJira = section == TicketSection.InProgress && ticket.IsStatusDone;
                return (ticket.Status, isDoneInJira ? Orange : Theme.ActionReadyToTest);
            }
            case TaskTableColumn.Due:
            {
                if (string.IsNullOrEmpty(ticket.DueDate)) return (EmptyCell, Theme.TextSecondary);
                var dueColor = ticket.GetDueUrgency(Hcm.Today()) switch
                {
                    DueUrgency.Overdue => Red,
                    DueUrgency.Soon => Orange,
                    _ => Theme.TextPrimary,
                };
                return (ShortDate(ticket.DueDate), dueColor);
            }
            case TaskTableColumn.Eta:
            {
                return string.IsNullOrEmpty(ticket.Eta) ? (EmptyCell, Theme.TextSecondary) : (ShortDate(ticket.Eta), Theme.TextPrimary);
            }
            case TaskTableColumn.Priority:
            {
                if (!ticket.DevPriority.HasValue) return (EmptyCell, Theme.TextSecondary);
                var priorityColor = ticket.DevPriorityLevel switch
                {
                    DevPriorityLevel.Top => Red,
                    DevPriorityLevel.High => Orange,
                    _ => Theme.TextPrimary,
                };
                return (ticket.DevPriority.Value.ToString("0.#", CultureInfo.InvariantCulture), priorityColor);
            }
            case TaskTableColumn.Logged:
            {
                var hasTime = (ticket.TimeSpentSeconds ?? 0) != 0;
                var loggedColor = ticket.IsTimeLogMissing ? Red : hasTime ? Theme.TextPrimary : Theme.TextSecondary;
                return (hasTime ? JiraTicket.FormatTimeSpent(ticket.TimeSpentSeconds) : EmptyCell, loggedColor);
            }
            default:
            {
                // The PR columns (PrCell) are not cells of a ticket alone.
                throw new ArgumentOutOfRangeException(nameof(column), column, null);
            }
        }
    }

    // One branch's PRs (the latest per repo) in one word: Merged once every repo's merged, "1/2 merged" while some still
    // wait, Open before any merged. An abandoned PR counts only when no repo has another. The tip names each PR.
    internal static TableCell PrCell(IReadOnlyList<TicketPr> prs, TaskTableColumn column)
    {
        var branch = column == TaskTableColumn.PrMain ? "main" : "dev";
        var branchPrs = prs.Where(tp => tp.Branch == branch).ToList();
        if (branchPrs.Count == 0) return new TableCell(column, EmptyCell, Theme.TextSecondary);

        var toolTip = string.Join(Environment.NewLine, branchPrs.Select(tp => $"{tp.RepoName} #{tp.PullRequestId}: {PrStatusText(tp.Status)}"));
        var livePrs = branchPrs.Where(tp => tp.Status != "abandoned").ToList();
        var mergedCount = livePrs.Count(tp => tp.Status == "completed");
        if (livePrs.Count == 0) return new TableCell(column, PrStatusText("abandoned"), Theme.TextSecondary, toolTip);
        if (mergedCount == livePrs.Count) return new TableCell(column, PrStatusText("completed"), WeekCard.Green, toolTip);
        if (mergedCount == 0) return new TableCell(column, PrStatusText("active"), Theme.Accent, toolTip);
        return new TableCell(column, Lang.T($"{mergedCount}/{livePrs.Count} merged", $"{mergedCount}/{livePrs.Count} đã merge"),
            Theme.Accent, toolTip);
    }

    // An Azure DevOps PR status as a word; any other status (notSet) as is.
    internal static string PrStatusText(string status) => status switch
    {
        "completed" => Lang.T("Merged", "Đã merge"),
        "active" => Lang.T("Open", "Đang mở"),
        "abandoned" => Lang.T("Abandoned", "Đã hủy"),
        _ => status,
    };

    // A Jira date ("2026-10-14") in the UI language's short form; other text as is, "-" when empty.
    internal static string ShortDate(string? isoDate) =>
        DateOnly.TryParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? Lang.ShortDate(date.ToDateTime(TimeOnly.MinValue))
            : string.IsNullOrEmpty(isoDate) ? "-" : isoDate;

    // A fact as a pill (the Ticket page's "No PR yet").
    internal static FactTag Fact(string text, Color tone, int minTextWidth = 0) => new(text, tone, FactFont, minTextWidth);

    internal static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[ui] Could not open {url}: {ex.Message}");
        }
    }

    // A link from a web page: OpenUrl shell-executes, so only an http(s) URL may reach it, never a file or a script.
    internal static string? WebUrl(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsoluteUri
            : null;
}
