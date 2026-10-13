using System.Diagnostics;
using System.Drawing;
using System.Globalization;

namespace NoisLogTray;

// One ticket on the Tasks board: key link + summary with the section's buttons at the right (Dismiss is an X, "+ Log"
// leftmost), then Due / P / ETA / status / Logged as colored tags and a line of PR tags. A narrow row puts the buttons
// under them. A ticket already in New entry gets an accent bar and an "In entry" button in place of "+ Log".
internal sealed class TaskRow : Panel
{
    private static readonly int ButtonH = UiScale.S(28);
    private static readonly int ButtonGap = UiScale.S(6);
    private static readonly int Pad = UiScale.S(8);
    private static readonly int LineGap = UiScale.S(4);

    private static readonly Color Red = Color.FromArgb(230, 76, 76);
    private static readonly Color Orange = Color.FromArgb(255, 140, 50);
    private static readonly Color Indigo = Color.FromArgb(110, 110, 230);
    internal static readonly Color Gray = Color.FromArgb(150, 150, 156);

    internal const string FactsName = "facts";
    internal const string PrTagsName = "prs";
    internal const string InEntryBarName = "inEntry";

    private static readonly Font KeyFont = UiScale.Font("Segoe UI", 9.5F, FontStyle.Bold);
    private static readonly Font SummaryFont = UiScale.Font("Segoe UI", 9.5F);
    private static readonly Font FactFont = UiScale.Font("Segoe UI", 8.5F);

    private readonly LinkLabel _keyLink;

    internal string TicketKey { get; }

    private static readonly ThemedToolTip StepsTip = new();

