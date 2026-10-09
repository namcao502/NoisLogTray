using System.Diagnostics;
using System.Drawing;

namespace NoisLogTray;

// One ticket line on the Dashboard: key link + summary, a line of facts beneath, and up to
// two buttons on the right. Same look as TaskRow; the page decides texts and actions.
internal sealed class DashboardRow : Panel
{
    private static readonly int ButtonH = UiScale.S(28);
    private static readonly int ButtonGap = UiScale.S(6);
    private static readonly int Pad = UiScale.S(8);
    private const int MaxButtons = 2;

    private static readonly Font KeyFont = UiScale.Font("Segoe UI", 9.5F, FontStyle.Bold);
    private static readonly Font SummaryFont = UiScale.Font("Segoe UI", 9.5F);
    private static readonly Font FactFont = UiScale.Font("Segoe UI", 8.5F);

    // buttons are listed right-to-left: the first one sits at the right edge.
    internal DashboardRow(string ticketKey, string linkUrl, string summary,
        IReadOnlyList<(string Text, Color Color)> facts,
        IReadOnlyList<(string Text, bool Primary, bool Enabled, Action OnClick)> buttons,
        int width)
    {
        if (buttons.Count > MaxButtons)
            throw new ArgumentException($"A dashboard row holds at most {MaxButtons} buttons.", nameof(buttons));

        var factsY = Pad + KeyFont.Height + UiScale.S(6);
        Margin = new Padding(0);
        BackColor = Color.Transparent;

        // Buttons first: their widths decide the text width, which decides how the facts wrap and the row height.
        var builtButtons = new List<(MacButton Button, int Width)>();
        foreach (var (text, primary, enabled, onClick) in buttons)
        {
            var button = primary ? MacButton.Primary(text) : MacButton.Secondary(text);
            // Fixed widths: a label change (Queue -> Queued) must not shift the row.
            var buttonW = primary ? ButtonWidths.ReadyToTestWidth(button.Font) : ButtonWidths.ActionWidth(button.Font);
            button.Enabled = enabled;
            button.AccessibleName = $"{text} {ticketKey}";
            button.Click += (_, _) => onClick();
            builtButtons.Add((button, buttonW));
        }
        var textWidth = width - 2 * Pad - builtButtons.Sum(bb => bb.Width + ButtonGap);

        var factsPanel = new FlowLayoutPanel
        {
            Location = new Point(Pad - UiScale.S(3), factsY),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true, // a long facts line wraps instead of being cut at the buttons
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
        var factsWidth = Math.Max(UiScale.S(20), textWidth);
        var factsHeight = Math.Max(FactFont.Height + UiScale.S(4), factsPanel.GetPreferredSize(new Size(factsWidth, 0)).Height);
        factsPanel.Size = new Size(factsWidth, factsHeight);
        var rowHeight = Math.Max(factsY + factsHeight + Pad, ButtonH + 2 * Pad);
        Size = new Size(width, rowHeight);

        var buttonX = width - Pad;
        foreach (var (button, buttonW) in builtButtons)
        {
            buttonX -= buttonW;
            button.Bounds = new Rectangle(buttonX, (rowHeight - ButtonH) / 2, buttonW, ButtonH);
            buttonX -= ButtonGap;
            Controls.Add(button);
        }

        var keyLink = new LinkLabel
        {
            Text = ticketKey,
            AutoSize = true,
            Location = new Point(Pad, Pad),
            Font = KeyFont,
            LinkColor = Theme.Accent,
            ActiveLinkColor = Theme.AccentHover,
            LinkBehavior = LinkBehavior.HoverUnderline,
            BackColor = Color.Transparent,
            UseMnemonic = false,
        };
        keyLink.LinkClicked += (_, _) => OpenUrl(linkUrl);
        Controls.Add(keyLink);

        var summaryX = Pad + Math.Max(RowColumns.KeyWidth(KeyFont), keyLink.PreferredWidth) + UiScale.S(6);
        // AutoEllipsis also shows the full text as a tooltip when it is cut.
        Controls.Add(new Label
        {
            Text = summary,
            Location = new Point(summaryX, Pad),
            Size = new Size(Math.Max(UiScale.S(20), Pad + textWidth - summaryX), keyLink.PreferredHeight),
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
