using System.Runtime.InteropServices;

namespace NoisLogTray;

// WinForms moves focus only to a control that can take it, so a click on a card, a label or a row leaves a text box
// focused with its caret blinking. After such a click the box lets go, as on a web page.
internal sealed class FocusReleaseFilter : IMessageFilter
{
    private const int WmLButtonDown = 0x0201;

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WmLButtonDown || Control.FromHandle(GetFocus()) is not TextBoxBase focusedBox) return false;
        if (!ReleasesFocus(Control.FromHandle(m.HWnd), focusedBox)) return false;

        // After the click is handled: if it moved focus somewhere (a button, another box), nothing is left to do.
        focusedBox.BeginInvoke(() =>
        {
            if (!focusedBox.IsDisposed && focusedBox.Focused && focusedBox.FindForm() is Form form) form.ActiveControl = null;
        });
        return false;
    }

    // clicked is null for a window WinForms does not own (a WebView2), which takes focus itself. The box's rounded frame
    // and the scroll bars (a multiline box's own sits beside it) keep the box focused.
    internal static bool ReleasesFocus(Control? clicked, TextBoxBase focusedBox) =>
        clicked != null
        && clicked != focusedBox
        && clicked is not RoundedHost
        && clicked is not ThemedScrollBar
        && clicked.FindForm() == focusedBox.FindForm();
}
