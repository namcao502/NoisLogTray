using System.Drawing;

namespace NoisLogTray;

// The Ticket page: a strip of tabs, one per open ticket, in the shell header (HeaderControl), and the selected ticket's
// TicketWorkspaceView below (all views stay alive while their tab exists). TrayApp owns the tab list and the per-ticket
// sessions; this page only shows them.
internal sealed class TicketWorkspacePage : UserControl, IShellPage
{
    private static readonly int TabH = UiScale.S(28);
    private static readonly int TabGap = UiScale.S(6);
    private static readonly int WheelStep = UiScale.S(60);

    private readonly TicketStore? _store;
    private readonly string _jiraBaseUrl;
    private readonly Func<string> _devRepoPath;
    private readonly Func<string, ClaudeSessionHost> _sessionFor;
    private readonly Func<string, TeamsPostView?> _teamsPostFor;
    private readonly HeaderToolbar _tabStrip = new();
    private readonly Panel _viewHost = new() { Dock = DockStyle.Fill };
    private readonly Label _empty = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Dictionary<string, TicketWorkspaceView> _views = new();
    private readonly Dictionary<string, TicketTab> _tabButtons = new();
    private readonly Dictionary<string, int> _tabLefts = new(); // each tab's left edge in the unscrolled strip
    private List<string> _tabKeys = new();
    private HashSet<string> _waiting = new();
    private int _tabsWidth;
    private int _tabScroll; // how far the strip is scrolled right; the header has no room for a scroll bar
    private string? _selected;
    private bool _headerCollapsed = AppSettings.Load().TicketHeaderCollapsed;

    // Raised from a tab's mouse or key handler: defer the SetTabs it leads to, since SetTabs disposes that tab.
    internal event Action<string>? CloseRequested;
    internal event Action<string>? VsCodeRequested;

    // A tab click or SetTabs moved the selection; TrayApp remembers it (and must not call SetTabs from here).
    internal event Action<string?>? SelectionChanged;
    internal event Action<ShellPage>? PageRequested;