    // isDismissed overrides section (Restore only); onMoved's flag says a keyboard press triggered the move.
    // A non-null busyText disables every button, replaces the facts line and hides the PR line.
    // onLog (key, summary) adds the "+ Log" button, which isInEntry turns into a disabled "In entry".
    internal TaskRow(JiraTicket ticket, TicketSection section, bool isDismissed, string jiraBaseUrl,
        TicketBoard board, Action<string, bool> onMoved, Action<string> onStart, Action<string> onOpen,
        bool isDevPrMerged, Action<string> onReadyToTest, string? busyText, int width, string prSummary = "", bool striped = false,
        Action<string, string>? onLog = null, bool isInEntry = false)
    {
        TicketKey = ticket.Key;
        Margin = new Padding(0);
        // Every other row is a shade off the card, and its buttons' corners blend into that.
        BackColor = striped ? Theme.RowStripe(Theme.CardSurface) : Color.Transparent;
        var surface = striped ? BackColor : Theme.CardSurface;

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
        var buttonsWidth = buttons.Sum(b => b.Width + ButtonGap);
        var buttonsOnOwnLine = ButtonsOnOwnLine(width, buttonsWidth);
        // Beside the buttons every line stops short of them, so they can sit centered on the row's height.
        var contentRight = buttonsOnOwnLine ? width - Pad : width - Pad - buttonsWidth + ButtonGap - UiScale.S(8);

        var lineWidth = contentRight - Pad;
        // Line 2: the facts, under the key line.
        var facts = new FlowLayoutPanel
        {
            Name = FactsName,
            Location = new Point(Pad, Pad + ButtonH + LineGap),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true, // a long facts line wraps instead of being cut
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        if (busyText != null)
            facts.Controls.Add(Fact(busyText, Theme.Accent));
        else
        {
            // Due, P, ETA and Logged at fixed widths line up in columns across rows; the status after them is as wide as it is.
            var columnWidths = RowColumns.FactWidths(FactFont);
            foreach (var (text, color) in Facts(ticket, section))
            {
                var column = facts.Controls.Count;
                facts.Controls.Add(Fact(text, color, column < columnWidths.Count ? columnWidths[column] : 0));
            }
        }
        facts.Size = new Size(lineWidth, facts.GetPreferredSize(new Size(lineWidth, 0)).Height);
        var contentBottom = facts.Bottom;
        if (busyText == null && prSummary.Length != 0)
        {
            var prTags = PrTags(prSummary, new Point(Pad, contentBottom), lineWidth);
            Controls.Add(prTags);
            contentBottom = prTags.Bottom;
        }

        var buttonLocations = new List<Point>();
        int rowHeight;
        if (buttonsOnOwnLine)
        {
            var lines = ButtonLines(width, buttons.Select(b => b.Width).ToList(), Pad, ButtonGap);
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
        }
        else
        {
            rowHeight = contentBottom + Pad;
            var buttonX = width - Pad;
            foreach (var button in buttons)
            {
                buttonX -= button.Width;
                buttonLocations.Add(new Point(buttonX, (rowHeight - ButtonH) / 2));
                buttonX -= ButtonGap;
            }
        }
        Size = new Size(width, rowHeight);

        for (var buttonIndex = 0; buttonIndex < buttons.Count; buttonIndex++)
        {
            buttons[buttonIndex].Location = buttonLocations[buttonIndex];
            buttons[buttonIndex].Enabled = busyText == null;
            Controls.Add(buttons[buttonIndex]);
        }
        if (isInEntry && logButton != null)
        {
            logButton.Enabled = false;
            Controls.Add(new Panel { Name = InEntryBarName, Bounds = new Rectangle(0, Pad, UiScale.S(3), rowHeight - 2 * Pad), BackColor = Theme.Accent });
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
        var summaryLabel = new Label
        {
            Text = ticket.Summary,
            Location = new Point(summaryX, keyLink.Top),
            Size = new Size(Math.Max(UiScale.S(20), contentRight - summaryX), keyLink.PreferredHeight),
            Font = SummaryFont,
            ForeColor = Theme.TextPrimary,
            BackColor = Color.Transparent,
            UseMnemonic = false,
        };
        ThemedToolTip.Track(summaryLabel); // cut with an ellipsis, the full text in a tooltip
        Controls.Add(summaryLabel);
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

    // Each fact has its own tone, red/orange when it needs action, and gray when it is empty ("-"). The status, whose
    // length has no bound, comes last, after the facts RowColumns gives fixed widths.
    internal static IEnumerable<(string Text, Color Color)> Facts(JiraTicket ticket, TicketSection section)
    {
        yield return DueFact(ticket);

        var priorityColor = ticket.DevPriorityLevel switch
        {
            DevPriorityLevel.Top => Red,
            DevPriorityLevel.High => Orange,
            DevPriorityLevel.Normal => Indigo,
            _ => Gray,
        };
        var priority = ticket.DevPriority.HasValue ? ticket.DevPriority.Value.ToString("0.#") : "-";
        yield return ($"P {priority}", priorityColor);

        yield return ($"ETA {ShortDate(ticket.Eta)}", string.IsNullOrEmpty(ticket.Eta) ? Gray : Theme.ActionDone);

        var loggedColor = ticket.IsTimeLogMissing ? Red : (ticket.TimeSpentSeconds ?? 0) == 0 ? Gray : WeekCard.Green;
        yield return ($"{Lang.T("Logged", "Đã log")} {JiraTicket.FormatTimeSpent(ticket.TimeSpentSeconds)}", loggedColor);

        var status = string.IsNullOrEmpty(ticket.Status) ? "-" : ticket.Status;
        var statusColor = string.IsNullOrEmpty(ticket.Status) ? Gray : Theme.ActionReadyToTest;
        if (section == TicketSection.InProgress && ticket.IsStatusDone)
        {
            status = Lang.T($"Jira: {status} -- move to Done?", $"Jira: {status} -- chuyển sang Done?");
            statusColor = Orange;
        }
        yield return (status, statusColor);
    }

    internal static (string Text, Color Color) DueFact(JiraTicket ticket)
    {
        var dueColor = ticket.GetDueUrgency(Hcm.Today()) switch
        {
            DueUrgency.Overdue => Red,
            DueUrgency.Soon => Orange,
            _ => string.IsNullOrEmpty(ticket.DueDate) ? Gray : Theme.Accent,
        };
        return ($"{Lang.T("Due", "Hạn")} {ShortDate(ticket.DueDate)}", dueColor);
    }

    // A Jira date ("2026-10-14") in the UI language's short form; other text as is, "-" when empty.
    internal static string ShortDate(string? isoDate) =>
        DateOnly.TryParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? Lang.ShortDate(date.ToDateTime(TimeOnly.MinValue))
            : string.IsNullOrEmpty(isoDate) ? "-" : isoDate;

    internal static FactTag Fact(string text, Color tone, int minTextWidth = 0) => new(text, tone, FactFont, minTextWidth);

    // Line 3 of a ticket row: "PR" then one tag per repo of the summary ("BE main ✓ · dev ● | FE Portal dev ✓").
    internal static FlowLayoutPanel PrTags(string prSummary, Point location, int width)
    {
        var prTags = new FlowLayoutPanel
        {
            Name = PrTagsName,
            Location = location,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        prTags.Controls.Add(new Label
        {
            Text = "PR",
            AutoSize = true,
            Margin = new Padding(0, UiScale.S(2), UiScale.S(6), 0),
            Font = FactFont,
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
        });
        foreach (var repoSummary in prSummary.Split(" | "))
        {
            var tag = Fact(repoSummary, PrTone(repoSummary));
            tag.Width = Math.Min(tag.Width, width); // an oversized one is cut with an ellipsis rather than overflowing
            prTags.Controls.Add(tag);
        }
        prTags.Size = new Size(width, prTags.GetPreferredSize(new Size(width, 0)).Height);
        return prTags;
    }

    // Green once the repo's PRs are all merged (or abandoned), blue while one is open, gray while a status is unknown.
    internal static Color PrTone(string repoSummary) =>
        repoSummary.Contains('?') ? Gray : repoSummary.Contains('●') ? Theme.Accent : WeekCard.Green;

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
