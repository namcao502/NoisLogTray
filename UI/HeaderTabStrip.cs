using System.Drawing;

namespace NoisLogTray;

// A page's tabs in the shell header, scrolled sideways by the wheel since the header has no room for a scroll bar. The
// page owns the selection (a press raises SelectRequested, the page answers with Select) and defers any SetTabs it causes.
internal sealed class HeaderTabStrip : HeaderToolbar
{
    private static readonly int TabH = UiScale.S(28);
    private static readonly int TabGap = UiScale.S(6);
    private static readonly int WheelStep = UiScale.S(60);

    private static readonly ThemedToolTip TabTip = new();

    private readonly bool _hasMenus;
    private readonly List<HeaderTab> _tabs = new();
    private readonly Dictionary<HeaderTab, int> _tabLefts = new(); // each tab's left edge in the unscrolled strip
    private List<(string Key, string Text, string? ToolTip)> _entries = new();
    private HashSet<string> _waiting = new();
    private string? _selected;
    private int _tabsWidth;
    private int _tabScroll; // how far the strip is scrolled right; the header has no room for a scroll bar

    internal event Action<string>? SelectRequested;
    internal event Action<string>? CloseRequested;
    internal event Action<string, Control>? MenuRequested; // the key, and the tab to open the menu under
    internal event Action? AddRequested;

    internal IReadOnlyList<HeaderTab> Tabs => _tabs;

    // The page names it (AccessibleName, tooltip); null without one.
    internal MacButton? AddButton { get; }

    internal HeaderTabStrip(bool hasMenus, bool hasAddButton)
    {
        _hasMenus = hasMenus;
        if (hasAddButton)
        {
            AddButton = MacButton.Secondary(string.Empty);
            AddButton.Glyph = Icons.Add;
            AddButton.OnWindow = true;
            AddButton.Size = new Size(TabH, TabH);
            AddButton.Click += (_, _) => AddRequested?.Invoke();
            AddButton.MouseWheel += (_, e) => ScrollTabs(-e.Delta / 120 * WheelStep);
            Controls.Add(AddButton);
        }
        Resize += (_, _) => PlaceTabs();
        Lang.Changed += BuildTabs; // the tabs' accessible names follow the language
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Lang.Changed -= BuildTabs;
        base.Dispose(disposing);
    }

    internal void SetTabs(IReadOnlyList<(string Key, string Text, string? ToolTip)> tabs, string? selectedKey, IReadOnlySet<string> waitingKeys)
    {
        _entries = tabs.ToList();
        _selected = selectedKey;
        _waiting = waitingKeys.ToHashSet();
        BuildTabs();
    }

    // Marks the page's selection and scrolls it into view, without rebuilding the tabs.
    internal void Select(string? key)
    {
        _selected = key;
        for (var tabIndex = 0; tabIndex < _tabs.Count; tabIndex++) _tabs[tabIndex].Selected = _entries[tabIndex].Key == key;
        ScrollSelectedIntoView();
    }

    // Tabs whose Claude waits for the user get a dot.
    internal void SetWaiting(IReadOnlySet<string> waitingKeys)
    {
        _waiting = waitingKeys.ToHashSet();
        for (var tabIndex = 0; tabIndex < _tabs.Count; tabIndex++)
        {
            _tabs[tabIndex].ShowDot = _waiting.Contains(_entries[tabIndex].Key);
            _tabs[tabIndex].AccessibleName = TabAccessibleName(_entries[tabIndex]);
        }
    }

    private string TabAccessibleName((string Key, string Text, string? ToolTip) entry) =>
        _waiting.Contains(entry.Key) ? Lang.T($"{entry.Text}, Claude is waiting", $"{entry.Text}, Claude đang chờ") : entry.Text;

    private void BuildTabs()
    {
        SuspendLayout();
        foreach (var staleTab in _tabs) staleTab.Dispose();
        _tabs.Clear();
        _tabLefts.Clear();
        var nextLeft = 0;
        foreach (var entry in _entries)
        {
            var key = entry.Key;
            var tab = new HeaderTab(entry.Text, TabH, _hasMenus)
            {
                AccessibleName = TabAccessibleName(entry),
                AccessibleDescription = _hasMenus
                    ? Lang.T("Delete closes the tab, Alt+Down opens its menu", "Delete để đóng tab, Alt+Down để mở menu")
                    : Lang.T("Delete closes the tab", "Delete để đóng tab"),
                ShowDot = _waiting.Contains(key),
                Selected = key == _selected,
            };
            if (entry.ToolTip != null) TabTip.SetUntilDisposed(tab, entry.ToolTip);
            tab.SelectRequested += () => SelectRequested?.Invoke(key);
            tab.CloseRequested += () => CloseRequested?.Invoke(key);
            tab.MenuRequested += () => MenuRequested?.Invoke(key, tab);
            tab.MouseWheel += (_, e) => ScrollTabs(-e.Delta / 120 * WheelStep);
            _tabLefts[tab] = nextLeft;
            nextLeft += tab.Width + TabGap;
            Controls.Add(tab);
            _tabs.Add(tab);
        }
        _tabsWidth = Math.Max(0, nextLeft - TabGap);
        AddButton?.BringToFront(); // over a tab that scrolls under it
        ResumeLayout();
        ScrollSelectedIntoView();
    }

    private void ScrollTabs(int distance)
    {
        _tabScroll += distance;
        PlaceTabs();
    }

    private void ScrollSelectedIntoView()
    {
        var selectedIndex = _entries.FindIndex(entry => entry.Key == _selected);
        if (selectedIndex >= 0 && selectedIndex < _tabs.Count)
        {
            var tab = _tabs[selectedIndex];
            var left = _tabLefts[tab];
            if (left < _tabScroll) _tabScroll = left;
            else if (left + tab.Width > _tabScroll + TabsRoom) _tabScroll = left + tab.Width - TabsRoom;
        }
        PlaceTabs();
    }

    // The width the tabs may use: all of it, less the "+" and its gap.
    private int TabsRoom => ClientSize.Width - (AddButton == null ? 0 : AddButton.Width + TabGap);

    // The scroll is clamped here, so a wider header or a closed tab never leaves a gap at the right.
    private void PlaceTabs()
    {
        _tabScroll = Math.Clamp(_tabScroll, 0, Math.Max(0, _tabsWidth - TabsRoom));
        var top = (ClientSize.Height - TabH) / 2;
        foreach (var tab in _tabs) tab.Location = new Point(_tabLefts[tab] - _tabScroll, top);
        if (AddButton != null)
        {
            var afterLastTab = _tabs.Count == 0 ? 0 : _tabsWidth - _tabScroll + TabGap;
            AddButton.Location = new Point(Math.Max(0, Math.Min(afterLastTab, ClientSize.Width - AddButton.Width)), top);
        }
    }
}
