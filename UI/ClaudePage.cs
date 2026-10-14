using System.Drawing;

namespace NoisLogTray;

// The Terminal page: one header tab per terminal (a ClaudeSessionHost TrayApp owns, with its tab list), the selected
// one's TerminalView below; a tab's ▾ picks its folder and "+" opens another terminal.
internal sealed class ClaudePage : UserControl, IShellPage
{
    private static readonly ThemedToolTip AddTip = new();

    private readonly Func<string, ClaudeSessionHost> _hostFor;
    private readonly Func<IReadOnlyList<string>> _folderChoices;
    private readonly HeaderTabStrip _tabStrip = new(hasMenus: true, hasAddButton: true);
    private readonly Panel _viewHost = new() { Dock = DockStyle.Fill };
    private readonly Label _empty = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    private readonly ContextMenuStrip _folderMenu = ThemedMenuRenderer.CreateMenu();
    private readonly Dictionary<string, TerminalView> _views = new();
    private List<(string Name, string Directory)> _terminals = new();
    private HashSet<string> _waiting = new();
    private string? _selected;

    internal event Action<ShellPage>? PageRequested;
    internal event Action? AddRequested;
    // Raised from a tab's mouse or key handler: defer the SetTabs it leads to, since SetTabs disposes that tab.
    internal event Action<string>? CloseRequested;
    // A terminal's new folder (name, folder), after a running terminal's question; the page restarts it right after.
    internal event Action<string, string>? FolderChosen;
    // A tab click or SetTabs moved the selection; TrayApp remembers it (and must not call SetTabs from here).
    internal event Action<string?>? SelectionChanged;

