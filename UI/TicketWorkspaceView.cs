using System.Drawing;

namespace NoisLogTray;

// One open ticket: a header over its TerminalView, ChangesView or TeamsPostView; the last two are created on their
// first click, then every view stays alive while the tab is open.
internal sealed class TicketWorkspaceView : UserControl
{
    private static readonly int Pad = UiScale.S(12);
    private static readonly int RightPad = UiScale.S(8);
    private static readonly int TopPad = UiScale.S(8);
    private static readonly int ButtonH = UiScale.S(28);
    private static readonly int ButtonGap = UiScale.S(6);
    private static readonly int RowGap = UiScale.S(4);

    private static readonly Font KeyFont = UiScale.Font("Segoe UI", 10.5F, FontStyle.Bold);
    private static readonly Font SummaryFont = UiScale.Font("Segoe UI", 10F);
    private static readonly Font PrFont = UiScale.Font("Segoe UI", 8.5F);

    private readonly string _jiraBaseUrl;
    private readonly Func<IReadOnlyList<TicketWorktree>> _worktrees;
    private readonly Func<TeamsPostView?> _createTeamsPost;
    private readonly Panel _header = new() { Dock = DockStyle.Top };
    private readonly Panel _divider = new() { Dock = DockStyle.Bottom, Height = 1 };
    private readonly Panel _body = new() { Dock = DockStyle.Fill };
    private readonly LinkLabel _keyLink = new();
    private readonly Label _summary = new();
    private readonly FlowLayoutPanel _facts = NewRow();
    private readonly FlowLayoutPanel _prLinks = NewRow();
    private readonly MacButton _claudeButton = MacButton.Secondary("Claude");
    private readonly MacButton _changesButton = MacButton.Secondary(string.Empty);
    private readonly MacButton _vsCodeButton = MacButton.Secondary("VS Code");
    private readonly MacButton _teamsButton = MacButton.Secondary("Teams");
    private readonly MacButton _collapseButton = MacButton.Secondary(string.Empty);
    private readonly TerminalView _terminal;
    private ChangesView? _changes;
    private TeamsPostView? _teamsPost;
    private BodyView _bodyView = BodyView.Claude;
    private (JiraTicket Ticket, TicketSection Section)? _found;
    private IReadOnlyList<TicketPr> _prs = new List<TicketPr>();
    private bool _headerCollapsed;

    // Ctrl+digit typed inside either view.
    internal event Action<ShellPage>? PageRequested;
    internal event Action<string>? VsCodeRequested;
    internal event Action? HeaderCollapseToggled;

    internal string TicketKey { get; }

