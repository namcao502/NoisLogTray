using System.Drawing;

namespace NoisLogTray;

// One open ticket: a header over its TerminalView, ChangesView, FilesView or TeamsPostView; the last three are created
// on their first click, then every view stays alive while the tab is open.
internal sealed class TicketWorkspaceView : UserControl
{
    private static readonly int Pad = UiScale.S(12);
    private static readonly int RightPad = UiScale.S(8);
    private static readonly int TopPad = UiScale.S(8);
    private static readonly int ButtonH = UiScale.S(28);
    private static readonly int ButtonGap = UiScale.S(6);
    private static readonly int RowGap = UiScale.S(4);
    private static readonly int ItemGap = UiScale.S(8);
    private static readonly int MinSummaryWidth = UiScale.S(160);

    private static readonly Font KeyFont = UiScale.Font("Segoe UI", 10.5F, FontStyle.Bold);
    private static readonly Font SummaryFont = UiScale.Font("Segoe UI", 10F);
    private static readonly Font PrFont = UiScale.Font("Segoe UI", 8.5F);
    private static readonly Font PrRepoFont = UiScale.Font("Segoe UI Semibold", 8.5F, FontStyle.Bold);
    private static readonly Font FactValueFont = UiScale.Font("Segoe UI", 9F);
    private static readonly int FactHeight = FactValueFont.Height + UiScale.S(6);

    private static readonly ThemedToolTip FactTip = new();

    private readonly string _jiraBaseUrl;
    private readonly Func<IReadOnlyList<TicketWorktree>> _worktrees;
    private readonly Func<string?> _docsPath;
    private readonly Func<TeamsPostView?> _createTeamsPost;
    private readonly Panel _header = new() { Dock = DockStyle.Top };
    private readonly Panel _divider = new() { Dock = DockStyle.Bottom, Height = 1 };
    private readonly Panel _body = new() { Dock = DockStyle.Fill };
    private readonly LinkLabel _keyLink = new();
    private readonly Label _summary = new();
    private readonly FlowLayoutPanel _facts = NewRow();
    private readonly Panel _prPanel = new() { BackColor = Color.Transparent }; // one PR row per repo
    private readonly MacButton _claudeButton = MacButton.Secondary("Claude");
    private readonly MacButton _changesButton = MacButton.Secondary(string.Empty);
    private readonly MacButton _filesButton = MacButton.Secondary(string.Empty);
    private readonly MacButton _vsCodeButton = MacButton.Secondary("VS Code");
    private readonly MacButton _teamsButton = MacButton.Secondary("Teams");
    private readonly MacButton _collapseButton = MacButton.Secondary(string.Empty);
    private readonly TerminalView _terminal;
    private ChangesView? _changes;
    private FilesView? _files;
    private TeamsPostView? _teamsPost;
    private BodyView _bodyView = BodyView.Claude;
    private (JiraTicket Ticket, TicketSection Section)? _found;
    private IReadOnlyList<TicketPr> _prs = new List<TicketPr>();
    private bool _headerCollapsed;

    // Ctrl+digit typed inside any of the views.
    internal event Action<ShellPage>? PageRequested;
    internal event Action<string>? VsCodeRequested;
    internal event Action? HeaderCollapseToggled;

    internal string TicketKey { get; }

    internal TicketWorkspaceView(string ticketKey, string jiraBaseUrl, ClaudeSessionHost session,
        Func<IReadOnlyList<TicketWorktree>> worktrees, Func<string?> docsPath, Func<TeamsPostView?> createTeamsPost)
    {
        TicketKey = ticketKey;
        _jiraBaseUrl = jiraBaseUrl;
        _worktrees = worktrees;
        _docsPath = docsPath;
        _createTeamsPost = createTeamsPost;
        BackColor = Theme.WindowBg;

        _terminal = new TerminalView(session) { Dock = DockStyle.Fill };
        _terminal.PageRequested += OnViewPageRequested;
        _terminal.FileRequested += OnFileRequested;
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
            _terminal.FileRequested -= OnFileRequested;
            if (_changes != null) _changes.PageRequested -= OnViewPageRequested;
            if (_files != null) _files.PageRequested -= OnViewPageRequested;
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
        Files,
        Teams,
    }