    internal ClaudePage(Func<string, ClaudeSessionHost> hostFor, Func<IReadOnlyList<string>> folderChoices)
    {
        _hostFor = hostFor;
        _folderChoices = folderChoices;
        _folderMenu.ShowCheckMargin = true;

        _empty.Font = UiScale.Font("Segoe UI", 10F);
        _empty.UseMnemonic = false;
        _viewHost.Controls.Add(_empty);
        Controls.Add(_viewHost);

        _tabStrip.SelectRequested += SelectTab;
        _tabStrip.CloseRequested += name => CloseRequested?.Invoke(name);
        _tabStrip.AddRequested += () => AddRequested?.Invoke();
        // Selected first, so the folder change lands on the terminal in view.
        _tabStrip.MenuRequested += (name, tab) =>
        {
            SelectTab(name);
            ShowFolderMenu(name, tab);
        };

        AddTip.SetUntilDisposed(_tabStrip.AddButton!, string.Empty);
        ApplyTheme();
        ApplyLanguage();
        Theme.Changed += ApplyTheme;
        Lang.Changed += ApplyLanguage;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= ApplyTheme;
            Lang.Changed -= ApplyLanguage;
            _folderMenu.Dispose();
            _tabStrip.Dispose(); // it lives in the shell header, not among this page's controls
        }
        base.Dispose(disposing);
    }

    internal Control HeaderControl => _tabStrip;

    internal bool HasOpenTabs => _terminals.Count != 0;

    // Disposes the views of removed terminals (a view only detaches from its host) and selects `selected`, else keeps
    // the current, else the first. A view is built the first time its terminal is shown.
    internal void SetTabs(IReadOnlyList<(string Name, string Directory)> terminals, string? selected)
    {
        _terminals = terminals.ToList();
        foreach (var removedName in _views.Keys.Where(name => !_terminals.Exists(terminal => terminal.Name == name)).ToList())
        {
            _views[removedName].Dispose();
            _views.Remove(removedName);
        }

        var previous = _selected;
        if (selected != null && _terminals.Exists(terminal => terminal.Name == selected)) _selected = selected;
        else if (_selected == null || !_terminals.Exists(terminal => terminal.Name == _selected))
            _selected = _terminals.Count != 0 ? _terminals[0].Name : null;

        _tabStrip.SetTabs(_terminals.Select(terminal => (terminal.Name, TerminalTabs.Label(terminal.Directory), (string?)terminal.Directory)).ToList(),
            _selected, _waiting);
        ShowSelected();
        if (_selected != previous) SelectionChanged?.Invoke(_selected);
    }

    // Terminals whose Claude waits for the user get a dot.
    internal void SetWaiting(IReadOnlyCollection<string> names)
    {
        _waiting = names.ToHashSet();
        _tabStrip.SetWaiting(_waiting);
    }

    public void OnPageShown()
    {
        ShowSelected();
    }

    public void OnActivatedRefresh()
    {
        if (_selected != null && _views.TryGetValue(_selected, out var view)) view.FocusTerminal();
    }

    public void OnHidden()
    {
    }

    private void SelectTab(string name)
    {
        if (_selected == name) return;
        _selected = name;
        ShowSelected();
        SelectionChanged?.Invoke(_selected);
    }

    private void ShowSelected()
    {
        var isShown = Visible && IsHandleCreated;
        if (_selected != null && isShown && !_views.ContainsKey(_selected)) CreateView(_selected);
        foreach (var (name, view) in _views) view.Visible = name == _selected;
        _tabStrip.Select(_selected);
        _empty.Visible = _selected == null;
        if (_selected != null && isShown) _views[_selected].Activate();
    }

    private void CreateView(string name)
    {
        var view = new TerminalView(_hostFor(name)) { Dock = DockStyle.Fill, Visible = false };
        view.PageRequested += page => PageRequested?.Invoke(page);
        // Not tied to a ticket, so there is no Files view to show a Ctrl+clicked file in.
        view.FileRequested += (filePath, line) => TicketLauncher.TryOpenVsCode(filePath, line);
        _viewHost.Controls.Add(view);
        _views[name] = view;
    }

    // Rebuilt on every open, so a folder picked elsewhere or deleted since shows up right. The picks are deferred: they
    // may ask a question and lead to SetTabs, which disposes the tab the menu hangs from.
    private void ShowFolderMenu(string name, Control tab)
    {
        var directory = TerminalDirectory(name);
        _folderMenu.Items.Clear();
        foreach (var folder in _folderChoices())
        {
            var item = new ToolStripMenuItem(folder) { Checked = string.Equals(folder, directory, StringComparison.OrdinalIgnoreCase) };
            item.Click += (_, _) => BeginInvoke(() => ChooseFolder(name, folder));
            _folderMenu.Items.Add(item);
        }
        _folderMenu.Items.Add(new ToolStripSeparator());
        var browse = new ToolStripMenuItem(Lang.T("Choose another folder...", "Chọn thư mục khác..."));
        browse.Click += (_, _) => BeginInvoke(() => BrowseFolder(name));
        _folderMenu.Items.Add(browse);
        ThemedMenuRenderer.ShowBelow(_folderMenu, tab);
    }

    private void BrowseFolder(string name)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = Lang.T("Choose the folder Claude runs in", "Chọn thư mục để chạy Claude"),
            UseDescriptionForTitle = true,
        };
        var directory = TerminalDirectory(name);
        if (directory != null && Directory.Exists(directory)) dialog.InitialDirectory = directory;
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK) ChooseFolder(name, dialog.SelectedPath);
    }

    // A running terminal (maybe Claude mid-task) is only stopped after a Yes.
    private void ChooseFolder(string name, string folder)
    {
        if (!_terminals.Exists(terminal => terminal.Name == name)) return; // closed while the menu was open
        var host = _hostFor(name);
        if (string.Equals(folder, TerminalDirectory(name), StringComparison.OrdinalIgnoreCase) && host.IsRunning) return;
        if (host.IsRunning)
        {
            var changeFolder = ConfirmDialog.Ask(FindForm(), Lang.T("Change folder?", "Đổi thư mục?"),
                Lang.T($"The terminal is running. Stop it and start Claude in {folder}?", $"Terminal đang chạy. Dừng nó và mở Claude ở {folder}?"),
                Lang.T("Change folder", "Đổi thư mục"), destructive: true);
            if (!changeFolder) return;
        }
        FolderChosen?.Invoke(name, folder);
        if (_views.TryGetValue(name, out var view)) view.RestartSession();
    }

    private string? TerminalDirectory(string name) => _terminals.Find(terminal => terminal.Name == name).Directory;

    private void ApplyTheme()
    {
        BackColor = Theme.WindowBg;
        _viewHost.BackColor = Theme.WindowBg;
        _empty.ForeColor = Theme.TextSecondary;
    }

    private void ApplyLanguage()
    {
        _empty.Text = Lang.T("No terminal open - press + to start one.", "Chưa mở terminal nào - bấm + để mở.");
        var addText = Lang.T("New terminal", "Mở terminal mới");
        _tabStrip.AddButton!.AccessibleName = addText;
        AddTip.SetToolTip(_tabStrip.AddButton, addText);
    }
}
