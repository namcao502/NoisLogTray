using System.Drawing;

namespace NoisLogTray;

// The Tasks board: assigned Jira tickets in New / In progress / Done / Dismissed sections,
// with search, a manual Add, and a "Check now" that asks the host to poll Jira.
internal sealed class TasksPage : UserControl, IShellPage
{
    private static readonly int W = UiScale.S(640);
    private static readonly int H = UiScale.S(640);
    private static readonly int Inner = UiScale.S(600); // card width
    private static readonly int ButtonH = UiScale.S(28);
    private static readonly int ListInset = UiScale.S(8); // lists sit this far inside the card

    private const string NewId = "new";
    private const string InProgressId = "inprogress";
    private const string DoneId = "done";
    private const string DismissedId = "dismissed";

    private static readonly Color Green = Color.FromArgb(46, 160, 80);
    private static readonly Color Red = Color.FromArgb(230, 76, 76);

    private static readonly Font InputFont = UiScale.Font("Segoe UI", 9F);
    private static readonly Font LabelFont = UiScale.Font("Segoe UI", 8.5F);
    private static readonly Font SectionFont = UiScale.Font("Segoe UI", 8.5F, FontStyle.Bold);

    // Raised by "Check now"; the host polls Jira and reports back through SetChecking.
    internal event Func<Task>? CheckRequested;
    // Raised by a row's Start / Open / Ready to Test; the host reports progress through SetTicketBusy.
    internal event Func<string, Task>? StartRequested;
    internal event Func<string, Task>? OpenRequested;
    internal event Func<string, Task>? ReadyToTestRequested;
    // Raised at the end of every Reload with AttentionCount, for the sidebar badge.
    internal event Action<int>? AttentionChanged;