    internal void Activate()
    {
        if (_bodyView == BodyView.Changes && _changes != null) _changes.Activate();
        else if (_bodyView == BodyView.Files && _files != null) _files.Activate();
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
        ThemedToolTip.Track(_summary);
        _summary.TextAlign = ContentAlignment.MiddleLeft;
        _summary.Font = SummaryFont;
        _summary.BackColor = Color.Transparent;
        _summary.UseMnemonic = false;

        _claudeButton.AccessibleName = "Claude";
        _vsCodeButton.AccessibleName = "VS Code";
        foreach (var (button, english, vietnamese) in new[] { (_claudeButton, "Claude", "Claude"), (_changesButton, "Changes", "Thay đổi"),
            (_filesButton, "Files", "File"), (_teamsButton, "Teams", "Teams"), (_vsCodeButton, "VS Code", "VS Code") })
        {
            button.OnWindow = true;
            // Its own label in either language: five toolbar-wide buttons would cover the key at the minimum width.
            button.Size = new Size(Math.Max(TextRenderer.MeasureText(english, button.Font).Width,
                TextRenderer.MeasureText(vietnamese, button.Font).Width) + UiScale.S(24), ButtonH);
        }
        _claudeButton.Click += (_, _) => ShowBody(BodyView.Claude);
        _changesButton.Click += (_, _) => ShowBody(BodyView.Changes);
        _filesButton.Click += (_, _) => ShowBody(BodyView.Files);
        _teamsButton.Click += (_, _) => ShowBody(BodyView.Teams);
        _vsCodeButton.Click += (_, _) => VsCodeRequested?.Invoke(TicketKey);
        _collapseButton.OnWindow = true;
        _collapseButton.Size = new Size(ButtonH, ButtonH);
        _collapseButton.Click += (_, _) => HeaderCollapseToggled?.Invoke();
        UpdateViewButtons();
        UpdateCollapseButton();

        _header.Controls.Add(_divider);
        _header.Controls.AddRange(new Control[] { _keyLink, _summary, _facts, _prPanel, _claudeButton, _changesButton, _filesButton, _teamsButton, _vsCodeButton, _collapseButton });
        _header.Resize += (_, _) => LayoutHeader();
        LayoutHeader();
    }

    // Row 1: key, summary, the facts and the buttons at the right; then one PR row per repo (hidden collapsed). A narrow
    // header moves the facts to a row of their own rather than squeeze the summary, which is cut with a tooltip.
    private void LayoutHeader()
    {
        var width = _header.ClientSize.Width;
        var buttonX = width - RightPad;
        foreach (var button in new[] { _collapseButton, _vsCodeButton, _teamsButton, _filesButton, _changesButton, _claudeButton })
        {
            buttonX -= button.Width;
            button.SetBounds(buttonX, TopPad, button.Width, ButtonH);
            buttonX -= ButtonGap;
        }
        _keyLink.SetBounds(Pad, TopPad, _keyLink.PreferredWidth, ButtonH);
        var summaryX = _keyLink.Right + ItemGap;
        var available = Math.Max(0, buttonX + ButtonGap - ItemGap - summaryX);
        var factsWidth = _facts.Controls.Cast<Control>().Sum(c => c.Width + c.Margin.Horizontal);
        // A little over the measured width, so a summary that fits is never cut by the label's own padding.
        var summaryTextWidth = TextRenderer.MeasureText(_summary.Text, SummaryFont).Width + UiScale.S(4);
        var (summaryWidth, factsOnKeyRow) = KeyRow(available, summaryTextWidth, factsWidth, MinSummaryWidth, ItemGap);
        _summary.SetBounds(summaryX, TopPad, summaryWidth, ButtonH);

        var rowWidth = Math.Max(0, width - 2 * Pad);
        var nextTop = TopPad + ButtonH + RowGap;
        if (factsOnKeyRow)
        {
            var tagHeight = _facts.Controls.Count == 0 ? 0 : _facts.Controls[0].Height;
            _facts.SetBounds(_summary.Right + ItemGap, TopPad + (ButtonH - tagHeight) / 2, factsWidth + UiScale.S(2), tagHeight + UiScale.S(4));
        }
        else
        {
            _facts.SetBounds(Pad, nextTop, rowWidth, _facts.GetPreferredSize(new Size(rowWidth, 0)).Height);
            nextTop = _facts.Bottom + RowGap;
        }

        var prTop = 0;
        foreach (Control prRow in _prPanel.Controls)
        {
            var prRowHeight = prRow.GetPreferredSize(new Size(rowWidth, 0)).Height;
            prRow.SetBounds(0, prTop, rowWidth, prRowHeight);
            prTop += prRowHeight;
        }
        _prPanel.SetBounds(Pad, nextTop, rowWidth, prTop);

        _facts.Visible = factsOnKeyRow || !_headerCollapsed;
        _prPanel.Visible = !_headerCollapsed;
        var height = _headerCollapsed ? TopPad + ButtonH + TopPad : _prPanel.Bottom + TopPad;
        if (_header.Height != height) _header.Height = height;
    }

