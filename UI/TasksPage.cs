using System.Drawing;

namespace NoisLogTray;

// The Tasks board, the left side of the Work page: assigned Jira tickets in New / In progress / Done / Dismissed
// sections and "+ Log" for New entry. Its search, Add by key and "Check now" (the host polls Jira) sit in the shell
// header (HeaderControl), leaving the page to the list.
internal sealed class TasksPage : UserControl, IShellPage
{
    // Logical px, since ShellLayout's Work page rules are built from them; scale before use.
    internal const int DesignWidth = 640;
    internal const int DesignHeight = 640;

    private static readonly int W = UiScale.S(DesignWidth);
    private static readonly int H = UiScale.S(DesignHeight);
    private static readonly int Inner = UiScale.S(600); // card width
    private static readonly int ButtonH = UiScale.S(28);
    private static readonly int ListInset = UiScale.S(8); // lists sit this far inside the card
    private static readonly int SearchMaxWidth = UiScale.S(280);
    private static readonly int SearchMinWidth = UiScale.S(140);
    private static readonly int AddBoxWidth = UiScale.S(120);
    private static readonly int ToolbarGap = UiScale.S(8);

    private const string NewId = "new";
    private const string InProgressId = "inprogress";
    private const string DoneId = "done";
    private const string DismissedId = "dismissed";

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
    // Raised after this page moved or added a ticket, so the host refreshes the other ticket views (Dashboard, Ticket page).
    internal event Action? BoardChanged;
    // Add by key's result, as a notice: the header has no room for a result line.
    internal event Action<string, NoticeKind>? StatusRaised;
    // Raised by a row's "+ Log" with the ticket's key and summary; the Work page puts it into New entry.
    internal event Action<string, string>? LogRequested;

    private readonly TicketStore _ticketStore;
    private readonly TicketBoard _ticketBoard;
    private readonly Func<LoggingService?> _getService;
    private readonly HashSet<string> _collapsed = new() { DismissedId };
    // Sections the user toggled during the current search; their choice beats auto-expand.
    private readonly HashSet<string> _toggledDuringSearch = new();
    // Debounces renders asked for while typing: the search box, and the tickets typed in New entry.
    private readonly System.Windows.Forms.Timer _renderTimer = new() { Interval = 200 };
    private readonly Dictionary<string, StartStage> _busyTickets = new();
    private HashSet<string> _entryKeys = new(); // tickets in New entry: their rows read "In entry"
    private List<SectionView> _sections = new();
    private HashSet<string> _devPrMergedKeys = new();
    private Dictionary<string, string> _prSummaries = new(); // ticket key -> "BE main ✓ · dev ● | FE Portal dev ✓"
    private DateTime? _checkedAt;
    private Size _renderedCardSize;
    private bool _renderPending = true; // rows not rendered since the last data/size change while hidden
    private bool _renderPosted;
    private string? _focusSectionId;
    private string? _focusTicketKey;

