using System.Drawing;

namespace NoisLogTray;

// A ToolTip drawn in the app's theme (the card surface, its border, the theme's text) instead of the Windows box.
internal sealed class ThemedToolTip : ToolTip
{
    private static readonly Font TipFont = UiScale.Font("Segoe UI", 9F);
    private static readonly int PadX = UiScale.S(8);
    private static readonly int PadY = UiScale.S(5);
    private static readonly int MaxTextWidth = UiScale.S(420);
    private const TextFormatFlags TextFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;

    // The full text of every tracked AutoEllipsis label.
    internal static readonly ThemedToolTip FullText = new();

    internal ThemedToolTip()
    {
        OwnerDraw = true;
        Popup += OnPopup;
        Draw += OnDraw;
    }

    // An AutoEllipsis label pops its own Windows tooltip while its text is cut. Any ToolTip text on the label switches
    // that one off for good, so the themed one takes over and pops only while the text is cut.
    internal static void Track(Label label)
    {
        label.AutoEllipsis = true;
        FullText.SetUntilDisposed(label, label.Text);
        label.TextChanged += (_, _) => FullText.SetToolTip(label, label.Text);
    }

    // For a shared instance: the tip goes with the control, or the instance would hold the control forever.
    internal void SetUntilDisposed(Control control, string text)
    {
        SetToolTip(control, text);
        control.Disposed += (_, _) => SetToolTip(control, null);
    }

    internal static bool IsCut(Label label)
    {
        var textSize = TextRenderer.MeasureText(label.Text, label.Font, new Size(label.ClientSize.Width, int.MaxValue), TextFlags);
        return textSize.Width > label.ClientSize.Width || textSize.Height > label.ClientSize.Height;
    }

    internal static Size Measure(string text)
    {
        var textSize = TextRenderer.MeasureText(text, TipFont, new Size(MaxTextWidth, int.MaxValue), TextFlags);
        return new Size(textSize.Width + 2 * PadX, textSize.Height + 2 * PadY);
    }

    private void OnPopup(object? sender, PopupEventArgs e)
    {
        var control = e.AssociatedControl;
        var text = control == null ? null : GetToolTip(control);
        if (string.IsNullOrEmpty(text) || control is Label label && label.AutoEllipsis && !IsCut(label))
        {
            e.Cancel = true;
            return;
        }
        e.ToolTipSize = Measure(text);
    }

    private static void OnDraw(object? sender, DrawToolTipEventArgs e)
    {
        var bounds = e.Bounds;
        using (var fill = new SolidBrush(Theme.CardSurface))
            e.Graphics.FillRectangle(fill, bounds);
        using (var border = new Pen(Theme.CardBorder))
            e.Graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        TextRenderer.DrawText(e.Graphics, e.ToolTipText, TipFont, Rectangle.Inflate(bounds, -PadX, -PadY), Theme.TextPrimary, TextFlags);
    }
}