    internal TicketWorkspaceView(string ticketKey, string jiraBaseUrl, ClaudeSessionHost session,
        Func<IReadOnlyList<TicketWorktree>> worktrees, Func<TeamsPostView?> createTeamsPost)
    {
        TicketKey = ticketKey;
        _jiraBaseUrl = jiraBaseUrl;
        _worktrees = worktrees;
        _createTeamsPost = createTeamsPost;
        BackColor = Theme.WindowBg;

        _terminal = new TerminalView(session) { Dock = DockStyle.Fill };
        _terminal.PageRequested += OnViewPageRequested;
        _body.Controls.Add(_terminal);

        BuildHeader();
        Controls.Add(_body);
        Controls.Add(_header);
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
            _terminal.PageRequested -= OnViewPageRequested;
            if (_changes != null) _changes.PageRequested -= OnViewPageRequested;
        }
        base.Dispose(disposing);
    }

    // Started first, then Done, New, Dismissed (with the section it was dismissed from).
    internal static (JiraTicket Ticket, TicketSection Section)? FindTicket(string key,
        IReadOnlyDictionary<string, JiraTicket> started, IReadOnlyDictionary<string, JiraTicket> done,
        IReadOnlyDictionary<string, JiraTicket> pending, IReadOnlyDictionary<string, DismissedTicket> dismissed)
    {
        if (started.TryGetValue(key, out var startedTicket)) return (startedTicket, TicketSection.InProgress);
        if (done.TryGetValue(key, out var doneTicket)) return (doneTicket, TicketSection.Done);
        if (pending.TryGetValue(key, out var pendingTicket)) return (pendingTicket, TicketSection.New);
        if (dismissed.TryGetValue(key, out var dismissedTicket)) return (dismissedTicket.Ticket, dismissedTicket.Origin);
        return null;
    }

    private enum BodyView
    {
        Claude,
        Changes,
        Teams,
    }

    internal void Activate()
    {
        if (_bodyView == BodyView.Changes && _changes != null) _changes.Activate();
        else if (_bodyView == BodyView.Teams && _teamsPost != null) _teamsPost.Activate();
        else _terminal.Activate();
    }

    // Collapsed, the header keeps only its key line (and the view buttons) to leave the room to the views.
    internal void SetHeaderCollapsed(bool collapsed)
    {
        _headerCollapsed = collapsed;
        UpdateCollapseButton();
        LayoutHeader();
    }

    internal void ShowInfo((JiraTicket Ticket, TicketSection Section)? found, IReadOnlyList<TicketPr> prs)
    {
        _found = found;
        _prs = prs;
        RenderInfo();
    }

    private static FlowLayoutPanel NewRow() => new()
    {
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = true, // a long row wraps instead of running under the edge
        BackColor = Color.Transparent,
        Margin = new Padding(0),
    };

    private void BuildHeader()
    {
        _keyLink.AutoSize = false;
        _keyLink.TextAlign = ContentAlignment.MiddleLeft;
        _keyLink.Text = TicketKey;
        _keyLink.Font = KeyFont;
        _keyLink.LinkBehavior = LinkBehavior.HoverUnderline;
        _keyLink.BackColor = Color.Transparent;
        _keyLink.UseMnemonic = false;
        _keyLink.LinkClicked += (_, _) => TaskRow.OpenUrl($"{_jiraBaseUrl}/browse/{TicketKey}");

        // AutoEllipsis also shows the full text as a tooltip when it is cut.
        _summary.AutoSize = false;
        _summary.AutoEllipsis = true;
        _summary.TextAlign = ContentAlignment.MiddleLeft;
        _summary.Font = SummaryFont;
        _summary.BackColor = Color.Transparent;
        _summary.UseMnemonic = false;

        _claudeButton.AccessibleName = "Claude";
        _vsCodeButton.AccessibleName = "VS Code";
        foreach (var button in new[] { _claudeButton, _changesButton, _teamsButton, _vsCodeButton })
        {
            button.OnWindow = true;
            button.Size = new Size(ButtonWidths.ToolbarWidth(button.Font), ButtonH);
        }
        _claudeButton.Click += (_, _) => ShowBody(BodyView.Claude);
        _changesButton.Click += (_, _) => ShowBody(BodyView.Changes);
        _teamsButton.Click += (_, _) => ShowBody(BodyView.Teams);
        _vsCodeButton.Click += (_, _) => VsCodeRequested?.Invoke(TicketKey);
        _collapseButton.OnWindow = true;
        _collapseButton.Size = new Size(ButtonH, ButtonH);
        _collapseButton.Click += (_, _) => HeaderCollapseToggled?.Invoke();
        UpdateViewButtons();
        UpdateCollapseButton();

        _header.Controls.Add(_divider);
        _header.Controls.AddRange(new Control[] { _keyLink, _summary, _facts, _prLinks, _claudeButton, _changesButton, _teamsButton, _vsCodeButton, _collapseButton });
        _header.Resize += (_, _) => LayoutHeader();
        LayoutHeader();
    }

    // Row 1: key, summary and the buttons at the right; then the facts and the PR links, each wrapping (hidden collapsed).
    private void LayoutHeader()
    {
        var width = _header.ClientSize.Width;
        var buttonX = width - RightPad;
        foreach (var button in new[] { _collapseButton, _vsCodeButton, _teamsButton, _changesButton, _claudeButton })
        {
            buttonX -= button.Width;
            button.SetBounds(buttonX, TopPad, button.Width, ButtonH);
            buttonX -= ButtonGap;
        }
        _keyLink.SetBounds(Pad, TopPad, _keyLink.PreferredWidth, ButtonH);
        var summaryX = _keyLink.Right + UiScale.S(8);
        _summary.SetBounds(summaryX, TopPad, Math.Max(0, buttonX + ButtonGap - UiScale.S(8) - summaryX), ButtonH);

        var rowWidth = Math.Max(0, width - 2 * Pad);
        var factsHeight = _facts.GetPreferredSize(new Size(rowWidth, 0)).Height;
        _facts.SetBounds(Pad, TopPad + ButtonH + RowGap, rowWidth, factsHeight);
        var prLinksHeight = _prLinks.GetPreferredSize(new Size(rowWidth, 0)).Height;
        _prLinks.SetBounds(Pad, _facts.Bottom + RowGap, rowWidth, prLinksHeight);

        _facts.Visible = !_headerCollapsed;
        _prLinks.Visible = !_headerCollapsed;
        var height = _headerCollapsed ? TopPad + ButtonH + TopPad : _prLinks.Bottom + TopPad;
        if (_header.Height != height) _header.Height = height;
    }

    private void RenderInfo()
    {
        _summary.Text = _found == null ? Lang.T("(not on the board)", "(không có trên board)") : _found.Value.Ticket.Summary;
        _summary.ForeColor = _found == null ? Theme.TextSecondary : Theme.TextPrimary;

        var facts = new List<Control>();
        if (_found != null)
        {
            foreach (var (text, color) in TaskRow.Facts(_found.Value.Ticket, _found.Value.Section))
                facts.Add(TaskRow.Fact(text, color));
        }
        ReplaceItems(_facts, facts);
        ReplaceItems(_prLinks, BuildPrLinks());
        LayoutHeader();
    }

    private List<Control> BuildPrLinks()
    {
        var links = new List<Control>();
        foreach (var pr in _prs)
        {
            var comments = pr.CommentCount == 0
                ? ""
                : Lang.T($" · {pr.CommentCount} comment{(pr.CommentCount == 1 ? "" : "s")}", $" · {pr.CommentCount} bình luận");
            var link = new LinkLabel
            {
                Text = $"{pr.RepoLabel} {pr.Branch} {DashboardDataBuilder.StatusGlyph(pr.Status)} #{pr.PullRequestId}{comments}",
                AutoSize = true,
                Margin = new Padding(0, 0, UiScale.S(14), 0),
                Font = PrFont,
                LinkColor = Theme.Accent,
                ActiveLinkColor = Theme.AccentHover,
                LinkBehavior = LinkBehavior.HoverUnderline,
                BackColor = Color.Transparent,
                UseMnemonic = false,
            };
            link.LinkClicked += (_, _) => TaskRow.OpenUrl(pr.Url);
            links.Add(link);
        }
        if (links.Count == 0) links.Add(TaskRow.Fact(Lang.T("No PR yet", "Chưa có PR"), Theme.TextSecondary));
        return links;
    }

    private static void ReplaceItems(FlowLayoutPanel row, List<Control> items)
    {
        var old = row.Controls.Cast<Control>().ToList();
        row.Controls.Clear();
        foreach (var control in old) control.Dispose();
        row.Controls.AddRange(items.ToArray());
    }

    private void ShowBody(BodyView bodyView)
    {
        if (bodyView == BodyView.Changes && _changes == null)
        {
            _changes = new ChangesView(TicketKey, _worktrees) { Dock = DockStyle.Fill };
            _changes.PageRequested += OnViewPageRequested;
            _body.Controls.Add(_changes);
        }
        if (bodyView == BodyView.Teams && _teamsPost == null)
        {
            _teamsPost = _createTeamsPost();
            if (_teamsPost == null) return; // no workflow URL yet: TrayApp has said so
            _teamsPost.Dock = DockStyle.Fill;
            _body.Controls.Add(_teamsPost);
        }
        _bodyView = bodyView;
        _terminal.Visible = bodyView == BodyView.Claude;
        if (_changes != null) _changes.Visible = bodyView == BodyView.Changes;
        if (_teamsPost != null) _teamsPost.Visible = bodyView == BodyView.Teams;
        UpdateViewButtons();
        Activate();
    }

    private void UpdateViewButtons()
    {
        _claudeButton.Selected = _bodyView == BodyView.Claude;
        _changesButton.Selected = _bodyView == BodyView.Changes;
        _teamsButton.Selected = _bodyView == BodyView.Teams;
    }

    private void UpdateCollapseButton()
    {
        _collapseButton.Text = _headerCollapsed ? "▾" : "▴";
        _collapseButton.AccessibleName = _headerCollapsed
            ? Lang.T("Show the ticket details", "Hiện chi tiết ticket")
            : Lang.T("Hide the ticket details", "Ẩn chi tiết ticket");
    }

    private void OnViewPageRequested(ShellPage page)
    {
        PageRequested?.Invoke(page);
    }

    private void ApplyTheme()
    {
        BackColor = Theme.WindowBg;
        _header.BackColor = Theme.WindowBg;
        _body.BackColor = Theme.WindowBg;
        _divider.BackColor = Theme.Divider;
        _keyLink.LinkColor = Theme.Accent;
        _keyLink.ActiveLinkColor = Theme.AccentHover;
        RenderInfo();
    }

    private void ApplyLanguage()
    {
        _changesButton.Text = Lang.T("Changes", "Thay đổi");
        _changesButton.AccessibleName = _changesButton.Text;
        _teamsButton.AccessibleName = Lang.T("Post to Teams", "Đăng Teams");
        UpdateCollapseButton();
        RenderInfo();
    }
}