    internal TicketWorkspacePage(TicketStore? store, string jiraBaseUrl, Func<string> devRepoPath,
        Func<string, ClaudeSessionHost> sessionFor, Func<string, TeamsPostView?> teamsPostFor)
    {
        _store = store;
        _jiraBaseUrl = jiraBaseUrl;
        _devRepoPath = devRepoPath;
        _sessionFor = sessionFor;
        _teamsPostFor = teamsPostFor;

        _empty.Font = UiScale.Font("Segoe UI", 10F);
        _empty.UseMnemonic = false;
        _viewHost.Controls.Add(_empty);
        Controls.Add(_viewHost);
        _tabStrip.Resize += (_, _) => PlaceTabs();
        MinimumSize = UiScale.S(new Size(640, 480));
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
            _tabStrip.Dispose(); // it lives in the shell header, not among this page's controls
        }
        base.Dispose(disposing);
    }

    internal Control HeaderControl => _tabStrip;

    internal bool HasOpenTabs => _tabKeys.Count != 0;

    // Creates views for new keys, disposes views of removed keys, selects `selected` (else keeps the current,
    // else the first). The hosts are TrayApp's: disposing a view only detaches from them.
    internal void SetTabs(IReadOnlyList<string> ticketKeys, string? selected)
    {
        var keys = ticketKeys.Distinct().ToList();
        foreach (var removedKey in _views.Keys.Where(openKey => !keys.Contains(openKey)).ToList())
        {
            var view = _views[removedKey];
            _views.Remove(removedKey);
            view.PageRequested -= OnViewPageRequested;
            view.VsCodeRequested -= OnViewVsCodeRequested;
            view.HeaderCollapseToggled -= OnHeaderCollapseToggled;
            view.Dispose();
        }
        var created = false;
        foreach (var newKey in keys.Where(ticketKey => !_views.ContainsKey(ticketKey)))
        {
            CreateView(newKey);
            created = true;
        }

        var previous = _selected;
        _tabKeys = keys;
        if (selected != null && keys.Contains(selected)) _selected = selected;
        else if (_selected == null || !keys.Contains(_selected)) _selected = keys.Count != 0 ? keys[0] : null;

        BuildTabs();
        if (created) Reload(); // a new view starts with no ticket data
        ShowSelected();
        if (_selected != previous) SelectionChanged?.Invoke(_selected);
    }

    // The board and the PR tracking are read once for every view.
    internal void Reload()
    {
        if (_store == null || _views.Count == 0) return;
        try
        {
            var started = _store.LoadStarted();
            var done = _store.LoadDone();
            var pending = _store.LoadPending();
            var dismissed = _store.LoadDismissed();
            var prTracking = _store.LoadPrTracking();
            foreach (var (key, view) in _views)
                view.ShowInfo(TicketWorkspaceView.FindTicket(key, started, done, pending, dismissed),
                    DashboardDataBuilder.LatestPrs(key, prTracking));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            AppLogger.Error($"[ticket] Reading the board for the Ticket page failed: {ex.Message}");
        }
    }

    public void OnPageShown()
    {
        ActivateSelected();
    }

    public void OnActivatedRefresh()
    {
        ActivateSelected();
    }

    public void OnHidden()
    {
    }

    private void CreateView(string key)
    {
        var view = new TicketWorkspaceView(key, _jiraBaseUrl, _sessionFor(key),
            () => TicketWorktrees.Find(_devRepoPath(), key), () => TicketFiles.DocsPath(_devRepoPath(), key), () => _teamsPostFor(key))
        {
            Dock = DockStyle.Fill,
            Visible = false,
        };
        view.PageRequested += OnViewPageRequested;
        view.VsCodeRequested += OnViewVsCodeRequested;
        view.HeaderCollapseToggled += OnHeaderCollapseToggled;
        view.SetHeaderCollapsed(_headerCollapsed);
        _viewHost.Controls.Add(view);
        _views[key] = view;
    }

    private void SelectTab(string key)
    {
        if (_selected == key) return;
        _selected = key;
        ShowSelected();
        SelectionChanged?.Invoke(_selected);
    }

    private void ShowSelected()
    {
        foreach (var (key, view) in _views) view.Visible = key == _selected;
        foreach (var (key, tab) in _tabButtons) tab.Selected = key == _selected;
        ScrollTabIntoView(_selected);
        _empty.Visible = _selected == null;
        ActivateSelected();
    }

    private void ActivateSelected()
    {
        if (_selected != null && Visible && IsHandleCreated) _views[_selected].Activate();
    }

    // Tabs whose Claude waits for the user get a dot; BuildTabs (a language switch) keeps it.
    internal void SetWaiting(IReadOnlyCollection<string> ticketKeys)
    {
        _waiting = ticketKeys.ToHashSet();
        foreach (var (key, tab) in _tabButtons)
        {
            tab.ShowDot = _waiting.Contains(key);
            tab.AccessibleName = TabAccessibleName(key);
        }
    }

    private string TabAccessibleName(string key) =>
        _waiting.Contains(key) ? Lang.T($"{key}, Claude is waiting", $"{key}, Claude đang chờ") : key;

    // One tab per ticket, its close inside it, left to right. More tabs than the header holds scroll sideways with the
    // mouse wheel over them, and the selected one is always scrolled into view.
    private void BuildTabs()
    {
        _tabStrip.SuspendLayout();
        foreach (var control in _tabStrip.Controls.Cast<Control>().ToList()) control.Dispose();
        _tabButtons.Clear();
        _tabLefts.Clear();
        var nextLeft = 0;
        foreach (var key in _tabKeys)
        {
            var tab = new TicketTab(key, TabH)
            {
                AccessibleName = TabAccessibleName(key),
                AccessibleDescription = Lang.T("Delete closes the tab", "Delete để đóng tab"),
                ShowDot = _waiting.Contains(key),
                Selected = key == _selected, // a rebuild (language switch) keeps the current tab marked
            };
            tab.SelectRequested += () => SelectTab(key);
            tab.CloseRequested += () => CloseRequested?.Invoke(key);
            tab.MouseWheel += (_, e) => ScrollTabs(-e.Delta / 120 * WheelStep);
            _tabLefts[key] = nextLeft;
            nextLeft += tab.Width + TabGap;
            _tabStrip.Controls.Add(tab);
            _tabButtons[key] = tab;
        }
        _tabsWidth = Math.Max(0, nextLeft - TabGap);
        _tabStrip.ResumeLayout();
        ScrollTabIntoView(_selected);
    }

    private void ScrollTabs(int distance)
    {
        _tabScroll += distance;
        PlaceTabs();
    }

    private void ScrollTabIntoView(string? key)
    {
        if (key == null || !_tabButtons.TryGetValue(key, out var tab)) return;
        var left = _tabLefts[key];
        if (left < _tabScroll) _tabScroll = left;
        else if (left + tab.Width > _tabScroll + _tabStrip.ClientSize.Width) _tabScroll = left + tab.Width - _tabStrip.ClientSize.Width;
        PlaceTabs();
    }

    // The scroll is clamped here, so a wider header or a closed tab never leaves a gap at the right.
    private void PlaceTabs()
    {
        _tabScroll = Math.Clamp(_tabScroll, 0, Math.Max(0, _tabsWidth - _tabStrip.ClientSize.Width));
        var top = (_tabStrip.ClientSize.Height - TabH) / 2;
        foreach (var (key, tab) in _tabButtons) tab.Location = new Point(_tabLefts[key] - _tabScroll, top);
    }

    private void OnViewPageRequested(ShellPage page)
    {
        PageRequested?.Invoke(page);
    }

    private void OnViewVsCodeRequested(string key)
    {
        VsCodeRequested?.Invoke(key);
    }

    // One setting for every tab, kept across restarts: collapsing one header collapses them all.
    private void OnHeaderCollapseToggled()
    {
        _headerCollapsed = !_headerCollapsed;
        foreach (var view in _views.Values) view.SetHeaderCollapsed(_headerCollapsed);
        var settings = AppSettings.Load();
        settings.TicketHeaderCollapsed = _headerCollapsed;
        AppSettings.Save(settings);
    }

    private void ApplyTheme()
    {
        BackColor = Theme.WindowBg;
        _viewHost.BackColor = Theme.WindowBg;
        _empty.ForeColor = Theme.TextSecondary;
    }

    private void ApplyLanguage()
    {
        _empty.Text = Lang.T("No ticket open - use Start or Open on Tasks.", "Chưa mở ticket nào - dùng Start hoặc Open ở Tasks.");
        BuildTabs(); // the close buttons' accessible names follow the language
    }
}
