using System.Drawing;

namespace NoisLogTray;

// A yes/no question in the app's own card instead of a MessageBox. The confirm button names the action ("Quit",
// "Close tab") rather than "Yes", and Enter picks Cancel unless confirming is the safe default.
internal sealed class ConfirmDialog : CardDialog
{
    private static readonly Font TitleFont = UiScale.Font("Segoe UI Semibold", 12F, FontStyle.Bold);
    private static readonly Font MessageFont = UiScale.Font("Segoe UI", 9.5F);
    private static readonly int Pad = UiScale.S(24);
    private static readonly int ContentW = UiScale.S(400);
    private static readonly int ButtonH = UiScale.S(34);
    private static readonly int ButtonGap = UiScale.S(10);
    private static readonly int MinButtonW = UiScale.S(96);
    private static readonly TextFormatFlags WrapFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;

    internal MacButton ConfirmChoice { get; }
    internal MacButton CancelChoice { get; }

    internal ConfirmDialog(string title, string message, string confirmText, bool confirmByDefault, bool destructive)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        var titleLabel = WrappedLabel(title, TitleFont, Theme.TextPrimary, Pad);
        var messageLabel = WrappedLabel(message, MessageFont, Theme.TextSecondary, titleLabel.Bottom + UiScale.S(10));

        ConfirmChoice = destructive ? MacButton.Tinted(confirmText, Theme.Badge) : MacButton.Primary(confirmText);
        ConfirmChoice.DialogResult = DialogResult.OK;
        CancelChoice = MacButton.Secondary(Lang.T("Cancel", "Hủy"));
        CancelChoice.DialogResult = DialogResult.Cancel;

        // Right-aligned, the action last, each as wide as its label needs.
        var buttonsTop = messageLabel.Bottom + UiScale.S(22);
        var right = Pad + ContentW;
        foreach (var button in new[] { ConfirmChoice, CancelChoice })
        {
            var width = Math.Max(MinButtonW, TextRenderer.MeasureText(button.Text, button.Font).Width + UiScale.S(32));
            button.SetBounds(right - width, buttonsTop, width, ButtonH);
            right -= width + ButtonGap;
        }
        CancelChoice.TabIndex = 0;
        ConfirmChoice.TabIndex = 1;

        Controls.Add(titleLabel);
        Controls.Add(messageLabel);
        Controls.Add(CancelChoice);
        Controls.Add(ConfirmChoice);

        AcceptButton = confirmByDefault ? ConfirmChoice : CancelChoice;
        CancelButton = CancelChoice;
        ActiveControl = confirmByDefault ? ConfirmChoice : CancelChoice;
        ClientSize = new Size(ContentW + 2 * Pad, buttonsTop + ButtonH + Pad);
    }

    // Owner null means there is no window to sit over (Quit from the tray menu while the window is hidden), so it
    // centers on the screen, stays on top and shows in the taskbar where it can be found.
    internal static bool Ask(IWin32Window? owner, string title, string message, string confirmText,
        bool confirmByDefault = false, bool destructive = false)
    {
        using var dialog = new ConfirmDialog(title, message, confirmText, confirmByDefault, destructive);
        if (owner == null)
        {
            dialog.StartPosition = FormStartPosition.CenterScreen;
            dialog.TopMost = true;
            dialog.ShowInTaskbar = true;
        }
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }

    private Label WrappedLabel(string text, Font font, Color color, int top)
    {
        var height = TextRenderer.MeasureText(text, font, new Size(ContentW, int.MaxValue), WrapFlags).Height;
        var label = new Label
        {
            Text = text,
            AutoSize = false,
            UseMnemonic = false,
            Font = font,
            ForeColor = color,
            BackColor = Color.Transparent,
            Bounds = new Rectangle(Pad, top, ContentW, height),
        };
        label.MouseDown += (_, e) => OnMouseDown(e);
        return label;
    }
}
