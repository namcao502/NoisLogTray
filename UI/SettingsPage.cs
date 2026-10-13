using System.Drawing;

namespace NoisLogTray;

// The shell's Settings page: the shared SettingsPanel on a card that spans the page (up to MaxWidth).
// Values are re-read each time the page is shown, so leaving the page (or hiding the window) drops
// unsaved edits. A width change rebuilds the panel at the new width, keeping what is typed.
internal sealed class SettingsPage : UserControl, IShellPage
{
    private static readonly int PageMargin = UiScale.S(20);
    private static readonly int MaxWidth = UiScale.S(1100); // past this the fields get too long to scan

    private readonly Card _card = new();
    private SettingsPanel _panel;
    private bool _rebuildPosted;

    internal event Action<IReadOnlyDictionary<string, string>>? SaveRequested;

    internal SettingsPage()
    {
        AutoScroll = true;
        ThemedScrollBar.Attach(this); // sits in the shell's page host beside this page
        BackColor = Theme.WindowBg;
        MinimumSize = UiScale.S(new Size(480, 300));
        _card.Location = new Point(PageMargin, UiScale.S(14));
        Controls.Add(_card);
        _panel = CreatePanel(AppConfig.ReadUserValues(), SettingsPanel.ClientW);
        Theme.Changed += ApplyTheme;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= ApplyTheme;
        base.Dispose(disposing);
    }

    // Mid-verification the panel keeps what is being saved.
    public void OnPageShown()
    {
        if (_panel.IsBusy) return;
        _panel.LoadValues(AppConfig.ReadUserValues());
        _panel.FocusFirstField();
    }

    // Nothing: re-reading on a focus change would wipe text being typed.
    public void OnActivatedRefresh()
    {
    }

    public void OnHidden()
    {
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (!IsHandleCreated || _rebuildPosted || PanelWidth() == _panel.Width) return;
        _rebuildPosted = true;
        BeginInvoke(new Action(() =>
        {
            _rebuildPosted = false;
            // Never rebuild mid-Save: the panel's async click is still running.
            if (IsDisposed || _panel.IsBusy || PanelWidth() == _panel.Width) return;
            var focused = _panel.ContainsFocus;
            var old = _panel;
            _panel = CreatePanel(old.CurrentValues(), PanelWidth());
            old.Dispose();
            if (focused) _panel.FocusFirstField();
        }));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (PanelWidth() == _panel.Width) return;
        var old = _panel;
        _panel = CreatePanel(old.CurrentValues(), PanelWidth());
        old.Dispose();
    }

    private int PanelWidth()
    {
        var available = ClientSize.Width - 2 * PageMargin - _card.Radius;
        return Math.Clamp(available, SettingsPanel.ClientW, MaxWidth);
    }

    private SettingsPanel CreatePanel(IReadOnlyDictionary<string, string> values, int width)
    {
        var panel = new SettingsPanel(values, width);
        // Inset by half the radius, with the card one radius larger, so the panel leaves the rounded corners visible.
        _card.Size = new Size(panel.Width + _card.Radius, panel.Height + _card.Radius);
        panel.Location = new Point(_card.Radius / 2, _card.Radius / 2);
        panel.SaveRequested += values => SaveRequested?.Invoke(values);
        _card.Controls.Add(panel);
        return panel;
    }

    private void ApplyTheme()
    {
        BackColor = Theme.WindowBg;
        Invalidate();
    }
}
