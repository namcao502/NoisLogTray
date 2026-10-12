using System.Diagnostics;
using System.Drawing;
using System.Globalization;

namespace NoisLogTray;

// One ticket on the Tasks board: key link + summary with the section's buttons at the right, then a line of
// Due / P / ETA / status / Logged facts and a PR line. A narrow row puts the buttons under them instead.
internal sealed class TaskRow : Panel
{
    private static readonly int ButtonH = UiScale.S(28);
    private static readonly int ButtonGap = UiScale.S(6);
    private static readonly int Pad = UiScale.S(8);
    private static readonly int LineGap = UiScale.S(4);

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
    // A non-null busyText disables every button, replaces the facts line and hides the PR line.
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
            var readyButton = MacButton.Tinted(ButtonWidths.ReadyToTestText, Theme.ActionReadyToTest);
            readyButton.Size = new Size(ButtonWidths.ReadyToTestWidth(readyButton.Font), ButtonH);
            readyButton.AccessibleName = $"{readyToTestLabel} {ticket.Key}";
            StepsTip.SetToolTip(readyButton, readyToTestLabel);
            readyButton.Click += (_, _) => onReadyToTest(ticket.Key);
            buttons.Add(readyButton);
        }
        var buttonsWidth = buttons.Sum(mb => mb.Width + ButtonGap);
        var buttonsOnOwnLine = ButtonsOnOwnLine(width, buttonsWidth);

        var lineWidth = width - 2 * Pad;
        // Line 2: the facts, under the key line and across the whole row.
        var facts = new FlowLayoutPanel
        {
            Location = new Point(Pad - UiScale.S(3), Pad + ButtonH + LineGap),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true, // a long facts line wraps instead of being cut
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
        }
        facts.Size = new Size(lineWidth, Math.Max(FactFont.Height + UiScale.S(4), facts.GetPreferredSize(new Size(lineWidth, 0)).Height));
        var contentBottom = facts.Bottom;
        if (busyText == null && prSummary.Length != 0)
        {
            // Same x as the facts panel, so the PR text lines up with the facts above it.
            var prLine = PrLine(prSummary, new Rectangle(Pad - UiScale.S(3), contentBottom + LineGap, lineWidth, FactFont.Height + UiScale.S(4)));
            Controls.Add(prLine);
            contentBottom = prLine.Bottom;
        }

        var buttonLocations = new List<Point>();
        int rowHeight;
        int summaryRight;
        if (buttonsOnOwnLine)
        {
            var lines = ButtonLines(width, buttons.Select(mb => mb.Width).ToList(), Pad, ButtonGap);
            var firstLineY = contentBottom + LineGap;
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
            summaryRight = width - Pad;
        }
        else
        {
            // Line 1 holds the buttons at its right; the summary stops short of them.
            var buttonX = width - Pad;
            foreach (var button in buttons)
            {
                buttonX -= button.Width;
                buttonLocations.Add(new Point(buttonX, Pad));
                buttonX -= ButtonGap;
            }
            rowHeight = contentBottom + Pad;
            summaryRight = buttonX + ButtonGap - UiScale.S(8);
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
            Font = KeyFont,
            LinkColor = Theme.Accent,
            ActiveLinkColor = Theme.AccentHover,
            LinkBehavior = LinkBehavior.HoverUnderline,
            BackColor = Color.Transparent,
            UseMnemonic = false,
        };
        keyLink.Location = new Point(Pad, Pad + (ButtonH - keyLink.PreferredHeight) / 2);
        keyLink.LinkClicked += (_, _) => OpenUrl($"{jiraBaseUrl}/browse/{ticket.Key}");
        Controls.Add(keyLink);
        _keyLink = keyLink;

        var summaryX = Pad + Math.Max(RowColumns.KeyWidth(KeyFont), keyLink.PreferredWidth) + UiScale.S(6);
        // AutoEllipsis also shows the full text as a tooltip when it is cut.
        Controls.Add(new Label
        {
            Text = ticket.Summary,
            Location = new Point(summaryX, keyLink.Top),
            Size = new Size(Math.Max(UiScale.S(20), summaryRight - summaryX), keyLink.PreferredHeight),
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
        var actions = new List<(string Text, Action<string> Action, bool IsMove, Color? Tint)>();
        if (isDismissed)
            actions.Add((Lang.T("Restore", "Khôi phục"), board.Restore, true, null));
        else
        {
            actions.Add((Lang.T("Dismiss", "Ẩn"), board.Dismiss, true, null));
            if (section == TicketSection.New) actions.Add((Lang.T("Start", "Bắt đầu"), onStart, false, Theme.ActionStart));
            if (section == TicketSection.InProgress)
            {
                var doneText = ticket.IsStatusDone ? Lang.T("→ Done", "→ Xong") : Lang.T("Done", "Xong");
                actions.Add((doneText, board.MarkDone, true, Theme.ActionDone));
                actions.Add((Lang.T("Open", "Mở"), onOpen, false, Theme.Accent));
            }
        }

        var buttons = new List<MacButton>();
        foreach (var (text, run, isMove, tint) in actions)
        {
            var button = tint == null ? MacButton.Secondary(text) : MacButton.Tinted(text, tint.Value);
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
        yield return ($"{Lang.T("Due", "Hạn")} {ShortDate(ticket.DueDate)}", dueColor);

        var priorityColor = ticket.DevPriorityLevel switch
        {
            DevPriorityLevel.Top => Red,
            DevPriorityLevel.High => Orange,
            DevPriorityLevel.Normal => Indigo,
            _ => Gray,
        };
        var priority = ticket.DevPriority.HasValue ? ticket.DevPriority.Value.ToString("0.#") : "-";
        yield return ($"P {priority}", priorityColor);

        yield return ($"ETA {ShortDate(ticket.Eta)}", Theme.TextSecondary);
        var status = string.IsNullOrEmpty(ticket.Status) ? "-" : ticket.Status;
        if (section == TicketSection.InProgress && ticket.IsStatusDone)
            status = Lang.T($"Jira: {status} -- move to Done?", $"Jira: {status} -- chuyển sang Done?");
        yield return (status, Theme.TextSecondary);

        var loggedColor = ticket.IsTimeLogMissing ? Red : Theme.TextSecondary;
        yield return ($"{Lang.T("Logged", "Đã log")} {JiraTicket.FormatTimeSpent(ticket.TimeSpentSeconds)}", loggedColor);
    }

    // A Jira date ("2026-10-14") in the UI language's short form; other text as is, "-" when empty.
    internal static string ShortDate(string? isoDate) =>
        DateOnly.TryParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? Lang.ShortDate(date.ToDateTime(TimeOnly.MinValue))
            : string.IsNullOrEmpty(isoDate) ? "-" : isoDate;

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

    // Line 3 of a ticket row: green once every PR shown is merged (or abandoned), nothing open on BE or FE.
    internal static Label PrLine(string prSummary, Rectangle bounds)
    {
        var allClosed = !prSummary.Contains('●') && !prSummary.Contains('?');
        var prLine = Fact($"PR  {prSummary}", allClosed ? WeekCard.Green : Theme.TextSecondary);
        prLine.AutoSize = false;
        prLine.AutoEllipsis = true; // the full text shows as a tooltip when cut
        prLine.Bounds = bounds;
        return prLine;
    }

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