    // Row 1's summary width, and whether the facts fit after it: the summary gives way first, down to minSummaryWidth.
    internal static (int SummaryWidth, bool FactsOnKeyRow) KeyRow(int available, int summaryTextWidth, int factsWidth, int minSummaryWidth, int gap)
    {
        var room = available - factsWidth - gap;
        if (factsWidth == 0 || room >= Math.Min(summaryTextWidth, minSummaryWidth))
            return (Math.Max(0, Math.Min(summaryTextWidth, factsWidth == 0 ? available : room)), true);
        return (Math.Max(0, Math.Min(summaryTextWidth, available)), false);
    }

    // The header's facts: Status, Due, ETA, P and Logged, each with its column title and its table cell.
    internal static IReadOnlyList<(string Title, TableCell Cell)> FactCells(JiraTicket ticket, TicketSection section)
    {
        var facts = new List<(string Title, TableCell Cell)>();
        foreach (var column in new[] { TaskTableColumn.Status, TaskTableColumn.Due, TaskTableColumn.Eta, TaskTableColumn.Priority, TaskTableColumn.Logged })
        {
            var (text, color) = TaskRow.Cell(ticket, section, column);
            var isDoneInJira = column == TaskTableColumn.Status && section == TicketSection.InProgress && ticket.IsStatusDone;
            facts.Add((TaskTableHeader.Title(column),
                new TableCell(column, text, color, isDoneInJira ? Lang.T("Done in Jira - move to Done", "Jira đã Done - chuyển sang Xong") : null)));
        }
        return facts;
    }