    private readonly TicketStore _ticketStore;
    private readonly TicketBoard _ticketBoard;
    private readonly Func<LoggingService?> _getService;
    private readonly HashSet<string> _collapsed = new() { DismissedId };
    // Sections the user toggled during the current search; their choice beats auto-expand.
    private readonly HashSet<string> _toggledDuringSearch = new();
    private readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 200 };
    private readonly Dictionary<string, StartStage> _busyTickets = new();
    private List<SectionView> _sections = new();
    private HashSet<string> _devPrMergedKeys = new();
    private Dictionary<string, string> _prSummaries = new(); // ticket key -> "BE main ✓ · dev ● | FE dev ✓"
    private DateTime? _checkedAt;
    private Size _renderedCardSize;
    private bool _renderPending = true; // rows not rendered since the last data/size change while hidden
    private bool _renderPosted;
    private bool _boardMode;
    private Color? _addResultColor; // null = theme secondary text
    private string? _focusSectionId;
    private string? _focusTicketKey;

    private readonly Card _top = new();
    private readonly TextBox _search = new();
    private readonly TextBox _addBox = new();
    private readonly MacButton _addBtn = MacButton.Primary(string.Empty);
    private readonly MacButton _checkBtn = MacButton.Secondary(string.Empty);
    private readonly Label _addResult = new();
    private readonly Label _checkedLabel = new();
    private readonly Card _card = new();
    private readonly FlowLayoutPanel _body = new();
    private readonly FlowLayoutPanel _newColumn = new();
    private readonly FlowLayoutPanel _inProgressColumn = new();
    private readonly FlowLayoutPanel _doneColumn = new();
    private readonly Panel _newDivider = new() { Visible = false };
    private readonly Panel _inProgressDivider = new() { Visible = false };
    // Holds the three board columns; scrolls sideways when they don't fit the card.
    private readonly Panel _boardHost = new() { AutoScroll = true, Visible = false };

    private sealed record SectionView(string Id, string Title, bool IsDismissed,
        List<(JiraTicket Ticket, TicketSection Origin)> Tickets);

    internal TasksPage(TicketStore store, TicketBoard board, Func<LoggingService?> getService)
    {
        _ticketStore = store;
        _ticketBoard = board;
        _getService = getService;
        BuildLayout();
        Theme.Changed += OnThemeChanged;
        Lang.Changed += OnLangChanged;
        ReflowRowsOnResize();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnThemeChanged;
            Lang.Changed -= OnLangChanged;
            _searchTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    private void BuildLayout()
    {
        BackColor = Theme.WindowBg;

        _top.Size = new Size(Inner, UiScale.S(64));
        _top.Location = UiScale.S(new Point(20, 12));

        var searchHost = InputHost(_search, UiScale.S(new Point(12, 10)), UiScale.S(220));
        // Debounced: each render allocates fresh rows and buttons.
        _search.TextChanged += (_, _) =>
        {
            _toggledDuringSearch.Clear();
            _searchTimer.Stop();
            _searchTimer.Start();
        };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            Render();
        };

        var addHost = InputHost(_addBox, UiScale.S(new Point(240, 10)), UiScale.S(120));
        _addBox.PlaceholderText = "MDP-1234";
        _addBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            AddAsync();
        };

        _addBtn.Click += (_, _) => AddAsync();
        _checkBtn.Click += (_, _) => CheckAsync();

        _addResult.AutoSize = false;
        _addResult.AutoEllipsis = true;
        _addResult.Location = UiScale.S(new Point(240, 40));
        _addResult.Size = UiScale.S(new Size(186, 18));
        _addResult.Font = LabelFont;
        _addResult.BackColor = Color.Transparent;
        _addResult.UseMnemonic = false;

        _checkedLabel.AutoSize = false;
        _checkedLabel.Location = UiScale.S(new Point(430, 40));
        _checkedLabel.Size = new Size(Inner - UiScale.S(12) - UiScale.S(430), UiScale.S(18));
        _checkedLabel.TextAlign = ContentAlignment.MiddleRight;
        _checkedLabel.Font = LabelFont;
        _checkedLabel.BackColor = Color.Transparent;

        _top.Controls.Add(searchHost);
        _top.Controls.Add(addHost);
        _top.Controls.Add(_addBtn);
        _top.Controls.Add(_checkBtn);
        _top.Controls.Add(_addResult);
        _top.Controls.Add(_checkedLabel);

        _card.Location = UiScale.S(new Point(20, 84));
        _card.Size = new Size(Inner, H - UiScale.S(84) - UiScale.S(12));

        // No anchors on the lists: Render places them for the current card size and mode.
        foreach (var list in new[] { _body, _newColumn, _inProgressColumn, _doneColumn })
        {
            list.FlowDirection = FlowDirection.TopDown;
            list.WrapContents = false;
            list.AutoScroll = true;
            list.Padding = new Padding(0);
            list.Visible = list == _body;
        }
        _card.Controls.Add(_body);
        foreach (var boardControl in new Control[] { _newColumn, _inProgressColumn, _doneColumn, _newDivider, _inProgressDivider })
        {
            boardControl.Visible = true;
            _boardHost.Controls.Add(boardControl);
        }
        _card.Controls.Add(_boardHost);

        Controls.Add(_top);
        Controls.Add(_card);

        ApplyTexts();
        ApplyColors();

        // Sized before anchoring: WinForms records each anchor's edge distances when Anchor is set.
        Size = new Size(W, H);
        MinimumSize = Size;
        _top.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _card.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _checkBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _checkedLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
    }

    // Rows and lists are sized from the card, so any card resize rebuilds them, once per burst.
    // Posted, like LogPage: inside the layout pass a list keeps the old scroll extent after a shrink.
    private void ReflowRowsOnResize()
    {
        _card.Resize += (_, _) =>
        {
            if (_renderPosted || _card.ClientSize == _renderedCardSize || !IsHandleCreated) return;
            if (!Visible)
            {
                _renderPending = true;
                return;
            }
            _renderPosted = true;
            BeginInvoke(new Action(() =>
            {
                _renderPosted = false;
                if (!IsDisposed && _card.ClientSize != _renderedCardSize) Render(); // a Reload may have rendered meanwhile
            }));
        };
    }

    private static RoundedHost InputHost(TextBox box, Point location, int width)
    {
        var host = new RoundedHost { Location = location, Size = new Size(width, ButtonH) };
        box.BorderStyle = BorderStyle.None;
        box.Font = InputFont;
        var boxH = box.PreferredHeight;
        box.SetBounds(UiScale.S(8), (host.Height - boxH) / 2, host.Width - UiScale.S(16), boxH);
        host.Controls.Add(box);
        return host;
    }

    private void ApplyTexts()
    {
        _search.PlaceholderText = Lang.T("Search", "Tìm kiếm");
        _addBtn.Text = Lang.T("Add", "Thêm");
        _checkBtn.Text = Lang.T("Check now", "Kiểm tra");
        var buttonY = UiScale.S(10);
        _addBtn.Bounds = new Rectangle(UiScale.S(366), buttonY, ButtonWidth(_addBtn), ButtonH);
        var checkWidth = ButtonWidth(_checkBtn);
        _checkBtn.Bounds = new Rectangle(_top.ClientSize.Width - UiScale.S(12) - checkWidth, buttonY, checkWidth, ButtonH);
        _checkedLabel.Text = _checkedAt.HasValue
            ? Lang.T($"Checked at {_checkedAt.Value:HH:mm:ss}", $"Kiểm tra lúc {_checkedAt.Value:HH:mm:ss}")
            : string.Empty;
    }

    private static int ButtonWidth(MacButton button)
        => ButtonWidths.ToolbarWidth(button.Font);

    private void ApplyColors()
    {
        BackColor = Theme.WindowBg;
        foreach (var list in new Control[] { _body, _newColumn, _inProgressColumn, _doneColumn, _boardHost })
            list.BackColor = Theme.CardSurface;
        _newDivider.BackColor = Theme.Divider;
        _inProgressDivider.BackColor = Theme.Divider;
        foreach (var box in new[] { _search, _addBox })
        {
            box.BackColor = Theme.InputBg;
            box.ForeColor = Theme.TextPrimary;
        }
        _checkedLabel.ForeColor = Theme.TextSecondary;
        _addResult.ForeColor = _addResultColor ?? Theme.TextSecondary;
    }

    private void OnThemeChanged()
    {
        ApplyColors();
        Render(); // colors only; the store did not change
        Invalidate(true);
    }

    private void OnLangChanged()
    {
        ApplyTexts();
        _addResult.Text = string.Empty; // the last result was worded in the old language
        Reload();
    }

    internal void SetChecking(bool checking, DateTime? checkedAt)
    {
        if (IsDisposed) return;
        _checkBtn.Enabled = !checking;
        if (checkedAt.HasValue) _checkedAt = checkedAt;
        ApplyTexts();
    }

    // UI thread only; TrayApp owns the busy state and re-seeds a rebuilt page.
    internal void SetTicketBusy(string ticketKey, StartStage? stage)
    {
        if (IsDisposed) return;
        if (stage.HasValue) _busyTickets[ticketKey] = stage.Value;
        else _busyTickets.Remove(ticketKey);
        Render();
    }

    internal static string StageText(StartStage stage) => stage switch
    {
        StartStage.Fetching => Lang.T("Fetching origin...", "Đang fetch origin..."),
        StartStage.CreatingWorktree => Lang.T("Creating worktree...", "Đang tạo worktree..."),
        StartStage.OpeningVsCode => Lang.T("Opening VS Code...", "Đang mở VS Code..."),
        StartStage.UpdatingJira => Lang.T("Updating Jira...", "Đang cập nhật Jira..."),
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null),
    };

    // Re-reads the store, then re-renders; the query, collapsed sections and scroll are kept.
    internal void Reload()
    {
        if (IsDisposed) return;
        _sections = LoadSections();
        var prTracking = _ticketStore.LoadPrTracking();
        var ticketKeys = _sections.SelectMany(sv => sv.Tickets).Select(jt => jt.Ticket.Key).Distinct().ToList();
        _devPrMergedKeys = ticketKeys.Where(key => PrTracker.IsDevPrMerged(prTracking, key)).ToHashSet();
        _prSummaries = ticketKeys.ToDictionary(key => key, key => DashboardDataBuilder.PrSummary(key, prTracking));
        // The badge needs the data now; the rows can wait until the page is shown.
        if (Visible) Render();
        else _renderPending = true;
        AttentionChanged?.Invoke(AttentionCount(SectionTickets(InProgressId), SectionTickets(DoneId), _devPrMergedKeys));
    }

    private IEnumerable<JiraTicket> SectionTickets(string sectionId) =>
        _sections.First(sv => sv.Id == sectionId).Tickets.Select(jt => jt.Ticket);

    // In-progress tickets the user should act on: the Ready to Test button, or Jira already Done.
    internal static int AttentionCount(IEnumerable<JiraTicket> inProgress, IEnumerable<JiraTicket> done, ISet<string> devPrMergedKeys) =>
        inProgress.Count(jt => jt.IsStatusDone
            || TaskRow.ReadyToTestLabel(jt, TicketSection.InProgress, devPrMergedKeys.Contains(jt.Key)) != null)
        + done.Count(jt => TaskRow.ReadyToTestLabel(jt, TicketSection.Done, devPrMergedKeys.Contains(jt.Key)) != null);

    // Polling has its own timer, so only a page switch or the window being shown reloads.
    public void OnPageShown()
    {
        if (_renderPending) Render();
    }

    public void OnActivatedRefresh()
    {
    }

    public void OnHidden()
    {
    }

    // Rebuilds the rows from the cached sections (no store read), so search stays cheap.
    private void Render()
    {
        _renderPending = false;
        // Mode and bounds first: each list's ClientSize below must reflect this render's layout.
        var listWidth = _card.ClientSize.Width - 2 * ListInset;
        var listHeight = _card.ClientSize.Height - 2 * ListInset;
        var board = ShellLayout.UseTaskBoard(UiScale.Logical(listWidth));
        var isSameMode = board == _boardMode;
        _boardMode = board;
        _renderedCardSize = _card.ClientSize;

        var query = _search.Text.Trim();
        // Filtered first: an empty column (after search) shrinks, so the layout needs the counts.
        var shownBySection = _sections.ToDictionary(sv => sv.Id, sv => query.Length == 0
            ? sv.Tickets
            : sv.Tickets.Where(jt => TicketSearch.Matches(jt.Ticket, query)).ToList());

        _body.Visible = !board;
        _boardHost.Visible = board;
        var boardScroll = _boardHost.AutoScrollPosition;
        if (board)
        {
            _boardHost.Bounds = new Rectangle(ListInset, ListInset, listWidth, listHeight);
            // A collapsed Dismissed header alone still counts as empty: there is nothing to read in the column.
            var dismissedShown = shownBySection[DismissedId].Count != 0
                && (query.Length != 0 && !_toggledDuringSearch.Contains(DismissedId) || !_collapsed.Contains(DismissedId));
            var doneEmpty = shownBySection[DoneId].Count == 0 && !dismissedShown;
            var (newLogical, inProgressLogical, doneLogical) = ShellLayout.TaskBoardColumnBounds(
                UiScale.Logical(listWidth), UiScale.Logical(listHeight),
                shownBySection[NewId].Count == 0, shownBySection[InProgressId].Count == 0, doneEmpty);
            var boardWidth = UiScale.S(doneLogical.Right);
            // A sideways scrollbar takes height from the columns, so they don't also scroll the host vertically.
            var columnHeight = boardWidth > listWidth ? listHeight - SystemInformation.HorizontalScrollBarHeight : listHeight;
            var newBounds = new Rectangle(UiScale.S(newLogical.X), 0, UiScale.S(newLogical.Width), columnHeight);
            var inProgressBounds = new Rectangle(UiScale.S(inProgressLogical.X), 0, UiScale.S(inProgressLogical.Width), columnHeight);
            var doneX = UiScale.S(doneLogical.X);
            // Fitting boards end exactly at the card edge; rounding never leaves a gap.
            var doneBounds = new Rectangle(doneX, 0, Math.Max(boardWidth, listWidth) - doneX, columnHeight);
            _boardHost.AutoScrollPosition = Point.Empty;
            _newColumn.Bounds = newBounds;
            _inProgressColumn.Bounds = inProgressBounds;
            _doneColumn.Bounds = doneBounds;
            var halfGap = UiScale.S(ShellLayout.TaskColumnGap) / 2;
            _newDivider.Bounds = new Rectangle(newBounds.Right + halfGap, 0, 1, columnHeight);
            _inProgressDivider.Bounds = new Rectangle(inProgressBounds.Right + halfGap, 0, 1, columnHeight);
        }
        else
            _body.Bounds = new Rectangle(ListInset, ListInset, listWidth, listHeight);

        var allLists = new[] { _body, _newColumn, _inProgressColumn, _doneColumn };
        var activeLists = board ? new[] { _newColumn, _inProgressColumn, _doneColumn } : new[] { _body };
        var scrollPositions = activeLists.ToDictionary(flp => flp, flp => flp.AutoScrollPosition);
        var rowWidths = activeLists.ToDictionary(flp => flp,
            flp => flp.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - UiScale.S(2));
        var jiraBaseUrl = _getService()?.JiraBaseUrl ?? AppConfig.DefaultJiraBaseUrl;
        Control? focusHeader = null;
        TaskRow? focusRow = null;

        foreach (var list in allLists)
        {
            list.SuspendLayout();
            ClearRows(list);
        }
        foreach (var section in _sections)
        {
            // Board mode: Dismissed shares the Done column, after Done.
            var container = !board ? _body : section.Id switch
            {
                NewId => _newColumn,
                InProgressId => _inProgressColumn,
                _ => _doneColumn,
            };
            var rowWidth = rowWidths[container];
            var shown = shownBySection[section.Id];
            var isAutoExpanded = query.Length != 0 && shown.Count != 0 && !_toggledDuringSearch.Contains(section.Id);
            var isExpanded = isAutoExpanded || !_collapsed.Contains(section.Id);
            var header = SectionHeader(section, shown.Count, isExpanded, rowWidth);
            container.Controls.Add(header);
            if (section.Id == _focusSectionId) focusHeader = header;
            if (!isExpanded) continue;

            foreach (var (ticket, origin) in shown)
            {
                // Stage text is resolved per render so a language switch re-words it.
                var busyText = _busyTickets.TryGetValue(ticket.Key, out var stage) ? StageText(stage) : null;
                var row = new TaskRow(ticket, origin, section.IsDismissed, jiraBaseUrl, _ticketBoard,
                    OnTicketMoved, tk => RaiseTicketRequest(StartRequested, tk, "Start"),
                    tk => RaiseTicketRequest(OpenRequested, tk, "Open"), _devPrMergedKeys.Contains(ticket.Key),
                    tk => RaiseTicketRequest(ReadyToTestRequested, tk, "Ready to Test"), busyText, rowWidth,
                    _prSummaries.GetValueOrDefault(ticket.Key, ""));
                container.Controls.Add(row);
                if (ticket.Key == _focusTicketKey) focusRow = row;
            }
        }
        foreach (var list in allLists) list.ResumeLayout();
        if (isSameMode)
        {
            foreach (var (list, scroll) in scrollPositions)
                list.AutoScrollPosition = new Point(-scroll.X, -scroll.Y);
            if (board) _boardHost.AutoScrollPosition = new Point(-boardScroll.X, -boardScroll.Y);
        }

        _focusSectionId = null;
        _focusTicketKey = null;
        focusHeader?.Focus();
        focusRow?.FocusKey();
    }

    // Deferred: the clicked button belongs to a row that Reload disposes.
    private void OnTicketMoved(string ticketKey, bool fromKeyboard)
    {
        if (fromKeyboard) _focusTicketKey = ticketKey;
        BeginInvoke(new Action(Reload));
    }

    // Deferred: the host calls SetTicketBusy, whose Render disposes the clicked button.
    private void RaiseTicketRequest(Func<string, Task>? request, string ticketKey, string actionName)
    {
        if (request == null || _busyTickets.ContainsKey(ticketKey)) return;
        BeginInvoke(new Action(async () =>
        {
            try
            {
                await request.Invoke(ticketKey);
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[tasks] {actionName} {ticketKey} failed: {ex.Message}");
            }
        }));
    }

    private List<SectionView> LoadSections()
    {
        return new List<SectionView>
        {
            new(NewId, Lang.T("NEW", "MỚI"), false,
                SortByDue(_ticketStore.LoadPending().Values.Select(jt => (jt, TicketSection.New)))),
            new(InProgressId, Lang.T("IN PROGRESS", "ĐANG LÀM"), false,
                SortByDue(_ticketStore.LoadStarted().Values.Select(jt => (jt, TicketSection.InProgress)))),
            new(DoneId, Lang.T("DONE", "XONG"), false,
                SortByDue(_ticketStore.LoadDone().Values.Select(jt => (jt, TicketSection.Done)))),
            new(DismissedId, Lang.T("DISMISSED", "ĐÃ ẨN"), true,
                SortByDue(_ticketStore.LoadDismissed().Values.Select(dt => (dt.Ticket, dt.Origin)))),
        };
    }

    private static List<(JiraTicket Ticket, TicketSection Origin)> SortByDue(
        IEnumerable<(JiraTicket Ticket, TicketSection Origin)> tickets)
        => TicketSort.ByDue(tickets, x => x.Ticket);

    private Control SectionHeader(SectionView section, int count, bool isExpanded, int width)
    {
        var text = $"{(isExpanded ? "▾" : "▸")}  {section.Title} ({count})";
        var headerHeight = SectionFont.Height + UiScale.S(14);
        var header = new ClickableRow
        {
            Size = new Size(width, headerHeight),
            Margin = new Padding(0),
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand,
            AccessibleName = text,
        };
        header.Paint += (_, e) => TextRenderer.DrawText(e.Graphics, text, SectionFont,
            new Rectangle(UiScale.S(6), 0, width - UiScale.S(12), headerHeight), Theme.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        header.Click += (_, _) =>
        {
            if (isExpanded) _collapsed.Add(section.Id);
            else _collapsed.Remove(section.Id);
            if (_search.Text.Trim().Length != 0) _toggledDuringSearch.Add(section.Id);
            // A mouse click always lands inside the header; a keyboard press usually does not.
            if (!header.ClientRectangle.Contains(header.PointToClient(Cursor.Position))) _focusSectionId = section.Id;
            // Deferred: Render disposes this header while its Click is still on the stack.
            BeginInvoke(new Action(Render));
        };
        return header;
    }

    private static void ClearRows(Control container)
    {
        var stale = container.Controls.Cast<Control>().ToArray();
        container.Controls.Clear();
        foreach (var staleControl in stale) staleControl.Dispose();
    }

    private async void AddAsync()
    {
        var service = _getService();
        if (service == null)
        {
            ShowAddResult(Lang.T("Config not loaded", "Chưa có cấu hình"), Red);
            return;
        }
        if (!TicketParser.TryParseKey(_addBox.Text, out var key))
        {
            ShowAddResult(Lang.T("Invalid ticket key", "Mã ticket không hợp lệ"), Red);
            return;
        }

        _addBtn.Enabled = false;
        try
        {
            var ticket = await service.Jira.GetTicketAsync(key);
            if (IsDisposed) return;

            if (ticket == null)
                ShowAddResult(Lang.T("Ticket not found", "Không tìm thấy ticket"), Red);
            else if (_ticketBoard.Add(ticket))
            {
                ShowAddResult(Lang.T("Added", "Đã thêm"), Green);
                _addBox.Text = string.Empty;
                Reload();
            }
            else
                ShowAddResult(Lang.T("Already on the board", "Đã có trong danh sách"), null);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[tasks] Add {key} failed: {ex.Message}");
            if (IsDisposed) return;
            ShowAddResult(Lang.T($"Failed: {ex.Message}", $"Lỗi: {ex.Message}"), Red);
        }
        finally
        {
            if (!IsDisposed) _addBtn.Enabled = true;
        }
    }

    private void ShowAddResult(string text, Color? color)
    {
        _addResultColor = color;
        _addResult.Text = text;
        _addResult.ForeColor = color ?? Theme.TextSecondary;
    }

    private async void CheckAsync()
    {
        if (CheckRequested == null) return;
        try
        {
            await CheckRequested.Invoke();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[tasks] Check now failed: {ex.Message}");
        }
    }
}
