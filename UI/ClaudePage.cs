namespace NoisLogTray;

// The Terminal page: a TerminalView over the general ClaudeSessionHost that TrayApp owns (a PowerShell that runs claude,
// so /exit leaves its prompt). Its header holds the Folder menu.
internal sealed class ClaudePage : UserControl, IShellPage
{
    private readonly TerminalView _terminal;
    private readonly HeaderToolbar _header = new();

    internal event Action<ShellPage>? PageRequested;
    internal event Action<string>? FolderChosen;

    // The Folder menu, shown in the shell header beside the page title.
    internal Control HeaderControl => _header;

    internal ClaudePage(ClaudeSessionHost claudeHost, Func<IReadOnlyList<string>> folderChoices)
    {
        _terminal = new TerminalView(claudeHost, folderChoices) { Dock = DockStyle.Fill };
        _terminal.PageRequested += page => PageRequested?.Invoke(page);
        // Not tied to a ticket, so there is no Files view to show a Ctrl+clicked file in.
        _terminal.FileRequested += (filePath, line) => TicketLauncher.TryOpenVsCode(filePath, line);
        _terminal.FolderChosen += folder => FolderChosen?.Invoke(folder);
        Controls.Add(_terminal);

        if (_terminal.FolderButton != null) _header.Controls.Add(_terminal.FolderButton);
        _header.Resize += (_, _) => LayoutHeader();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _header.Dispose(); // it lives in the shell header, not among this page's controls
        base.Dispose(disposing);
    }

    private void LayoutHeader()
    {
        var folderButton = _terminal.FolderButton;
        if (folderButton == null) return;
        var buttonHeight = UiScale.S(30);
        folderButton.SetBounds(0, (_header.ClientSize.Height - buttonHeight) / 2, folderButton.Width, buttonHeight);
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
