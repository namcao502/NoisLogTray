using System.Drawing;

namespace NoisLogTray;

// The Ticket page: a strip of tabs, one per open ticket, over that ticket's TicketWorkspaceView (all views stay
// alive while their tab exists). TrayApp owns the tab list and the per-ticket sessions; this page only shows them.
internal sealed class TicketWorkspacePage : UserControl, IShellPage
{
    private static readonly int TabH = UiScale.S(28);
    private static readonly int CloseW = UiScale.S(28);
    private static readonly int StripPad = UiScale.S(8);
    private static readonly int TabGap = UiScale.S(8);
    private static readonly int CloseGap = UiScale.S(2);

    private readonly TicketStore? _store;
    private readonly string _jiraBaseUrl;
    private readonly Func<string> _devRepoPath;
    private readonly Func<string, ClaudeSessionHost> _sessionFor;
    private readonly Panel _tabStrip = new() { Dock = DockStyle.Top, AutoScroll = true };
    private readonly Panel _viewHost = new() { Dock = DockStyle.Fill };
    private readonly Label _empty = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Dictionary<string, TicketWorkspaceView> _views = new();
    private readonly Dictionary<string, MacButton> _tabButtons = new();
    private List<string> _tabKeys = new();
    private HashSet<string> _waiting = new();
    private int _tabsWidth;
    private string? _selected;

    // Raised from the × button's Click: defer the SetTabs it leads to, since SetTabs disposes that button.
    internal event Action<string>? CloseRequested;
    internal event Action<string>? VsCodeRequested;

    // A tab click or SetTabs moved the selection; TrayApp remembers it (and must not call SetTabs from here).
    internal event Action<string?>? SelectionChanged;
    internal event Action<ShellPage>? PageRequested;

    internal TicketWorkspacePage(TicketStore? store, string jiraBaseUrl, Func<string> devRepoPath,
        Func<string, ClaudeSessionHost> sessionFor)
    {
        _store = store;
        _jiraBaseUrl = jiraBaseUrl;
        _devRepoPath = devRepoPath;
        _sessionFor = sessionFor;

        _empty.Font = UiScale.Font("Segoe UI", 10F);
        _empty.UseMnemonic = false;
        _viewHost.Controls.Add(_empty);
        Controls.Add(_viewHost);
        Controls.Add(_tabStrip);
        _tabStrip.Resize += (_, _) => FitStripHeight();
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
        }
        base.Dispose(disposing);
    }

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
            () => TicketWorktrees.Find(_devRepoPath(), key))
        {
            Dock = DockStyle.Fill,
            Visible = false,
        };
        view.PageRequested += OnViewPageRequested;
        view.VsCodeRequested += OnViewVsCodeRequested;
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
        if (_selected != null && _tabButtons.TryGetValue(_selected, out var selectedTab)) _tabStrip.ScrollControlIntoView(selectedTab);
        _empty.Visible = _selected == null;
        _tabStrip.Visible = _selected != null;
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

    // A tab button then its close button per ticket, left to right; a strip wider than the page scrolls sideways.
    private void BuildTabs()
    {
        _tabStrip.SuspendLayout();
        foreach (var control in _tabStrip.Controls.Cast<Control>().ToList()) control.Dispose();
        _tabButtons.Clear();
        // Bounds are relative to the scrolled view, so lay out from an unscrolled strip.
        _tabStrip.AutoScrollPosition = Point.Empty;
        var nextLeft = StripPad;
        foreach (var key in _tabKeys)
        {
            var tab = MacButton.Secondary(key);
            tab.OnWindow = true;
            tab.AccessibleName = TabAccessibleName(key);
            tab.ShowDot = _waiting.Contains(key);
            tab.Selected = key == _selected; // a rebuild (language switch) keeps the current tab marked
            tab.Bounds = new Rectangle(nextLeft, StripPad, TextRenderer.MeasureText(key, tab.Font).Width + UiScale.S(24), TabH);
            tab.Click += (_, _) => SelectTab(key);
            nextLeft = tab.Right + CloseGap;

            var close = MacButton.Secondary("×");
            close.OnWindow = true;
            close.AccessibleName = Lang.T($"Close {key}", $"Đóng {key}");
            close.Bounds = new Rectangle(nextLeft, StripPad, CloseW, TabH);
            close.Click += (_, _) => CloseRequested?.Invoke(key);
            nextLeft = close.Right + TabGap;

            _tabStrip.Controls.Add(tab);
            _tabStrip.Controls.Add(close);
            _tabButtons[key] = tab;
        }
        _tabsWidth = nextLeft - TabGap + StripPad;
        _tabStrip.AutoScrollMinSize = new Size(_tabsWidth, 0);
        _tabStrip.ResumeLayout();
        FitStripHeight();
        if (_selected != null && _tabButtons.TryGetValue(_selected, out var selectedTab)) _tabStrip.ScrollControlIntoView(selectedTab);
    }

    // Room for the scroll bar only while the tabs overflow, so it never covers the buttons.
    private void FitStripHeight()
    {
        var overflows = _tabsWidth > _tabStrip.Width;
        var height = 2 * StripPad + TabH + (overflows ? SystemInformation.HorizontalScrollBarHeight : 0);
        if (_tabStrip.Height != height) _tabStrip.Height = height;
    }

    private void OnViewPageRequested(ShellPage page)
    {
        PageRequested?.Invoke(page);
    }

    private void OnViewVsCodeRequested(string key)
    {
        VsCodeRequested?.Invoke(key);
    }

    private void ApplyTheme()
    {
        BackColor = Theme.WindowBg;
        _tabStrip.BackColor = Theme.WindowBg;
        _viewHost.BackColor = Theme.WindowBg;
        _empty.ForeColor = Theme.TextSecondary;
    }

    private void ApplyLanguage()
    {
        _empty.Text = Lang.T("No ticket open - use Start or Open on Tasks.", "Chưa mở ticket nào - dùng Start hoặc Open ở Tasks.");
        BuildTabs(); // the close buttons' accessible names follow the language
    }
}
