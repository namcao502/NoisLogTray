using System.Diagnostics;
using System.Drawing;

namespace NoisLogTray;

// One ticket on the Dashboard: key link + summary with up to two buttons at the right, a line of
// fact tags beneath and an optional line of PR tags. Same look as TaskRow; the page decides texts and actions.
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
        IReadOnlyList<(string Text, Color? Tint, bool Enabled, Action OnClick, string? Glyph)> buttons,
        int width, bool striped = false)
    {
        if (buttons.Count > MaxButtons)
            throw new ArgumentException($"A dashboard row holds at most {MaxButtons} buttons.", nameof(buttons));

        Margin = new Padding(0);
        // Every other row is a shade off the card, and its buttons' corners blend into that.
        BackColor = striped ? Theme.RowStripe(Theme.CardSurface) : Color.Transparent;
        var surface = striped ? BackColor : Theme.CardSurface;

        var builtButtons = new List<(MacButton Button, int Width)>();
        foreach (var (text, tint, enabled, onClick, glyph) in buttons)
        {
            var button = tint == null ? MacButton.Secondary(text) : MacButton.Tinted(text, tint.Value);
            button.Glyph = glyph;
            button.Surface = () => surface;
            // Fixed widths: a label change (Queue -> Queued) must not shift the row.
            // Ready to Test is the one label wider than the shared action width.
            var buttonW = text == ButtonWidths.ReadyToTestText ? ButtonWidths.ReadyToTestWidth(button.Font) : ButtonWidths.ActionWidth(button.Font);
            button.Enabled = enabled;
            button.AccessibleName = $"{text} {ticketKey}";
            button.Click += (_, _) => onClick();
            builtButtons.Add((button, buttonW));
        }
        // Every line stops short of the buttons, so they can sit centered on the row's height.
        var buttonsWidth = builtButtons.Sum(bb => bb.Width + ButtonGap);
        var contentRight = builtButtons.Count == 0 ? width - Pad : width - Pad - buttonsWidth + ButtonGap - UiScale.S(8);
        var lineWidth = contentRight - Pad;
        // Line 2: the facts, under the key line.
        var factsPanel = new FlowLayoutPanel
        {
            Name = TaskRow.FactsName,
            Location = new Point(Pad, Pad + ButtonH + LineGap),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true, // a long facts line wraps instead of being cut
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        foreach (var (text, color) in facts)
        {
            // The first fact is the due date on most rows; a fixed width lines up what follows across rows.
            factsPanel.Controls.Add(TaskRow.Fact(text, color, factsPanel.Controls.Count == 0 ? RowColumns.DueWidth(FactFont) : 0));
        }
        factsPanel.Size = new Size(lineWidth, factsPanel.GetPreferredSize(new Size(lineWidth, 0)).Height);
        var contentBottom = factsPanel.Bottom;
        if (prSummary.Length != 0)
        {
            var prTags = TaskRow.PrTags(prSummary, new Point(Pad, contentBottom), lineWidth);
            Controls.Add(prTags);
            contentBottom = prTags.Bottom;
        }
        var rowHeight = contentBottom + Pad;
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
        var summaryLabel = new Label
        {
            Text = summary,
            Location = new Point(summaryX, keyLink.Top),
            Size = new Size(Math.Max(UiScale.S(20), contentRight - summaryX), keyLink.PreferredHeight),
            Font = SummaryFont,
            ForeColor = Theme.TextPrimary,
            BackColor = Color.Transparent,
            UseMnemonic = false,
        };
        ThemedToolTip.Track(summaryLabel); // cut with an ellipsis, the full text in a tooltip
        Controls.Add(summaryLabel);

        Controls.Add(factsPanel);

        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Divider });
    }

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
