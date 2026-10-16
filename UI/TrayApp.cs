using System.Diagnostics;
using System.Drawing;
using System.Reflection;

namespace NoisLogTray;

// Tray-resident application context. Owns the NotifyIcon, the capture window, and
// the daily scheduler. Background work (sniff, MCP, drain) runs off the UI thread
// and is marshaled back through a hidden control for tooltip/balloon/log updates.
internal sealed partial class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private Action? _balloonOpen; // where a click on the last balloon goes; null opens the window
    private bool _balloonClicked; // set while that click runs: only then may the window take the foreground from another app
    private readonly Control _marshal;
    private LoggingService? _service;
    private string? _configError;
    private TeamsNotifier _teams;
    private IAzureDevOpsClient? _ado; // null when no Azure DevOps PAT is configured
    // For the app's lifetime, not rebuilt on a Settings save: they hold the Teams session and the channel list.
    private readonly TeamsChatClient _teamsChat = new(TeamsWebSniffer.SniffAsync);
    private readonly TeamsThreadService _teamsThreads;
    private readonly SixPmScheduler _scheduler;
    private readonly ToolStripMenuItem _updateItem;
    private readonly ToolStripSeparator _updateSeparator;
    private UpdateInfo? _pendingUpdate; // set when a newer GitHub Release is found
    private ShellForm? _shell;
    private DashboardPage? _dashboardPage;
    private LogPage? _logPage; // the Work page's log column
    private TasksPage? _tasksPage; // the Work page's board; null when Tasks is disabled this session
    private WorkPage? _workPage;
    private ClaudePage? _claudePage;
    private TicketWorkspacePage? _ticketPage;
    private SettingsPage? _settingsPage;
    // All three stay null when the TaskWatcher state import failed: Tasks is disabled this session.
    private readonly TicketStore? _ticketStore;
    private readonly TicketBoard? _ticketBoard;
    private readonly TicketPoller? _ticketPoller;
    private readonly System.Windows.Forms.Timer _tasksTimer;
    private bool _checkingTickets;
    private int _ticketPollFailures;
    private DateTime? _lastTicketCheck;
    // Tickets with a Start / Ready to Test in flight; outlives TasksPage so a rebuilt page shows them busy.
    private readonly Dictionary<string, StartStage> _launchStages = new();
    internal const string TasksDisabledEnglish = "Tasks are disabled this session (setup failed; see log).";
    internal const string TasksDisabledVietnamese = "Tasks đang tắt trong phiên này (khởi tạo lỗi; xem log).";
    private int _draining; // 0 = idle, 1 = a drain is running
    private int _redrainPending; // 1 = a drain was requested while one ran; run one more pass
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromMinutes(5);

    // Tray menu items with both texts, so ApplyLanguage can re-label them on a switch.
    private readonly List<(ToolStripItem Item, string English, string Vietnamese)> _menuTexts = new();

    internal TrayApp()
    {
        _marshal = new Control();
        _ = _marshal.Handle; // force handle creation on the UI thread

        var config = AppConfig.TryLoad(out var error);
        _configError = error;
        _service = config != null ? new LoggingService(config) : null;
        (_teams, _ado) = CreatePrIntegrations(config);
        _teamsThreads = new TeamsThreadService(_teamsChat, new TeamsPostIndex(AppPaths.TeamsPostIndexPath),
            () => AppSettings.Load(), settings => AppSettings.Save(settings));
        StartHookServer();

        var menu = ThemedMenuRenderer.CreateMenu();
        // Hidden until the startup check finds a newer release; then it sits at the top.
        _updateItem = new ToolStripMenuItem("", null, (_, _) => OpenUpdatePage())
        {
            Visible = false,
        };
        _updateSeparator = new ToolStripSeparator { Visible = false };
        menu.Items.Add(_updateItem);
        menu.Items.Add(_updateSeparator);
        // Just Open and Quit: every page, Settings included, is one click away in the window.
        AddMenuItem(menu, "Open", "Mở", (_, _) => OpenShell());
        menu.Items.Add(new ToolStripSeparator());
        AddMenuItem(menu, "Quit", "Thoát", (_, _) => Quit());

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Load(16),
            Visible = true,
            Text = "NOIS Workday",
            ContextMenuStrip = menu,
        };
        // MouseClick (not the bare Click event) so a right-click to open the
        // ContextMenuStrip doesn't also open the window -- only a left click does.
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenShell();
        };
        _tray.BalloonTipClicked += (_, _) =>
        {
            _balloonClicked = true;
            try
            {
                (_balloonOpen ?? OpenShell)();
            }
            finally
            {
                _balloonClicked = false;
            }
        };

        ApplyLanguage();
        Lang.Changed += ApplyLanguage;

        // After _tray exists so Notify in the catch is safe.
        try
        {
            _ticketStore = new TicketStore(AppPaths.TicketsDirectory);
            var importedFileCount = _ticketStore.ImportFromTaskWatcher(AppPaths.TaskWatcherStateDirectory);
            if (importedFileCount != 0) AppLogger.Info($"[tickets] Imported {importedFileCount} TaskWatcher state file(s).");
            _ticketBoard = new TicketBoard(_ticketStore);
            _ticketPoller = new TicketPoller(_ticketStore, _ticketBoard);
        }
        catch (Exception e)
        {
            AppLogger.Error($"[tickets] Tasks disabled; state setup failed: {e}");
            Notify(Lang.T(TasksDisabledEnglish, TasksDisabledVietnamese), ToolTipIcon.Warning);
            _ticketStore = null;
            _ticketBoard = null;
            _ticketPoller = null;
        }
        _tasksTimer = new System.Windows.Forms.Timer { Interval = (int)TimeSpan.FromMinutes(TicketCheckInterval.Minutes).TotalMilliseconds };
        _tasksTimer.Tick += (_, _) =>
        {
            _ticketTimerArmedAt = DateTime.Now; // a WinForms timer counts down again from each tick
            _ = CheckTicketsAsync();
        };
        if (_ticketPoller != null) _tasksTimer.Start();
        // Settings' pills change it on the UI thread; a new Interval also restarts the countdown.
        TicketCheckInterval.Changed += () =>
        {
            _tasksTimer.Interval = (int)TimeSpan.FromMinutes(TicketCheckInterval.Minutes).TotalMilliseconds;
            _ticketTimerArmedAt = DateTime.Now;
            Log($"[tickets] checking Jira and PRs every {TicketCheckInterval.Minutes} min");
        };

        _scheduler = new SixPmScheduler(config?.LogTime ?? AppConfig.DefaultLogTime, OnScheduledFireAsync, Log);
        _scheduler.Start();
        _ = CheckTicketsAsync();
        CatchUpIfDue();
        _ = CheckForUpdateAsync(); // fire-and-forget; silent on any failure
        StartKnowledgeScanWatch();
        StartTeamsAttentionWatch();

        // TSC logging needs the system Chrome; warn once up front rather than at 6 PM.
        if (_service != null && !TscTokenSniffer.ChromeInstalled())
            Notify(Lang.T("Google Chrome is not installed - TSC logging needs it. Install Chrome from google.com/chrome.",
                "Chưa cài Google Chrome - log TSC cần nó. Cài Chrome tại google.com/chrome."), ToolTipIcon.Warning);

        // Once the message loop is running: prompt for first-run config if it's
        // missing (rather than a modal dialog inside the constructor), otherwise
        // nothing to do here.
        if (_service == null)
            _marshal.BeginInvoke(new Action(RunFirstRunSetup));

        AppLogger.Info("Tray ready.");
    }

    // First-run (or after a cancelled/incomplete setup): collect config, and on
    // success rebuild the service and open the window as confirmation.
    private void RunFirstRunSetup()
    {
        if (!PromptForCredentials(firstRun: true))
        {
            Notify(Lang.T("Setup skipped - logging is disabled. Click here to set it up.",
                "Đã bỏ qua thiết lập - chưa log được. Bấm vào đây để thiết lập."), ToolTipIcon.Warning, open: EditCredentials);
            return;
        }
        ReloadServiceAndShow();
        Notify(_service != null
            ? Lang.T("Setup complete. Use \"Re-authenticate TSC\" to finish signing in.",
                "Thiết lập xong. Dùng \"Đăng nhập lại TSC\" để hoàn tất đăng nhập.")
            : Lang.T($"Saved, but config is still invalid: {_configError}", $"Đã lưu, nhưng cấu hình vẫn chưa hợp lệ: {_configError}"),
            _service != null ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    // Reload config into a fresh service (which also drops the cached Graph token),
    // rebuild any open window so it uses it, and show the window as confirmation.
    private void ReloadServiceAndShow()
    {
        var config = AppConfig.TryLoad(out var error);
        _configError = error;
        _service = config != null ? new LoggingService(config) : null;
        (_teams, _ado) = CreatePrIntegrations(config);
        _scheduler.SetFireTime(config?.LogTime ?? AppConfig.DefaultLogTime);

        var page = _shell?.CurrentPage ?? ShellPage.Work;
        if (_shell != null && !_shell.IsDisposed)
        {
            _shell.Close(); // the UserClosing path: saves bounds, tells pages they are hidden
            _shell.Dispose();
        }
        _shell = null;
        _dashboardPage = null;
        _logPage = null;
        _tasksPage = null;
        _workPage = null;
        _claudePage = null;
        _ticketPage = null;
        _settingsPage = null;
        if (_service != null)
        {
            ShowShell(page);
            _ = CheckTicketsAsync();
        }
        UpdateTooltip();
    }

    private static (TeamsNotifier Teams, IAzureDevOpsClient? Ado) CreatePrIntegrations(AppConfig? config)
    {
        var teams = new TeamsNotifier(config?.TeamsWebhookUrl, config?.JiraBaseUrl.TrimEnd('/') ?? AppConfig.DefaultJiraBaseUrl);
        var pat = config?.AzureDevOpsPat;
        return (teams, string.IsNullOrEmpty(pat) ? null : new AzureDevOpsClient(pat));
    }

    private void ShowShell(ShellPage page)
    {
        if (_shell == null || _shell.IsDisposed)
        {
            var startMaximized = ShellLayout.StartMaximized(AppSettings.Load());
            _shell = new ShellForm();
            // Bound to this shell: an action still running after a Settings rebuild must not touch the new one.
            var shell = _shell;
            _dashboardPage = new DashboardPage(_service, _ticketStore, _service?.JiraBaseUrl ?? AppConfig.DefaultJiraBaseUrl,
                () => DevRepoPath, () => _openTickets);
            _shell.AddPage(ShellPage.Dashboard, _dashboardPage);
            _dashboardPage.LogQueueRequested += () => _ = DrainAsync(fromUser: true);
            _dashboardPage.CheckRequested += () => _ = CheckTicketsAsync();
            _dashboardPage.StartRequested += StartTicketAsync;
            _dashboardPage.OpenRequested += OpenTicketAsync;
            _dashboardPage.ReadyToTestRequested += MoveToReadyToTestAsync;
            _dashboardPage.QueueTicketsRequested += QueueTicketsForToday;
            _dashboardPage.MarkDoneRequested += MarkTicketDone;
            _dashboardPage.CommentsSeen += ClearPrCommentAlert;
            _dashboardPage.PageRequested += ShowShell;
            _dashboardPage.LogDayRequested += date =>
            {
                ShowShell(ShellPage.Work);
                _logPage?.PrepareForDate(date);
            };
            _dashboardPage.ClaudeSessionRequested += sessionName =>
            {
                if (TerminalTabs.IsTerminalSession(sessionName)) ShowTerminalTab(sessionName);
                else ShowTicketTab(sessionName);
            };
            _dashboardPage.CleanupRequested += OpenWorktreeCleanup;
            _dashboardPage.TeamsConversationRequested += ShowTeamsConversation;
            _dashboardPage.TeamsSeenRequested += MarkTeamsSeen;
            _dashboardPage.SetChecking(_checkingTickets, _lastTicketCheck);
            _dashboardPage.SetDraining(Volatile.Read(ref _draining) != 0); // a Settings rebuild can land mid-drain
            foreach (var (ticketKey, stage) in _launchStages) _dashboardPage.SetTicketBusy(ticketKey, stage);
            _dashboardPage.Reload();
            _logPage = new LogPage(_service, _configError);
            _shell.PageViewed += AcknowledgeSeenWaits;
            _shell.PageViewed += AcknowledgeWorkAttention;
            if (_ticketStore != null && _ticketBoard != null)
            {
                _tasksPage = new TasksPage(_ticketStore, _ticketBoard, () => _service);
                _tasksPage.CheckRequested += CheckTicketsAsync;
                _tasksPage.StartRequested += StartTicketAsync;
                _tasksPage.OpenRequested += OpenTicketAsync;
                _tasksPage.ReadyToTestRequested += MoveToReadyToTestAsync;
                // Not RefreshTicketViews: the Tasks page already reloaded itself.
                _tasksPage.BoardChanged += () =>
                {
                    _dashboardPage?.Reload();
                    _ticketPage?.Reload();
                };
                _tasksPage.AttentionChanged += OnWorkAttentionChanged;
                _tasksPage.StatusRaised += (message, kind) =>
                    Notify(message, kind == NoticeKind.Error ? ToolTipIcon.Warning : ToolTipIcon.Info, kind);
            }
            _workPage = new WorkPage(_logPage, _tasksPage);
            _shell.AddPage(ShellPage.Work, _workPage, _workPage.HeaderControl);
            if (_tasksPage != null)
            {
                _tasksPage.SetChecking(_checkingTickets, _lastTicketCheck);
                foreach (var (ticketKey, stage) in _launchStages) _tasksPage.SetTicketBusy(ticketKey, stage);
                _tasksPage.Reload(); // after subscribing, so the badge has a value right away
            }
            PruneTicketTabs();
            _ticketPage = new TicketWorkspacePage(_ticketStore, _service?.JiraBaseUrl ?? AppConfig.DefaultJiraBaseUrl, () => DevRepoPath, SessionFor, CreateTeamsThreadView,
                () => _ado);
            _shell.AddPage(ShellPage.Ticket, _ticketPage, _ticketPage.HeaderControl);
            // Deferred: the x button is on the Click stack and SetTabs disposes it.
            _ticketPage.CloseRequested += ticketKey => shell.BeginInvoke(() => CloseTicketTab(ticketKey));
            _ticketPage.VsCodeRequested += OpenInVsCode;
            // Only stores and clears a seen dot: SetTabs raises this itself, so it must not call SetTabs.
            _ticketPage.SelectionChanged += ticketKey => { _selectedTicket = ticketKey; AcknowledgeSeenWaits(); };
            _ticketPage.PageRequested += ShowShell;
            foreach (var (ticketKey, chatId) in _teamsTabsToOpen)
            {
                _ticketPage.OpenOnTeams(ticketKey, chatId);
            }
            _ticketPage.SetTabs(_openTickets, _selectedTicket); // before the page is shown, so HasOpenTabs is right for Esc
            // The sessions live in TrayApp so a rebuilt window (Settings save) reattaches instead of killing them.
            _claudePage = new ClaudePage(TerminalHostFor, ClaudeFolderChoices);
            _shell.AddPage(ShellPage.Claude, _claudePage, _claudePage.HeaderControl);
            _claudePage.PageRequested += ShowShell;
            _claudePage.AddRequested += AddTerminal;
            // Deferred: the x button is on the Click stack and SetTabs disposes it.
            _claudePage.CloseRequested += name => shell.BeginInvoke(() => CloseTerminal(name));
            _claudePage.FolderChosen += OnTerminalFolderChosen;
            _claudePage.SelectionChanged += OnTerminalSelectionChanged;
            ShowTerminalTabs(); // before the page is shown, so HasOpenTabs is right for Esc
            RefreshClaudeAttention();
            RefreshTeamsAttention();
            _settingsPage = new SettingsPage();
            _shell.AddPage(ShellPage.Settings, _settingsPage);
            _settingsPage.SaveRequested += OnSettingsSaved;
            _settingsPage.CleanupWorktreesRequested += OpenWorktreeCleanup;
            _settingsPage.RebuildRequested += RebuildAndRestart;
            _settingsPage.SchedulesRequested += OpenSchedules;
            ShowSchedulesBadge(); // after the Settings page exists: its button carries the dot
            _logPage.QueueChanged += UpdateTooltip;
            _logPage.QueueChanged += () => _dashboardPage?.Reload(); // its queue counts; cheap while hidden
            _logPage.ReauthSucceeded += CatchUpIfDue; // retry due entries once TSC is signed in
            _logPage.DrainRequested += () => _ = DrainAsync(fromUser: true); // "Log queue now"
            _logPage.StatusRaised += (message, kind) =>
                Notify(message, kind == NoticeKind.Error ? ToolTipIcon.Warning : ToolTipIcon.Info, kind, ShowWorkPage);
            _logPage.BusyChanged += text => { if (!shell.IsDisposed) shell.ShowBusy(text); };
            _shell.WindowState = startMaximized ? FormWindowState.Maximized : FormWindowState.Normal;
        }
        // Before Show: a hidden shell defers OnPageShown to OnVisibleChanged, so it fires once.
        _shell.ShowPage(page);
        if (_shell.WindowState == FormWindowState.Minimized)
            _shell.WindowState = ShellLayout.StartMaximized(AppSettings.Load()) ? FormWindowState.Maximized : FormWindowState.Normal;
        if (!_shell.Visible) _shell.Show();
        // A background call (the 18:00 reminder) must not steal focus while the user types elsewhere.
        if (_balloonClicked) _shell.ForceForeground();
        else _shell.Activate();
        _shell.BringToFront();
    }

    // Tray Open / left click: the Dashboard when the window is hidden, else keep the page on screen.
    private void OpenShell()
    {
        if (_shell != null && !_shell.IsDisposed && _shell.Visible) ShowShell(_shell.CurrentPage);
        else ShowShell(ShellPage.Dashboard);
    }

    // Every log, queue and new-ticket notice. With Tasks off, the page says so in the board's place.
    private void ShowWorkPage() => ShowShell(ShellPage.Work);

    // Start and the Dashboard's queue buttons; the refusals become one notice, grouped by reason.
    private void QueueTicketsForToday(IReadOnlyList<string> ticketKeys)
    {
        try
        {
            var refusals = new List<(string TicketKey, string Reason)>();
            foreach (var ticketKey in ticketKeys)
            {
                var now = Hcm.Now();
                QueueAddResult queueResult;
                try
                {
                    // A running drain read its snapshot already; a ticket merged now would survive alone and overwrite the cell.
                    queueResult = Volatile.Read(ref _draining) != 0 ? QueueAddResult.Draining : TicketQueue.AddStartedTicket(ticketKey, now, _service?.LogTime ?? AppConfig.DefaultLogTime,
                        TscWrittenDates.Contains(DateOnly.FromDateTime(now.DateTime)));
                    // A drain that began between the check and the write may hold a snapshot without this ticket; take it back out.
                    if (queueResult == QueueAddResult.Added && Volatile.Read(ref _draining) != 0)
                    {
                        TicketQueue.RemoveTicket(Hcm.ApiDate(DateOnly.FromDateTime(now.DateTime)), ticketKey);
                        queueResult = QueueAddResult.Draining;
                    }
                }
                catch (Exception e)
                {
                    AppLogger.Error($"[tasks] Queueing {ticketKey} failed: {e}");
                    Notify(Lang.T($"{ticketKey} not queued: the queue could not be saved (see log).",
                        $"Chưa thêm {ticketKey} vào queue: không lưu được queue (xem log)."), ToolTipIcon.Warning, open: ShowWorkPage);
                    return;
                }
                var reason = queueResult switch
                {
                    QueueAddResult.CustomHours => Lang.T("today has custom hours", "hôm nay đang chia giờ tuỳ chỉnh"),
                    QueueAddResult.TooLate => Lang.T("today's log time has passed", "đã qua giờ log hôm nay"),
                    QueueAddResult.AlreadyLogged => Lang.T("today's TSC cell was already written or attempted", "ô TSC hôm nay đã ghi hoặc đã thử ghi"),
                    QueueAddResult.Unsupported => Lang.T("only MDP tickets can be queued", "chỉ queue được ticket MDP"),
                    QueueAddResult.Weekend => Lang.T("it is the weekend", "hôm nay là cuối tuần"),
                    QueueAddResult.Draining => Lang.T("a log run is in progress", "đang có lượt log chạy"),
                    _ => null,
                };
                if (reason != null) refusals.Add((ticketKey, reason));
            }
            if (refusals.Count != 0)
            {
                var refusedKeys = refusals.GroupBy(refusal => refusal.Reason)
                    .Select(group => (Keys: string.Join(", ", group.Select(refusal => refusal.TicketKey)), Reason: group.Key))
                    .ToList();
                Notify(string.Join(" ", refusedKeys.Select(refused => Lang.T($"{refused.Keys} not queued: {refused.Reason} - log it in the window.",
                        $"Chưa thêm {refused.Keys} vào queue: {refused.Reason} - hãy log trong cửa sổ."))),
                    ToolTipIcon.Info, open: ShowWorkPage);
            }
            UpdateTooltip();
            if (_logPage != null && !_logPage.IsDisposed) _logPage.RefreshQueuedView();
        }
        finally
        {
            // In finally because the save-failure branch returns early.
            _dashboardPage?.Reload();
        }
    }

    // Opening a ticket counts as seeing its new PR comments.
    private void ClearPrCommentAlert(string ticketKey)
    {
        if (_ticketStore == null) return;
        try
        {
            var alerts = _ticketStore.LoadPrCommentAlerts();
            if (alerts.ContainsKey(ticketKey)) _ticketStore.SavePrCommentAlerts(PrCommentAlerts.Remove(alerts, ticketKey));
        }
        catch (Exception e)
        {
            AppLogger.Error($"[ado] Clearing PR comment alert for {ticketKey} failed: {e.Message}");
        }
        RefreshTicketViews();
    }

    // Dashboard "-> Done"; refreshed even after a failure, since the board may have saved part of the move.
    private void MarkTicketDone(string ticketKey)
    {
        try
        {
            _ticketBoard?.MarkDone(ticketKey);
        }
        catch (Exception e)
        {
            AppLogger.Error($"[tasks] Moving {ticketKey} to Done failed: {e.Message}");
        }
        finally
        {
            RefreshTicketViews();
        }
    }

    private void RefreshTicketViews()
    {
        _tasksPage?.Reload();
        _dashboardPage?.Reload();
        _ticketPage?.Reload();
    }

    private async Task MoveToReadyToTestAsync(string ticketKey)
    {
        if (_service == null || _ticketBoard == null || _launchStages.ContainsKey(ticketKey)) return;
        // Testers see the move and it cannot be walked back from here, so a stray click must not do it.
        var moveInJira = ConfirmDialog.Ask(_shell, Lang.T($"Move {ticketKey} to Ready to Test?", $"Chuyển {ticketKey} sang Ready to Test?"),
            Lang.T($"{ticketKey} is walked through Jira's statuses up to Ready to Test. The team sees the change.",
                $"{ticketKey} sẽ được chuyển qua các trạng thái Jira đến Ready to Test. Cả team sẽ thấy thay đổi này."),
            ButtonWidths.ReadyToTestText, confirmByDefault: true);
        if (!moveInJira) return;
        var jira = _service.Jira; // captured: a Settings save may replace _service mid-walk
        SetLaunchStage(ticketKey, StartStage.UpdatingJira);
        using var busyWork = BusyWork.Shared.Begin(Lang.T($"Moving {ticketKey} to Ready to Test...", $"Đang chuyển {ticketKey} sang Ready to Test..."));
        try
        {
            try
            {
                var live = await jira.GetTicketAsync(ticketKey);
                // Steps come from Jira's live status, never the stored one, so a ticket is never walked backward.
                var steps = JiraStatusFlow.GetStepsToReadyToTest(live?.Status);
                if (live == null)
                    Notify(Lang.T($"{ticketKey} was not found in Jira.", $"Không tìm thấy {ticketKey} trên Jira."), ToolTipIcon.Warning);
                else if (steps.Count == 0)
                    Notify(Lang.T($"{ticketKey} is already at or past Ready to Test.", $"{ticketKey} đã ở hoặc qua Ready to Test."), ToolTipIcon.Info);
                foreach (var step in steps)
                {
                    if (await jira.TransitionAsync(ticketKey, step)) continue;
                    AppLogger.Error($"[tasks] Ready to Test {ticketKey}: no transition to {step}.");
                    Notify(Lang.T($"{ticketKey}: could not move to {step} (see log).", $"{ticketKey}: không chuyển được sang {step} (xem log)."), ToolTipIcon.Warning);
                    break;
                }
            }
            catch (Exception e)
            {
                AppLogger.Error($"[tasks] Ready to Test {ticketKey} failed: {e}");
                Notify(Lang.T($"{ticketKey}: Jira update failed (see log).", $"{ticketKey}: cập nhật Jira lỗi (xem log)."), ToolTipIcon.Warning);
            }

            // Re-read even after a partial walk so the row shows Jira's real status.
            try
            {
                var refreshed = await jira.GetTicketAsync(ticketKey);
                if (refreshed != null) _ticketBoard.UpdateTicketDetails(new() { [ticketKey] = refreshed });
            }
            catch (Exception e)
            {
                AppLogger.Error($"[tasks] Refresh {ticketKey} failed: {e.Message}");
            }
        }
        finally
        {
            SetLaunchStage(ticketKey, null);
            RefreshTicketViews();
        }
    }

    private void NotifyRepoNotFound(string repoPath) =>
        Notify(Lang.T($"Dev repo not found: {repoPath}. Set it in Settings.",
            $"Không thấy repo dev: {repoPath}. Đặt lại trong Cài đặt."), ToolTipIcon.Warning, open: EditCredentials);

    // A null stage clears the ticket's busy state.
    private void SetLaunchStage(string ticketKey, StartStage? stage)
    {
        if (stage.HasValue) _launchStages[ticketKey] = stage.Value;
        else _launchStages.Remove(ticketKey);
        _tasksPage?.SetTicketBusy(ticketKey, stage);
        _dashboardPage?.SetTicketBusy(ticketKey, stage);
    }

    // Check GitHub Releases once at startup. On a newer release, reveal the "Download
    // update" menu item and show a one-time balloon. Silent on any failure/offline.
    private async Task CheckForUpdateAsync()
    {
        var current = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
        var update = await UpdateService.CheckAsync(current);
        if (update == null) return;

        _pendingUpdate = update;
        RunOnUi(() =>
        {
            ApplyLanguage(); // the update item's text carries the version
            _updateItem.Visible = true;
            _updateSeparator.Visible = true;
        });
        Notify(Lang.T($"Update v{update.Latest} available - open the tray menu to download.",
            $"Có bản cập nhật v{update.Latest} - mở menu khay để tải."), ToolTipIcon.Info, open: OpenUpdatePage);
    }

    // Open the release page in the default browser so the user can download the new build.
    private void OpenUpdatePage()
    {
        if (_pendingUpdate == null) return;
        try
        {
            Process.Start(new ProcessStartInfo(_pendingUpdate.Url) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Notify(Lang.T($"Could not open the download page: {e.Message}", $"Không mở được trang tải: {e.Message}"), ToolTipIcon.Warning);
        }
    }

    private void UpdateTooltip() => RunOnUi(() =>
    {
        var count = TicketQueue.Read().Count;
        _tray.Text = count > 0 ? Lang.T($"NOIS Workday ({count} queued)", $"NOIS Workday ({count} đang chờ)") : "NOIS Workday";
    });

    // Show the credentials dialog; on save, write the per-user config into settings.json.
    // Returns true if saved. Used both at first run (config missing) and from the menu.
    private bool PromptForCredentials(bool firstRun)
    {
        var initial = AppConfig.ReadUserValues();
        using var dialog = new CredentialsForm(initial, firstRun);
        // Center on the window when it is open (the sidebar), else on screen (tray / first run).
        var owner = _shell != null && !_shell.IsDisposed && _shell.Visible ? _shell : null;
        if (owner != null) dialog.StartPosition = FormStartPosition.CenterParent;
        else dialog.ShowInTaskbar = true; // no window behind it, so keep it findable
        if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
        return TrySaveUserConfig(dialog.Values);
    }

    // Shared by the dialog and the Settings page; a failed write is a notice, not a crash.
    private bool TrySaveUserConfig(IReadOnlyDictionary<string, string> values)
    {
        try
        {
            AppConfig.SaveUserConfig(values);
            return true;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            AppLogger.Error($"[settings] Saving settings failed: {e.Message}");
            Notify(Lang.T("Could not save settings - see the log.", "Không lưu được cài đặt - xem log."), ToolTipIcon.Warning);
            return false;
        }
    }

    // Config notices: the Settings page; the dialog only while there is no working config.
    private void EditCredentials()
    {
        if (_service != null)
        {
            ShowShell(ShellPage.Settings);
            return;
        }
        if (!PromptForCredentials(firstRun: false)) return;
        ReloadServiceAndShow();
        NotifySettingsResult();
    }

    // Save from the Settings page: write the config, then rebuild the service and the window.
    private void OnSettingsSaved(IReadOnlyDictionary<string, string> values)
    {
        if (!TrySaveUserConfig(values)) return;
        // Deferred: the rebuild disposes the shell, and the panel's Save click is still on the stack.
        _marshal.BeginInvoke(new Action(() =>
        {
            ReloadServiceAndShow();
            NotifySettingsResult();
        }));
    }

    // Still-invalid config leaves no window to edit in, so the dialog reopens for another try.
    private void NotifySettingsResult()
    {
        Notify(_service != null ? Lang.T("Settings saved.", "Đã lưu cài đặt.")
            : Lang.T($"Saved, but config still invalid: {_configError}", $"Đã lưu, nhưng cấu hình vẫn chưa hợp lệ: {_configError}"),
            _service != null ? ToolTipIcon.Info : ToolTipIcon.Warning);
        if (_service == null) EditCredentials();
    }

    private void Quit()
    {
        // Quitting stops every ticket's terminal, maybe mid-`ship`; ask the way closing a running tab does.
        var runningTickets = _ticketSessions.Where(kvp => kvp.Value.IsRunning).Select(kvp => kvp.Key).ToList();
        if (runningTickets.Count != 0)
        {
            var owner = _shell != null && _shell.Visible ? _shell : null;
            var quit = ConfirmDialog.Ask(owner, Lang.T("Quit NOIS Workday?", "Thoát NOIS Workday?"),
                Lang.T($"The terminal is still running in {string.Join(", ", runningTickets)}. Quit and stop it?",
                    $"Terminal vẫn đang chạy trong {string.Join(", ", runningTickets)}. Thoát và dừng nó?"),
                Lang.T("Quit", "Thoát"), destructive: true);
            if (!quit) return;
        }
        ShutDown();
    }

    private void ShutDown()
    {
        _scheduler.Dispose();
        _tasksTimer.Dispose();
        _knowledgeScanTimer.Dispose();
        StopTeamsAttentionWatch();
        _schedulesDialog?.Close();
        _shell?.Dispose();
        foreach (var host in _terminalHosts.Values) host.Dispose();
        foreach (var host in _ticketSessions.Values) host.Dispose();
        _hookServer?.Dispose();
        ExitThread();
    }

    private static void Log(string line) => AppLogger.Info(line);

    private void AddMenuItem(ContextMenuStrip menu, string english, string vietnamese, EventHandler onClick)
    {
        var item = new ToolStripMenuItem(Lang.T(english, vietnamese), null, onClick);
        menu.Items.Add(item);
        _menuTexts.Add((item, english, vietnamese));
    }

    // Re-label the tray menu and tooltip for the current language (the window re-labels itself).
    private void ApplyLanguage()
    {
        foreach (var (item, english, vietnamese) in _menuTexts) item.Text = Lang.T(english, vietnamese);
        _updateItem.Text = _pendingUpdate != null
            ? Lang.T($"Download update v{_pendingUpdate.Latest}...", $"Tải bản cập nhật v{_pendingUpdate.Latest}...")
            : Lang.T("Download update...", "Tải bản cập nhật...");
        UpdateTooltip();
    }

    // Every notice lands in the window's bell history and shows in-window while the window is
    // on screen; a Windows balloon is added unless the window is the one in the foreground.
    // A click on the toast, the history line or the balloon runs open (the place the notice is about).
    private void Notify(string message, ToolTipIcon icon, NoticeKind? kind = null, Action? open = null) =>
        RunOnUi(() =>
        {
            var noticeKind = kind ?? (icon == ToolTipIcon.Info ? NoticeKind.Info : NoticeKind.Error);
            var shell = _shell != null && !_shell.IsDisposed ? _shell : null;
            shell?.PostNotice(message, noticeKind, open);
            if (shell == null || !shell.IsForeground)
            {
                // Not cleared on BalloonTipClosed: the replaced balloon's close can arrive after this one is set.
                _balloonOpen = open;
                _tray.ShowBalloonTip(5000, "NOIS Workday", message, icon);
            }
        });

    private void RunOnUi(Action action)
    {
        if (_marshal.IsDisposed) return;
        if (_marshal.InvokeRequired) _marshal.BeginInvoke(action);
        else action();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scheduler.Dispose();
            _tasksTimer.Dispose();
            _knowledgeScanTimer.Dispose();
        StopTeamsAttentionWatch();
            Lang.Changed -= ApplyLanguage;
            _tray.Visible = false;
            _tray.Dispose();
            _schedulesDialog?.Close();
            _shell?.Dispose();
            foreach (var host in _terminalHosts.Values) host.Dispose();
            foreach (var host in _ticketSessions.Values) host.Dispose();
            _hookServer?.Dispose();
            _marshal.Dispose();
        }
        base.Dispose(disposing);
    }
}