    private static Label FactLabel(string text, Font font, Color color, int rightMargin)
    {
        var label = new Label
        {
            Text = text,
            Font = font,
            ForeColor = color,
            BackColor = Color.Transparent,
            UseMnemonic = false,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, rightMargin, 0),
        };
        label.Size = new Size(label.PreferredWidth, FactHeight);
        return label;
    }

    // One PR row per repo, in the PRs' own order (BE first, see DashboardDataBuilder.LatestPrs).
    internal static IReadOnlyList<(string RepoName, IReadOnlyList<TicketPr> Prs)> PrGroups(IReadOnlyList<TicketPr> prs) =>
        prs.GroupBy(tp => tp.RepoName).Select(g => (g.Key, (IReadOnlyList<TicketPr>)g.ToList())).ToList();

    // A PR tag beside its repo's label: branch, status mark, number and comments.
    internal static string PrTagText(TicketPr pr, bool vietnamese)
    {
        var comments = pr.CommentCount == 0
            ? ""
            : vietnamese ? $" · {pr.CommentCount} bình luận" : $" · {pr.CommentCount} comment{(pr.CommentCount == 1 ? "" : "s")}";
        return $"{pr.Branch} {DashboardDataBuilder.StatusGlyph(pr.Status)} #{pr.PullRequestId}{comments}";
    }

    private void RenderInfo()
    {
        _summary.Text = _found == null ? Lang.T("(not on the board)", "(không có trên board)") : _found.Value.Ticket.Summary;
        _summary.ForeColor = _found == null ? Theme.TextSecondary : Theme.TextPrimary;

        // Each fact as its title then its value, colored like the board's cells; one height so the pairs line up.
        var facts = new List<Control>();
        if (_found != null)
        {
            foreach (var (title, cell) in FactCells(_found.Value.Ticket, _found.Value.Section))
            {
                facts.Add(FactLabel(title, TaskTableHeader.TitleFont, Theme.TextSecondary, UiScale.S(4)));
                var valueLabel = FactLabel(cell.Text, FactValueFont, cell.Color, UiScale.S(14));
                if (cell.ToolTip != null) FactTip.SetUntilDisposed(valueLabel, cell.ToolTip);
                facts.Add(valueLabel);
            }
        }
        ReplaceItems(_facts, facts);
        BuildPrRows();
        LayoutHeader();
    }

    // "BE", "FE External", ... in a label column wide enough for the longest, so every row's tags start at one x.
    private void BuildPrRows()
    {
        var oldRows = _prPanel.Controls.Cast<Control>().ToList();
        _prPanel.Controls.Clear();
        foreach (var oldRow in oldRows) oldRow.Dispose();

        var groups = PrGroups(_prs);
        if (groups.Count == 0)
        {
            var emptyRow = NewRow();
            emptyRow.Controls.Add(TaskRow.Fact(Lang.T("No PR yet", "Chưa có PR"), TaskRow.Gray));
            _prPanel.Controls.Add(emptyRow);
            return;
        }
        var labelWidth = groups.Max(g => TextRenderer.MeasureText(g.RepoName, PrRepoFont).Width) + UiScale.S(10);
        foreach (var (repoName, prs) in groups)
        {
            var row = NewRow();
            var tags = prs.Select(pr => new FactTag(PrTagText(pr, Lang.Vietnamese),
                // Merged green, open blue; an abandoned or unknown one gray.
                pr.Status == "completed" ? WeekCard.Green : pr.Status == "active" ? Theme.Accent : TaskRow.Gray,
                PrFont, onClick: () => TaskRow.OpenUrl(pr.Url))).ToList();
            row.Controls.Add(new Label
            {
                Text = repoName,
                AutoSize = false,
                Size = new Size(labelWidth, tags[0].Height),
                Margin = new Padding(0, 0, 0, UiScale.S(4)),
                TextAlign = ContentAlignment.MiddleLeft,
                Font = PrRepoFont,
                ForeColor = Theme.TextSecondary,
                BackColor = Color.Transparent,
                UseMnemonic = false,
            });
            row.Controls.AddRange(tags.ToArray());
            _prPanel.Controls.Add(row);
        }
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
        if (bodyView == BodyView.Files && _files == null)
        {
            _files = new FilesView(TicketKey, _worktrees, _docsPath) { Dock = DockStyle.Fill };
            _files.PageRequested += OnViewPageRequested;
            _body.Controls.Add(_files);
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
        if (_files != null) _files.Visible = bodyView == BodyView.Files;
        if (_teamsPost != null) _teamsPost.Visible = bodyView == BodyView.Teams;
        UpdateViewButtons();
        Activate();
    }

    // Ctrl+click on a path in this tab's terminal: the Files view for a path in the ticket's docs or worktrees, else VS Code.
    private void OnFileRequested(string fullPath, int? line)
    {
        var located = FilesView.Locate(fullPath, FilesView.RootsFor(TicketKey, _docsPath(), _worktrees()));
        if (located == null)
        {
            TicketLauncher.TryOpenVsCode(fullPath, line);
            return;
        }
        // The listing that Activate starts answers the reveal; it continues only after this handler returns.
        ShowBody(BodyView.Files);
        _files?.Reveal(located.Value.Group, located.Value.Path, fullPath, line);
    }

    private void UpdateViewButtons()
    {
        _claudeButton.Selected = _bodyView == BodyView.Claude;
        _changesButton.Selected = _bodyView == BodyView.Changes;
        _filesButton.Selected = _bodyView == BodyView.Files;
        _teamsButton.Selected = _bodyView == BodyView.Teams;
    }

    private void UpdateCollapseButton()
    {
        _collapseButton.Glyph = _headerCollapsed ? Icons.ChevronDown : Icons.ChevronUp;
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
        _filesButton.Text = Lang.T("Files", "File");
        _filesButton.AccessibleName = _filesButton.Text;
        _teamsButton.AccessibleName = Lang.T("Post to Teams", "Đăng Teams");
        UpdateCollapseButton();
        RenderInfo();
    }
}
