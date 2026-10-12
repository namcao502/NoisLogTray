using System.Diagnostics;
using System.Drawing;

namespace NoisLogTray;

// One ticket on the Dashboard: key link + summary with up to two buttons at the right, a line of
// facts beneath and an optional PR line. Same look as TaskRow; the page decides texts and actions.
internal sealed class DashboardRow : Panel
{
    private static readonly int ButtonH = UiScale.S(28);
    private static readonly int ButtonGap = UiScale.S(6);
    private static readonly int Pad = UiScale.S(8);
    private static readonly int LineGap = UiScale.S(4);
    private const int MaxButtons = 2;

    private static readonly Font KeyFont = UiScale.Font("Segoe UI", 9.5F, FontStyle.Bold);
    private static readonly Font SummaryFont = UiScale.Font("Segoe UI", 9.5F);
    private static readonly Font FactFont = UiScale.Font("Segoe UI", 8.5F);

    // buttons are listed right-to-left: the first one sits at the right edge.
    internal DashboardRow(string ticketKey, string linkUrl, string summary,
        IReadOnlyList<(string Text, Color Color)> facts, string prSummary,
        IReadOnlyList<(string Text, Color? Tint, bool Enabled, Action OnClick)> buttons,
        int width)
    {
        if (buttons.Count > MaxButtons)
            throw new ArgumentException($"A dashboard row holds at most {MaxButtons} buttons.", nameof(buttons));

        Margin = new Padding(0);
        BackColor = Color.Transparent;

        var builtButtons = new List<(MacButton Button, int Width)>();
        foreach (var (text, tint, enabled, onClick) in buttons)
        {
            var button = tint == null ? MacButton.Secondary(text) : MacButton.Tinted(text, tint.Value);
            // Fixed widths: a label change (Queue -> Queued) must not shift the row.
            // Ready to Test is the one label wider than the shared action width.
            var buttonW = text == ButtonWidths.ReadyToTestText ? ButtonWidths.ReadyToTestWidth(button.Font) : ButtonWidths.ActionWidth(button.Font);
            button.Enabled = enabled;
            button.AccessibleName = $"{text} {ticketKey}";
            button.Click += (_, _) => onClick();
            builtButtons.Add((button, buttonW));
        }
        var lineWidth = width - 2 * Pad;
        // Line 2: the facts, under the key line and across the whole row.
        var factsPanel = new FlowLayoutPanel
        {
            Location = new Point(Pad - UiScale.S(3), Pad + ButtonH + LineGap),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true, // a long facts line wraps instead of being cut
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        foreach (var (text, color) in facts)
        {
            var fact = Fact(text, color);
            // The first fact is the due date on most rows; a fixed width lines up what follows across rows.
            if (factsPanel.Controls.Count == 0) fact.MinimumSize = new Size(RowColumns.DueWidth(FactFont), 0);
            factsPanel.Controls.Add(fact);
        }
        factsPanel.Size = new Size(lineWidth, Math.Max(FactFont.Height + UiScale.S(4), factsPanel.GetPreferredSize(new Size(lineWidth, 0)).Height));
        var contentBottom = factsPanel.Bottom;
        if (prSummary.Length != 0)
        {
            var prLine = TaskRow.PrLine(prSummary, new Rectangle(Pad - UiScale.S(3), contentBottom + LineGap, lineWidth, FactFont.Height + UiScale.S(4)));
            Controls.Add(prLine);
            contentBottom = prLine.Bottom;
        }
        var rowHeight = contentBottom + Pad;
        Size = new Size(width, rowHeight);

        // Line 1 holds the buttons at its right; the summary stops short of the leftmost (or at the edge with none).
        var buttonX = width - Pad;
        foreach (var (button, buttonW) in builtButtons)
        {
            buttonX -= buttonW;
            button.Bounds = new Rectangle(buttonX, Pad, buttonW, ButtonH);
            buttonX -= ButtonGap;
            Controls.Add(button);
        }
        var summaryRight = builtButtons.Count == 0 ? width - Pad : buttonX + ButtonGap - UiScale.S(8);

        var keyLink = new LinkLabel
        {
            Text = ticketKey,
            AutoSize = true,
            Font = KeyFont,
            LinkColor = Theme.Accent,
            ActiveLinkColor = Theme.AccentHover,
            LinkBehavior = LinkBehavior.HoverUnderline,
            BackColor = Color.Transparent,
            UseMnemonic = false,
        };
        keyLink.Location = new Point(Pad, Pad + (ButtonH - keyLink.PreferredHeight) / 2);
        keyLink.LinkClicked += (_, _) => OpenUrl(linkUrl);
        Controls.Add(keyLink);

        var summaryX = Pad + Math.Max(RowColumns.KeyWidth(KeyFont), keyLink.PreferredWidth) + UiScale.S(6);
        // AutoEllipsis also shows the full text as a tooltip when it is cut.
        Controls.Add(new Label
        {
            Text = summary,
            Location = new Point(summaryX, keyLink.Top),
            Size = new Size(Math.Max(UiScale.S(20), summaryRight - summaryX), keyLink.PreferredHeight),
            Font = SummaryFont,
            ForeColor = Theme.TextPrimary,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
            UseMnemonic = false,
        });

        Controls.Add(factsPanel);

        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Divider });
    }

    private static Label Fact(string text, Color color) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(0, 0, UiScale.S(14), 0),
        Font = FactFont,
        ForeColor = color,
        BackColor = Color.Transparent,
        UseMnemonic = false,
    };

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[dashboard] Could not open {url}: {ex.Message}");
        }
    }
}