    private readonly HeaderToolbar _toolbar = new();
    private readonly RoundedHost _searchHost = new() { OnWindow = true };
    private readonly TextBox _search = new();
    private readonly RoundedHost _addHost = new() { OnWindow = true };
    private readonly TextBox _addBox = new();
    private readonly MacButton _addBtn = MacButton.Primary(string.Empty);
    private readonly MacButton _checkBtn = MacButton.Secondary(string.Empty);
    private readonly Label _checkedLabel = new();
    private readonly Card _card = new();
    private readonly FlowLayoutPanel _body = new();

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
            _renderTimer.Dispose();
            _toolbar.Dispose(); // it lives in the shell header, not among this page's controls
        }
        base.Dispose(disposing);
    }

    private void BuildLayout()
    {
        BackColor = Theme.WindowBg;

        SetUpInput(_searchHost, _search, SearchMaxWidth);
        // Debounced: each render allocates fresh rows and buttons.
        _search.TextChanged += (_, _) =>
        {
            _toggledDuringSearch.Clear();
            _renderTimer.Stop();
            _renderTimer.Start();
        };
        _renderTimer.Tick += (_, _) =>
        {
            _renderTimer.Stop();
            Render();
        };

        SetUpInput(_addHost, _addBox, AddBoxWidth);
        _addBox.PlaceholderText = "MDP-1234";
        _addBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            AddAsync();
        };

        _addBtn.OnWindow = true;
        _addBtn.Click += (_, _) => AddAsync();
        _checkBtn.OnWindow = true;
        _checkBtn.Click += (_, _) => CheckAsync();

        _checkedLabel.AutoSize = false;
        _checkedLabel.TextAlign = ContentAlignment.MiddleLeft;
        _checkedLabel.Font = LabelFont;
        _checkedLabel.BackColor = Color.Transparent;

        _toolbar.Controls.AddRange(new Control[] { _searchHost, _addHost, _addBtn, _checkBtn, _checkedLabel });
        _toolbar.Resize += (_, _) => LayoutToolbar();

        // The 20px bottom margin lines the card up with the log column's week card beside it.
        _card.Location = UiScale.S(new Point(20, 14));
        _card.Size = new Size(Inner, H - UiScale.S(14) - UiScale.S(20));

        // No anchor on the list: Render places it for the current card size.
        _body.FlowDirection = FlowDirection.TopDown;
        _body.WrapContents = false;
        _body.AutoScroll = true;
        _body.Padding = new Padding(0);
        _card.Controls.Add(_body);
        ThemedScrollBar.Attach(_body);

        Controls.Add(_card);

        ApplyTexts();
        ApplyColors();

        // Sized before anchoring: WinForms records each anchor's edge distances when Anchor is set.
        Size = new Size(W, H);
        MinimumSize = Size;
        _card.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
    }

    internal Control HeaderControl => _toolbar;

    // Left to right: search, Add by key, Add, Check now, the last check's time. A narrow header drops the time, then
    // Add by key (Check now stays), and the search box takes what is left.
    private void LayoutToolbar()
    {
        var y = (_toolbar.ClientSize.Height - ButtonH) / 2;
        var addWidth = _addHost.Width + ToolbarGap + _addBtn.Width;
        var labelWidth = _checkedLabel.Text.Length == 0 ? 0 : TextRenderer.MeasureText(_checkedLabel.Text, LabelFont).Width;
        // A gap before the header's own buttons at the right.
        var room = _toolbar.ClientSize.Width - ToolbarGap - _checkBtn.Width - ToolbarGap;
        var showAdd = room - addWidth - ToolbarGap >= SearchMinWidth;
        if (showAdd) room -= addWidth + ToolbarGap;
        var showLabel = labelWidth != 0 && room - labelWidth - ToolbarGap >= SearchMinWidth;
        if (showLabel) room -= labelWidth + ToolbarGap;

        _searchHost.SetBounds(0, y, Math.Clamp(room, UiScale.S(60), SearchMaxWidth), ButtonH);
        var nextLeft = _searchHost.Right + ToolbarGap;
        _addHost.Visible = showAdd;
        _addBtn.Visible = showAdd;
        if (showAdd)
        {
            _addHost.Location = new Point(nextLeft, y);
            _addBtn.Location = new Point(_addHost.Right + ToolbarGap, y);
            nextLeft = _addBtn.Right + ToolbarGap;
        }
        _checkBtn.Location = new Point(nextLeft, y);
        _checkedLabel.Visible = showLabel;
        _checkedLabel.Bounds = new Rectangle(_checkBtn.Right + ToolbarGap, y, labelWidth, ButtonH);
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

    private static void SetUpInput(RoundedHost host, TextBox box, int width)
    {
        host.Size = new Size(width, ButtonH);
        box.BorderStyle = BorderStyle.None;
        box.Font = InputFont;
        var boxH = box.PreferredHeight;
        box.SetBounds(UiScale.S(8), (host.Height - boxH) / 2, host.Width - UiScale.S(16), boxH);
        // Sized before anchoring, so the search box follows its host as the header gives it more or less room.
        box.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        host.Controls.Add(box);
    }

    private void ApplyTexts()
    {
        _search.PlaceholderText = Lang.T("Search tickets", "Tìm ticket");
        _search.AccessibleName = Lang.T("Search tickets", "Tìm ticket");
        _addBox.AccessibleName = Lang.T("Add a ticket by key or Jira link", "Thêm ticket bằng mã hoặc link Jira");
        _addBtn.Text = Lang.T("Add", "Thêm");
        _checkBtn.Text = Lang.T("Check now", "Kiểm tra");
        _addBtn.Size = new Size(FitWidth(_addBtn, "Add", "Thêm"), ButtonH);
        _checkBtn.Size = new Size(FitWidth(_checkBtn, "Check now", "Kiểm tra"), ButtonH);
        _checkedLabel.Text = _checkedAt.HasValue
            ? Lang.T($"Checked {_checkedAt.Value:HH:mm}", $"Kiểm tra lúc {_checkedAt.Value:HH:mm}")
            : string.Empty;
        LayoutToolbar();
    }

    // As wide as the longer of its two labels, so a language switch never moves the toolbar.
    private static int FitWidth(MacButton button, string english, string vietnamese) =>
        Math.Max(TextRenderer.MeasureText(english, button.Font).Width, TextRenderer.MeasureText(vietnamese, button.Font).Width)
        + UiScale.S(28);

    private void ApplyColors()
    {
        BackColor = Theme.WindowBg;
        _body.BackColor = Theme.CardSurface;
        foreach (var box in new[] { _search, _addBox })
        {
            box.BackColor = Theme.InputBg;
            box.ForeColor = Theme.TextPrimary;
        }
        _checkedLabel.ForeColor = Theme.TextSecondary;
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

    // The tickets typed in New entry; the rows re-render (debounced, since it follows each keystroke) only on a change.
    internal void SetEntryKeys(IReadOnlyCollection<string> ticketKeys)
    {
        if (IsDisposed || _entryKeys.SetEquals(ticketKeys)) return;
        _entryKeys = ticketKeys.ToHashSet();
        if (!Visible)
        {
            _renderPending = true;
            return;
        }
        _renderTimer.Stop();
        _renderTimer.Start();
    }

    internal static string StageText(StartStage stage) => stage switch
    {
        StartStage.Fetching => Lang.T("Fetching origin...", "Đang fetch origin..."),
        StartStage.CreatingWorktree => Lang.T("Creating worktree...", "Đang tạo worktree..."),
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
    // One list across the whole card, the sections stacked (no kanban columns).
    private void Render()
    {
        _renderPending = false;
        // Bounds first: the list's ClientSize below must reflect this render's layout.
        _body.Bounds = new Rectangle(ListInset, ListInset, _card.ClientSize.Width - 2 * ListInset, _card.ClientSize.Height - 2 * ListInset);
        _renderedCardSize = _card.ClientSize;

        var query = _search.Text.Trim();
        var scrollPosition = _body.AutoScrollPosition;
        var rowWidth = _body.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - UiScale.S(2);
        var jiraBaseUrl = _getService()?.JiraBaseUrl ?? AppConfig.DefaultJiraBaseUrl;
        Control? focusHeader = null;
        TaskRow? focusRow = null;

        _body.SuspendLayout();
        ClearRows(_body);
        foreach (var section in _sections)
        {
            var shown = query.Length == 0 ? section.Tickets : section.Tickets.Where(jt => TicketSearch.Matches(jt.Ticket, query)).ToList();
            var isAutoExpanded = query.Length != 0 && shown.Count != 0 && !_toggledDuringSearch.Contains(section.Id);
            var isExpanded = isAutoExpanded || !_collapsed.Contains(section.Id);
            var header = SectionHeader(section, shown.Count, isExpanded, rowWidth);
            _body.Controls.Add(header);
            if (section.Id == _focusSectionId) focusHeader = header;
            if (!isExpanded) continue;

            var rowIndex = 0;
            foreach (var (ticket, origin) in shown)
            {
                // Stage text is resolved per render so a language switch re-words it.
                var busyText = _busyTickets.TryGetValue(ticket.Key, out var stage) ? StageText(stage) : null;
                var row = new TaskRow(ticket, origin, section.IsDismissed, jiraBaseUrl, _ticketBoard,
                    OnTicketMoved, tk => RaiseTicketRequest(StartRequested, tk, "Start"),
                    tk => RaiseTicketRequest(OpenRequested, tk, "Open"), _devPrMergedKeys.Contains(ticket.Key),
                    tk => RaiseTicketRequest(ReadyToTestRequested, tk, "Ready to Test"), busyText, rowWidth,
                    _prSummaries.GetValueOrDefault(ticket.Key, ""), striped: rowIndex++ % 2 == 1,
                    onLog: RaiseLogRequest, isInEntry: _entryKeys.Contains(ticket.Key));
                _body.Controls.Add(row);
                if (ticket.Key == _focusTicketKey) focusRow = row;
            }
        }
        _body.ResumeLayout();
        _body.AutoScrollPosition = new Point(-scrollPosition.X, -scrollPosition.Y);

        _focusSectionId = null;
        _focusTicketKey = null;
        focusHeader?.Focus();
        focusRow?.FocusKey();
    }

    // Deferred: the clicked button belongs to a row that Reload disposes.
    private void OnTicketMoved(string ticketKey, bool fromKeyboard)
    {
        if (fromKeyboard) _focusTicketKey = ticketKey;
        BeginInvoke(new Action(() =>
        {
            Reload();
            BoardChanged?.Invoke();
        }));
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

    // Deferred: putting the ticket into New entry re-renders the rows, disposing the clicked button.
    private void RaiseLogRequest(string ticketKey, string summary) =>
        BeginInvoke(new Action(() => LogRequested?.Invoke(ticketKey, summary)));

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
        var text = $"{section.Title} ({count})";
        var twisty = isExpanded ? Icons.ChevronDown : Icons.ChevronRight;
        var headerHeight = SectionFont.Height + UiScale.S(14);
        var header = new ClickableRow
        {
            Size = new Size(width, headerHeight),
            Margin = new Padding(0),
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand,
            AccessibleName = isExpanded ? text : $"{text}, {Lang.T("collapsed", "đã thu gọn")}",
        };
        header.Paint += (_, e) =>
        {
            var twistyWidth = Icons.Small.Height;
            Icons.Draw(e.Graphics, twisty, Icons.Small, new Rectangle(UiScale.S(2), 0, twistyWidth, headerHeight), Theme.TextSecondary);
            var textLeft = UiScale.S(2) + twistyWidth + UiScale.S(4);
            TextRenderer.DrawText(e.Graphics, text, SectionFont, new Rectangle(textLeft, 0, width - textLeft - UiScale.S(6), headerHeight),
                Theme.TextSecondary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
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
            StatusRaised?.Invoke(Lang.T("Config not loaded; cannot add a ticket.", "Chưa có cấu hình; không thêm được ticket."), NoticeKind.Error);
            return;
        }
        if (!TicketParser.TryParseKey(_addBox.Text, out var key))
        {
            StatusRaised?.Invoke(Lang.T("Not a ticket key or Jira link.", "Không phải mã ticket hay link Jira."), NoticeKind.Error);
            return;
        }

        _addBtn.Enabled = false;
        using var busyWork = BusyWork.Shared.Begin(Lang.T($"Adding {key}...", $"Đang thêm {key}..."));
        try
        {
            var ticket = await service.Jira.GetTicketAsync(key);
            if (IsDisposed) return;

            if (ticket == null)
                StatusRaised?.Invoke(Lang.T($"{key} was not found in Jira.", $"Không tìm thấy {key} trên Jira."), NoticeKind.Error);
            else if (_ticketBoard.Add(ticket))
            {
                StatusRaised?.Invoke(Lang.T($"{key} added to the board.", $"Đã thêm {key} vào bảng."), NoticeKind.Success);
                _addBox.Text = string.Empty;
                Reload();
                BoardChanged?.Invoke();
            }
            else
                StatusRaised?.Invoke(Lang.T($"{key} is already on the board.", $"{key} đã có trên bảng."), NoticeKind.Info);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[tasks] Add {key} failed: {ex.Message}");
            if (IsDisposed) return;
            StatusRaised?.Invoke(Lang.T($"Adding {key} failed: {ex.Message}", $"Thêm {key} lỗi: {ex.Message}"), NoticeKind.Error);
        }
        finally
        {
            if (!IsDisposed) _addBtn.Enabled = true;
        }
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
