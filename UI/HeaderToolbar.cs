namespace NoisLogTray;

// A page's controls in the shell header (ShellForm.AddPage's header control): the Work board's search and buttons,
// the Ticket tabs. Its empty part lets the mouse through to the header behind it, so a drag there still moves the
// window and a double-click still maximizes it, as on the rest of the header.
internal sealed class HeaderToolbar : Panel
{
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;

    internal HeaderToolbar()
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.WindowBg;
        Theme.Changed += ApplyTheme;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= ApplyTheme;
        base.Dispose(disposing);
    }

    private void ApplyTheme() => BackColor = Theme.WindowBg;

    // Child controls are windows of their own and hit-test as usual; only the toolbar's own background is see-through.
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmNcHitTest)
        {
            m.Result = (IntPtr)HtTransparent;
            return;
        }
        base.WndProc(ref m);
    }
}
