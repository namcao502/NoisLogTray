namespace NoisLogTray;

// The general Claude page: one TerminalView over the ClaudeSessionHost that TrayApp owns.
internal sealed class ClaudePage : UserControl, IShellPage
{
    private readonly TerminalView _terminal;

    internal event Action<ShellPage>? PageRequested;

    internal ClaudePage(ClaudeSessionHost host)
    {
        _terminal = new TerminalView(host) { Dock = DockStyle.Fill };
        _terminal.PageRequested += page => PageRequested?.Invoke(page);
        Controls.Add(_terminal);
    }

    public void OnPageShown()
    {
        _terminal.Activate();
    }

    public void OnActivatedRefresh()
    {
        _terminal.FocusTerminal();
    }

    public void OnHidden()
    {
    }
}
