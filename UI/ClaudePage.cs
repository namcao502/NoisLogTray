namespace NoisLogTray;

// The general Claude page: one TerminalView over the ClaudeSessionHost that TrayApp owns.
internal sealed class ClaudePage : UserControl, IShellPage
{
    private readonly TerminalView _terminal;

    internal event Action<ShellPage>? PageRequested;
    internal event Action<string>? FolderChosen;

    internal ClaudePage(ClaudeSessionHost host, Func<IReadOnlyList<string>> folderChoices)
    {
        _terminal = new TerminalView(host, folderChoices) { Dock = DockStyle.Fill };
        _terminal.PageRequested += page => PageRequested?.Invoke(page);
        _terminal.FolderChosen += folder => FolderChosen?.Invoke(folder);
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
