using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// One suggestion on the Dashboard's to-do list: a tinted icon, a title line (the ticket key as a Jira link when there is
// one) over a gray detail line, and up to two buttons at the right edge, always shown. The page decides texts and actions.
internal sealed class TodoRow : Panel
{
    private const int MaxButtons = 2;

    private static readonly int RowH = UiScale.S(52);
    private static readonly int ButtonH = UiScale.S(28);
    private static readonly int ButtonGap = UiScale.S(6);
    private static readonly int Pad = UiScale.S(8);
    private static readonly int IconBox = UiScale.S(30);
    private static readonly int TextX = UiScale.S(50); // Pad + IconBox + 12
    private static readonly int TitleY = UiScale.S(7);
    private static readonly int DetailY = UiScale.S(27);

    private static readonly Font KeyFont = UiScale.Font("Segoe UI", 9.5F, FontStyle.Bold);
    private static readonly Font TitleFont = UiScale.Font("Segoe UI Semibold", 9.5F);
    private static readonly Font DetailFont = UiScale.Font("Segoe UI", 9F);

    private readonly string _icon;
    private readonly Color _tone;

    // buttons are listed right-to-left: the first one sits at the right edge.
    internal TodoRow(string? ticketKey, string? linkUrl, string icon, Color tone, string title, string detail,
        IReadOnlyList<(string Text, Color? Tint, bool Enabled, Action OnClick, string? Glyph)> buttons, int width, bool striped = false)
    {
        if (buttons.Count > MaxButtons)
            throw new ArgumentException($"A to-do row holds at most {MaxButtons} buttons.", nameof(buttons));

        _icon = icon;
        _tone = tone;
        Margin = new Padding(0);
        DoubleBuffered = true;
        // Every other row is a shade off the card.
        BackColor = striped ? Theme.RowStripe(Theme.CardSurface) : Color.Transparent;
        var surface = striped ? BackColor : Theme.CardSurface;
        Size = new Size(width, RowH);
        AccessibleRole = AccessibleRole.ListItem;
        AccessibleName = detail.Length == 0 ? $"{ticketKey} {title}".Trim() : $"{ticketKey} {title}, {detail}".Trim();

        // Right to left from the edge; added leftmost first below, so Tab walks them left to right.
        var builtButtons = new List<MacButton>();
        var buttonRight = width - Pad;
        foreach (var (text, tint, enabled, onClick, glyph) in buttons)
        {
            var button = tint == null ? MacButton.Secondary(text) : MacButton.Tinted(text, tint.Value);
            button.Glyph = glyph;
            button.Surface = () => surface;
            // Fixed widths, as on the tables; Ready to Test is the one label wider than the shared action width.
            var buttonWidth = text == ButtonWidths.ReadyToTestText ? ButtonWidths.ReadyToTestWidth(button.Font) : ButtonWidths.ActionWidth(button.Font);
            button.Bounds = new Rectangle(buttonRight - buttonWidth, (RowH - ButtonH) / 2, buttonWidth, ButtonH);
            button.Enabled = enabled;
            button.AccessibleName = $"{text} {ticketKey ?? title}";
            button.Click += (_, _) => onClick();
            builtButtons.Add(button);
            buttonRight -= buttonWidth + ButtonGap;
        }
        for (var buttonIndex = builtButtons.Count - 1; buttonIndex >= 0; buttonIndex--)
        {
            Controls.Add(builtButtons[buttonIndex]);
        }

        var textRight = (builtButtons.Count == 0 ? width - Pad : builtButtons[^1].Left) - UiScale.S(12);
        var titleX = TextX;
        if (ticketKey != null)
        {
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
                Location = new Point(TextX, TitleY),
            };
            if (linkUrl != null) keyLink.LinkClicked += (_, _) => TaskRow.OpenUrl(linkUrl);
            Controls.Add(keyLink);
            titleX = keyLink.Right + UiScale.S(4);
        }
        Controls.Add(TextLabel(title, TitleFont, Theme.TextPrimary, titleX, TitleY, textRight));
        Controls.Add(TextLabel(detail, DetailFont, Theme.TextSecondary, TextX, DetailY, textRight));
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Divider });
    }

    // Cut with "...", the full text in a tooltip.
    private static Label TextLabel(string text, Font font, Color color, int x, int y, int right)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = false,
            Bounds = new Rectangle(x, y, Math.Max(0, right - x), font.Height + UiScale.S(2)),
            Font = font,
            ForeColor = color,
            BackColor = Color.Transparent,
            UseMnemonic = false,
        };
        ThemedToolTip.Track(label);
        return label;
    }

    // The icon on a soft fill of its own tone, like a FactTag.
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var box = new Rectangle(Pad, (Height - IconBox) / 2, IconBox, IconBox);
        var diameter = UiScale.S(16);
        using (var path = new GraphicsPath())
        {
            path.AddArc(box.X, box.Y, diameter, diameter, 180, 90);
            path.AddArc(box.Right - diameter, box.Y, diameter, diameter, 270, 90);
            path.AddArc(box.Right - diameter, box.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(box.X, box.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(40, _tone));
            graphics.FillPath(fill, path);
        }
        Icons.Draw(graphics, _icon, Icons.Medium, box, _tone);
    }
}
