using System.Diagnostics;
using System.Drawing;
using System.Globalization;

namespace NoisLogTray;

// One ticket on the Tasks board: key link + summary, a line of Due / P / ETA / status /
// Logged facts, and the section's move buttons on the right.
internal sealed class TaskRow : Panel
{
    private static readonly int ButtonH = UiScale.S(28);
    private static readonly int ButtonGap = UiScale.S(6);
    private static readonly int Pad = UiScale.S(8);

    private static readonly Color Red = Color.FromArgb(230, 76, 76);
    private static readonly Color Orange = Color.FromArgb(255, 140, 50);
    private static readonly Color Indigo = Color.FromArgb(110, 110, 230);
    private static readonly Color Gray = Color.FromArgb(150, 150, 156);

    private static readonly Font KeyFont = UiScale.Font("Segoe UI", 9.5F, FontStyle.Bold);
    private static readonly Font SummaryFont = UiScale.Font("Segoe UI", 9.5F);
    private static readonly Font FactFont = UiScale.Font("Segoe UI", 8.5F);

    private readonly LinkLabel _keyLink;

    internal string TicketKey { get; }

    private static readonly ToolTip StepsTip = new();

    // isDismissed overrides section (Restore only); onMoved's flag says a keyboard press triggered the move.
    // A non-null busyText disables every button and replaces the facts line.
    internal TaskRow(JiraTicket ticket, TicketSection section, bool isDismissed, string jiraBaseUrl,
        TicketBoard board, Action<string, bool> onMoved, Action<string> onStart, Action<string> onOpen,
        bool isDevPrMerged, Action<string> onReadyToTest, string? busyText, int width, string prSummary = "")
    {
        TicketKey = ticket.Key;
        Margin = new Padding(0);
        BackColor = Color.Transparent;

        var buttons = BuildButtons(ticket, section, isDismissed, board, onMoved, onStart, onOpen);
        var readyToTestLabel = isDismissed ? null : ReadyToTestLabel(ticket, section, isDevPrMerged);
        if (readyToTestLabel != null)
        {
            // Last in the right-to-left list, so it sits leftmost.
            // Fixed label and width; the Jira steps it will walk are in the tooltip.
            var readyButton = MacButton.Primary(ButtonWidths.ReadyToTestText);
            readyButton.Size = new Size(ButtonWidths.ReadyToTestWidth(readyButton.Font), ButtonH);
            readyButton.AccessibleName = $"{readyToTestLabel} {ticket.Key}";
            StepsTip.SetToolTip(readyButton, readyToTestLabel);
            readyButton.Click += (_, _) => onReadyToTest(ticket.Key);
            buttons.Add(readyButton);
        }
        var buttonsWidth = buttons.Sum(mb => mb.Width + ButtonGap);
        var buttonsOnOwnLine = ButtonsOnOwnLine(width, buttonsWidth);

        var facts = new FlowLayoutPanel
        {
            Location = new Point(Pad - UiScale.S(3), Pad + KeyFont.Height + UiScale.S(6)),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true, // a long facts line wraps instead of being cut at the buttons
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        if (busyText != null)
            facts.Controls.Add(Fact(busyText, Theme.TextSecondary));
        else
        {
            foreach (var (text, color) in Facts(ticket, section))
            {
                var fact = Fact(text, color);
                // The due date comes first; a fixed width lines up the facts after it across rows.
                if (facts.Controls.Count == 0) fact.MinimumSize = new Size(RowColumns.DueWidth(FactFont), 0);
                facts.Controls.Add(fact);
            }
            // Green once every PR shown is merged (or abandoned): nothing left open on BE or FE.
            if (prSummary.Length != 0)
            {
                var allClosed = !prSummary.Contains('●') && !prSummary.Contains('?');
                facts.Controls.Add(Fact($"PR {prSummary}", allClosed ? WeekPage.Green : Theme.TextSecondary));
            }
        }

        var factsY = facts.Location.Y;
        int rowHeight;
        int textWidth;
        var buttonLocations = new List<Point>();
        if (buttonsOnOwnLine)
        {
            textWidth = width - 2 * Pad;
            var factsHeight = facts.GetPreferredSize(new Size(width - 2 * Pad, 0)).Height;
            var lines = ButtonLines(width, buttons.Select(mb => mb.Width).ToList(), Pad, ButtonGap);
            var firstLineY = factsY + factsHeight + UiScale.S(4);
            var buttonX = width - Pad;
            var previousLine = 0;
            for (var buttonIndex = 0; buttonIndex < buttons.Count; buttonIndex++)
            {
                if (lines[buttonIndex] != previousLine)
                {
                    previousLine = lines[buttonIndex];
                    buttonX = width - Pad;
                }
                // An oversized single button is clamped to the left padding and cut on the right.
                buttonX = Math.Max(buttonX - buttons[buttonIndex].Width, Pad);
                buttonLocations.Add(new Point(buttonX, firstLineY + lines[buttonIndex] * (ButtonH + ButtonGap)));
                buttonX -= ButtonGap;
            }
            rowHeight = buttonLocations[^1].Y + ButtonH + Pad;
            facts.Size = new Size(width - 2 * Pad, factsHeight);
        }
        else
        {
            textWidth = width - Pad - buttonsWidth - Pad;
            var factsHeight = Math.Max(FactFont.Height + UiScale.S(4), facts.GetPreferredSize(new Size(textWidth, 0)).Height);
            rowHeight = Math.Max(factsY + factsHeight + Pad, ButtonH + 2 * Pad);
            var buttonX = width - Pad;
            foreach (var button in buttons)
            {
                buttonX -= button.Width;
                buttonLocations.Add(new Point(buttonX, (rowHeight - ButtonH) / 2));
                buttonX -= ButtonGap;
            }
            facts.Size = new Size(textWidth, factsHeight);
        }
        Size = new Size(width, rowHeight);

        for (var buttonIndex = 0; buttonIndex < buttons.Count; buttonIndex++)
        {
            buttons[buttonIndex].Location = buttonLocations[buttonIndex];
            buttons[buttonIndex].Enabled = busyText == null;
            Controls.Add(buttons[buttonIndex]);
        }

        var keyLink = new LinkLabel
        {
            Text = ticket.Key,
            AutoSize = true,
            Location = new Point(Pad, Pad),
            Font = KeyFont,
            LinkColor = Theme.Accent,
            ActiveLinkColor = Theme.AccentHover,
            LinkBehavior = LinkBehavior.HoverUnderline,
            BackColor = Color.Transparent,
            UseMnemonic = false,
        };
        keyLink.LinkClicked += (_, _) => OpenUrl($"{jiraBaseUrl}/browse/{ticket.Key}");
        Controls.Add(keyLink);
        _keyLink = keyLink;

        var summaryX = Pad + Math.Max(RowColumns.KeyWidth(KeyFont), keyLink.PreferredWidth) + UiScale.S(6);
        // AutoEllipsis also shows the full text as a tooltip when it is cut.
        Controls.Add(new Label
        {
            Text = ticket.Summary,
            Location = new Point(summaryX, Pad),
            Size = new Size(Math.Max(UiScale.S(20), Pad + textWidth - summaryX), keyLink.PreferredHeight),
            Font = SummaryFont,
            ForeColor = Theme.TextPrimary,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
            UseMnemonic = false,
        });
        Controls.Add(facts);

        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Divider });
    }

    internal static bool ButtonsOnOwnLine(int rowWidth, int buttonsWidth) => buttonsWidth > rowWidth / 2;

    // Line index per button, in the given right-to-left order. An empty line always takes the next
    // button, so an oversized one gets its own line instead of looping.
    internal static IReadOnlyList<int> ButtonLines(int rowWidth, IReadOnlyList<int> buttonWidths, int pad, int gap)
    {
        var available = rowWidth - 2 * pad;
        var lines = new List<int>();
        var line = 0;
        var used = 0;
        foreach (var buttonWidth in buttonWidths)
        {
            if (used != 0 && used + gap + buttonWidth > available)
            {
                line++;
                used = 0;
            }
            used = used == 0 ? buttonWidth : used + gap + buttonWidth;
            lines.Add(line);
        }
        return lines;
    }

    internal void FocusKey() => _keyLink.Focus();

    internal static string? ReadyToTestLabel(JiraTicket ticket, TicketSection section, bool isDevPrMerged)
        => JiraStatusFlow.ReadyToTestLabel(ticket, section, isDevPrMerged);

    private static List<MacButton> BuildButtons(JiraTicket ticket, TicketSection section, bool isDismissed,
        TicketBoard board, Action<string, bool> onMoved, Action<string> onStart, Action<string> onOpen)
    {
        var key = ticket.Key;
        // Listed right-to-left: the first entry sits at the right edge. IsMove = a board move
        // that is followed by onMoved; Start/Open only hand the key to the host.
        var actions = new List<(string Text, Action<string> Action, bool IsMove)>();
        if (isDismissed)
            actions.Add((Lang.T("Restore", "Khôi phục"), board.Restore, true));
        else
        {
            actions.Add((Lang.T("Dismiss", "Ẩn"), board.Dismiss, true));
            if (section == TicketSection.New) actions.Add((Lang.T("Start", "Bắt đầu"), onStart, false));
            if (section == TicketSection.InProgress)
            {
                var doneText = ticket.IsStatusDone ? Lang.T("→ Done", "→ Xong") : Lang.T("Done", "Xong");
                actions.Add((doneText, board.MarkDone, true));
                actions.Add((Lang.T("Open", "Mở"), onOpen, false));
            }
        }

        var buttons = new List<MacButton>();
        foreach (var (text, run, isMove) in actions)
        {
            var button = MacButton.Secondary(text);
            button.Size = new Size(ButtonWidths.ActionWidth(button.Font), ButtonH);
            button.AccessibleName = $"{text} {key}";
            if (!isMove)
            {
                button.Click += (_, _) => run(key);
                buttons.Add(button);
                continue;
            }
            button.Click += (_, _) =>
            {
                // A mouse click always lands inside the button; a keyboard press usually does not.
                var fromKeyboard = !button.ClientRectangle.Contains(button.PointToClient(Cursor.Position));
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
            buttons.Add(button);
        }
        return buttons;
    }

    internal static IEnumerable<(string Text, Color Color)> Facts(JiraTicket ticket, TicketSection section)
    {
        var dueColor = ticket.GetDueUrgency(Hcm.Today()) switch
        {
            DueUrgency.Overdue => Red,
            DueUrgency.Soon => Orange,
            _ => Theme.TextSecondary,
        };
        var due = DateOnly.TryParseExact(ticket.DueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dueDate)
            ? Lang.ShortDate(dueDate.ToDateTime(TimeOnly.MinValue))
            : ticket.DueDate ?? "-";
        yield return ($"{Lang.T("Due", "Hạn")}: {due}", dueColor);

        var priorityColor = ticket.DevPriorityLevel switch
        {
            DevPriorityLevel.Top => Red,
            DevPriorityLevel.High => Orange,
            DevPriorityLevel.Normal => Indigo,
            _ => Gray,
        };
        var priority = ticket.DevPriority.HasValue ? ticket.DevPriority.Value.ToString("0.#") : "-";
        yield return ($"P: {priority}", priorityColor);

        yield return ($"ETA: {(ticket.HasEta ? ticket.Eta : "-")}", Theme.TextSecondary);
        var status = string.IsNullOrEmpty(ticket.Status) ? "-" : ticket.Status;
        if (section == TicketSection.InProgress && ticket.IsStatusDone)
            status = Lang.T($"Jira: {status} -- move to Done?", $"Jira: {status} -- chuyển sang Done?");
        yield return (status, Theme.TextSecondary);

        var loggedColor = ticket.IsTimeLogMissing ? Red : Theme.TextSecondary;
        yield return ($"{Lang.T("Logged", "Đã log")}: {JiraTicket.FormatTimeSpent(ticket.TimeSpentSeconds)}", loggedColor);
    }

    internal static Label Fact(string text, Color color) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(0, 0, UiScale.S(14), 0),
        Font = FactFont,
        ForeColor = color,
        BackColor = Color.Transparent,
        UseMnemonic = false,
    };

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
}
