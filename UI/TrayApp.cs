using System.Diagnostics;
using System.Drawing;
using System.Reflection;

namespace NoisLogTray;

// Tray-resident application context. Owns the NotifyIcon, the capture window, and
// the daily scheduler. Background work (sniff, MCP, drain) runs off the UI thread
// and is marshaled back through a hidden control for tooltip/balloon/log updates.
internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly Control _marshal;
    private LoggingService? _service;
    private string? _configError;
    private TeamsNotifier _teams;
    private IAzureDevOpsClient? _ado; // null when no Azure DevOps PAT is configured
    private readonly SixPmScheduler _scheduler;
    private readonly ToolStripMenuItem _updateItem;
    private readonly ToolStripSeparator _updateSeparator;
    private UpdateInfo? _pendingUpdate; // set when a newer GitHub Release is found
    private ShellForm? _shell;
    private DashboardPage? _dashboardPage;
    private LogPage? _logPage;
    private TasksPage? _tasksPage; // null when Tasks is disabled this session
    private WeekPage? _weekPage;
    private SettingsPage? _settingsPage;
    // All three stay null when the TaskWatcher state import failed: Tasks is disabled this session.
    private readonly TicketStore? _ticketStore;
    private readonly TicketBoard? _ticketBoard;
    private readonly TicketPoller? _ticketPoller;
    private readonly System.Windows.Forms.Timer _tasksTimer;
    private bool _checkingTickets;
    private int _ticketPollFailures;
    private DateTime? _lastTicketCheck;
    // Tickets with a Start / Open in flight; outlives TasksPage so a rebuilt page shows them busy.
    private readonly Dictionary<string, StartStage> _launchStages = new();
    private const string TasksDisabledEnglish = "Tasks are disabled this session (setup failed; see log).";
    private const string TasksDisabledVietnamese = "Tasks đang tắt trong phiên này (khởi tạo lỗi; xem log).";
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

        var menu = new ContextMenuStrip();
        // Hidden until the startup check finds a newer release; then it sits at the top.
        _updateItem = new ToolStripMenuItem("", null, (_, _) => OpenUpdatePage())
        {
            Visible = false,
        };
        _updateSeparator = new ToolStripSeparator { Visible = false };
        menu.Items.Add(_updateItem);
        menu.Items.Add(_updateSeparator);
        // Only what has no other home: logging and TSC actions live in the window, and
        // startup / log folder sit in Settings.
        AddMenuItem(menu, "Open", "Mở", (_, _) => OpenShell());
        AddMenuItem(menu, "Weekly check...", "Kiểm tra tuần...", (_, _) => ShowWeeklyCheck());
        AddMenuItem(menu, "Tasks...", "Công việc...", (_, _) => ShowTasks());
        AddMenuItem(menu, "Settings...", "Cài đặt...", (_, _) => EditCredentials());
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
        _tasksTimer = new System.Windows.Forms.Timer { Interval = (int)TimeSpan.FromMinutes(5).TotalMilliseconds };
        _tasksTimer.Tick += (_, _) => _ = CheckTicketsAsync();
        if (_ticketPoller != null) _tasksTimer.Start();

        _scheduler = new SixPmScheduler(config?.LogTime ?? AppConfig.DefaultLogTime, OnScheduledFireAsync, Log);
        _scheduler.Start();
        _ = CheckTicketsAsync();
        CatchUpIfDue();
        _ = CheckForUpdateAsync(); // fire-and-forget; silent on any failure

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
            Notify(Lang.T("Setup skipped - logging is disabled. Use \"Settings...\" in the tray menu to set it up.",
                "Đã bỏ qua thiết lập - chưa log được. Dùng \"Cài đặt...\" trong menu khay để thiết lập."), ToolTipIcon.Warning);
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

        var page = _shell?.CurrentPage ?? ShellPage.Log;
        if (_shell != null && !_shell.IsDisposed)
        {
            _shell.Close(); // the UserClosing path: saves bounds, tells pages they are hidden
            _shell.Dispose();
        }
        _shell = null;
        _dashboardPage = null;
        _logPage = null;
        _tasksPage = null;
        _weekPage = null;
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

    // If the app was not running at 18:00 (asleep, off, or launched later), a queue
    // that is already due would otherwise wait until the next 18:00. Drain once on
    // startup when something is loggable now: a past-dated entry (loggable anytime),
    // or a today entry once it is 18:00 or later (HRM rejects future stop times).
    private void CatchUpIfDue()
    {
        if (_service == null) return;

        var now = Hcm.Now();
        var today = DateOnly.FromDateTime(now.DateTime);
        var due = false;
        foreach (var entry in TicketQueue.Read())
        {
            if (!DateOnly.TryParseExact(entry.Date, "yyyy-MM-dd", out var date)) continue;
            if (date < today || (date == today && now.Hour >= 18)) { due = true; break; }
        }

        if (!due) return;
        Log("[scheduler] Catch-up: queue has due entries; draining now.");
        _ = DrainAsync(fromUser: false);
    }

    private void ShowShell(ShellPage page)
    {
        if (_shell == null || _shell.IsDisposed)
        {
            var startMaximized = ShellLayout.StartMaximized(AppSettings.Load());
            _shell = new ShellForm();
            // Bound to this shell: an action still running after a Settings rebuild must not touch the new one.
            var shell = _shell;
            _dashboardPage = new DashboardPage(_service, _ticketStore, _service?.JiraBaseUrl ?? AppConfig.DefaultJiraBaseUrl);
            _shell.AddPage(ShellPage.Dashboard, _dashboardPage);
            _dashboardPage.LogQueueRequested += () => _ = DrainAsync(fromUser: true);
            _dashboardPage.CheckRequested += () => _ = CheckTicketsAsync();
            _dashboardPage.StartRequested += StartTicketAsync;
            _dashboardPage.OpenRequested += OpenTicketAsync;
            _dashboardPage.ReadyToTestRequested += MoveToReadyToTestAsync;
            _dashboardPage.QueueTicketRequested += QueueTicketForToday;
            _dashboardPage.MarkDoneRequested += MarkTicketDone;
            _dashboardPage.CommentsSeen += ClearPrCommentAlert;
            _dashboardPage.PageRequested += ShowShell;
            _dashboardPage.SetChecking(_checkingTickets, _lastTicketCheck);
            _dashboardPage.SetDraining(Volatile.Read(ref _draining) != 0); // a Settings rebuild can land mid-drain
            foreach (var (ticketKey, stage) in _launchStages) _dashboardPage.SetTicketBusy(ticketKey, stage);
            _dashboardPage.Reload();
            _logPage = new LogPage(_service, _configError);
            _shell.AddPage(ShellPage.Log, _logPage);
            // Only Tasks can be missing: its page is not added when the state setup failed.
            _shell.PageUnavailable += _ => Notify(Lang.T(TasksDisabledEnglish, TasksDisabledVietnamese), ToolTipIcon.Warning);
            if (_ticketStore != null && _ticketBoard != null)
            {
                _tasksPage = new TasksPage(_ticketStore, _ticketBoard, () => _service);
                _shell.AddPage(ShellPage.Tasks, _tasksPage);
                _tasksPage.CheckRequested += CheckTicketsAsync;
                _tasksPage.StartRequested += StartTicketAsync;
                _tasksPage.OpenRequested += OpenTicketAsync;
                _tasksPage.ReadyToTestRequested += MoveToReadyToTestAsync;
                _tasksPage.AttentionChanged += count => shell.SetBadge(ShellPage.Tasks, count);
                _tasksPage.SetChecking(_checkingTickets, _lastTicketCheck);
                foreach (var (ticketKey, stage) in _launchStages) _tasksPage.SetTicketBusy(ticketKey, stage);
                _tasksPage.Reload(); // after subscribing, so the badge has a value right away
            }
            _weekPage = new WeekPage(_service);
            _shell.AddPage(ShellPage.Week, _weekPage);
            // Clicking an under-logged day opens the Log page on that date.
            _weekPage.LogDayRequested += date => { ShowShell(ShellPage.Log); _logPage?.PrepareForDate(date); };
            _settingsPage = new SettingsPage();
            _shell.AddPage(ShellPage.Settings, _settingsPage);
            _settingsPage.SaveRequested += OnSettingsSaved;
            _logPage.QueueChanged += UpdateTooltip;
            _logPage.QueueChanged += () => _dashboardPage?.Reload(); // its queue counts; cheap while hidden
            _logPage.ReauthSucceeded += CatchUpIfDue; // retry due entries once TSC is signed in
            _logPage.DrainRequested += () => _ = DrainAsync(fromUser: true); // "Log queue now"
            _logPage.StatusRaised += (message, kind) =>
                Notify(message, kind == NoticeKind.Error ? ToolTipIcon.Warning : ToolTipIcon.Info, kind);
            _logPage.BusyChanged += text => { if (!shell.IsDisposed) shell.ShowBusy(text); };
            _shell.WindowState = startMaximized ? FormWindowState.Maximized : FormWindowState.Normal;
        }
        // Before Show: a hidden shell defers OnPageShown to OnVisibleChanged, so it fires once.
        _shell.ShowPage(page);
        if (_shell.WindowState == FormWindowState.Minimized)
            _shell.WindowState = ShellLayout.StartMaximized(AppSettings.Load()) ? FormWindowState.Maximized : FormWindowState.Normal;
        if (!_shell.Visible) _shell.Show();
        _shell.Activate();
        _shell.BringToFront();
    }

    // Tray Open / left click: the Dashboard when the window is hidden, else keep the page on screen.
    private void OpenShell()
    {
        if (_shell != null && !_shell.IsDisposed && _shell.Visible) ShowShell(_shell.CurrentPage);
        else ShowShell(ShellPage.Dashboard);
    }

    // Tray "Weekly check...": the window on the Week page.
    private void ShowWeeklyCheck()
    {
        if (_service == null) { Notify(Lang.T("Config not loaded; set up credentials first.", "Chưa có cấu hình; hãy thiết lập thông tin đăng nhập trước."), ToolTipIcon.Warning); return; }
        ShowShell(ShellPage.Week);
    }

    // Tray "Tasks...": the window on the Tasks page.
    private void ShowTasks()
    {
        if (_ticketStore == null || _ticketBoard == null)
        {
            Notify(Lang.T(TasksDisabledEnglish, TasksDisabledVietnamese), ToolTipIcon.Warning);
            return;
        }
        ShowShell(ShellPage.Tasks);
    }

    // The ContainsKey check and first SetLaunchStage run before any await, so a double click cannot launch twice.
    private async Task StartTicketAsync(string ticketKey)
    {
        if (_ticketBoard == null || _launchStages.ContainsKey(ticketKey)) return;
        var repoPath = _service?.DevRepoPath ?? AppConfig.DefaultDevRepoPath;
        if (Directory.Exists(repoPath))
        {
            SetLaunchStage(ticketKey, StartStage.Fetching);
            // Created on the UI thread so reports come back here; a report after the finally is ignored.
            var progress = new Progress<StartStage>(stage =>
            {
                if (_launchStages.ContainsKey(ticketKey)) SetLaunchStage(ticketKey, stage);
            });
            try
            {
                if (!await Task.Run(() => TicketLauncher.StartNewTicket(ticketKey, repoPath, progress)))
                    Notify(Lang.T($"{ticketKey}: worktree failed - opened the main repo without ship (see log).",
                        $"{ticketKey}: tạo worktree lỗi - đã mở repo chính, không ship (xem log)."), ToolTipIcon.Warning);
            }
            catch (Exception e)
            {
                AppLogger.Error($"[tasks] Start {ticketKey} failed: {e}");
                Notify(Lang.T($"{ticketKey}: start failed (see log).", $"{ticketKey}: Start lỗi (xem log)."), ToolTipIcon.Warning);
            }
            finally
            {
                SetLaunchStage(ticketKey, null);
            }
        }
        else
        {
            NotifyRepoNotFound(repoPath);
        }

        _ticketBoard.Start(ticketKey);
        RefreshTicketViews();
        QueueTicketForToday(ticketKey);
    }

    // Start and the Dashboard's queue button; a refusal becomes a notice.
    private void QueueTicketForToday(string ticketKey)
    {
        try
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
                    $"Chưa thêm {ticketKey} vào queue: không lưu được queue (xem log)."), ToolTipIcon.Warning);
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
            if (reason != null)
                Notify(Lang.T($"{ticketKey} not queued: {reason} - log it in the window.",
                    $"Chưa thêm {ticketKey} vào queue: {reason} - hãy log trong cửa sổ."), ToolTipIcon.Info);
            UpdateTooltip();
            if (_logPage != null && !_logPage.IsDisposed) _logPage.RefreshQueuedView();
        }
        finally
        {
            // In finally because the save-failure branch returns early.
            _dashboardPage?.Reload();
        }
    }

    // Open: reopen an existing worktree (or the main repo) in VS Code; no board or queue change.
    private async Task OpenTicketAsync(string ticketKey)
    {
        if (_launchStages.ContainsKey(ticketKey)) return;
        var repoPath = _service?.DevRepoPath ?? AppConfig.DefaultDevRepoPath;
        if (!Directory.Exists(repoPath))
        {
            NotifyRepoNotFound(repoPath);
            return;
        }

        SetLaunchStage(ticketKey, StartStage.OpeningVsCode);
        try
        {
            await Task.Run(() => TicketLauncher.OpenExistingTicket(ticketKey, repoPath));
            ClearPrCommentAlert(ticketKey);
            // `code` returns before VS Code has opened; keep the row busy a little longer.
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        catch (Exception e)
        {
            AppLogger.Error($"[tasks] Open {ticketKey} failed: {e}");
        }
        finally
        {
            SetLaunchStage(ticketKey, null);
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
    }

    private async Task MoveToReadyToTestAsync(string ticketKey)
    {
        if (_service == null || _ticketBoard == null || _launchStages.ContainsKey(ticketKey)) return;
        var jira = _service.Jira; // captured: a Settings save may replace _service mid-walk
        SetLaunchStage(ticketKey, StartStage.UpdatingJira);
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
            $"Không thấy repo dev: {repoPath}. Đặt lại trong Cài đặt."), ToolTipIcon.Warning);

    // A null stage clears the ticket's busy state.
    private void SetLaunchStage(string ticketKey, StartStage? stage)
    {
        if (stage.HasValue) _launchStages[ticketKey] = stage.Value;
        else _launchStages.Remove(ticketKey);
        _tasksPage?.SetTicketBusy(ticketKey, stage);
        _dashboardPage?.SetTicketBusy(ticketKey, stage);
    }

    // Poll Jira for newly assigned / finished tickets. Started on the UI thread, so the
    // continuations (store access, form updates) stay there.
    private async Task CheckTicketsAsync()
    {
        if (_checkingTickets || _service == null || _ticketPoller == null) return;
        _checkingTickets = true;
        _tasksPage?.SetChecking(true, _lastTicketCheck);
        _dashboardPage?.SetChecking(true, _lastTicketCheck);
        try
        {
            try
            {
                var result = await _ticketPoller.PollAsync(_service.Jira);
                if (result == null)
                {
                    // Once per failure streak, so a long offline day does not nag every 15 minutes.
                    if (++_ticketPollFailures == 3)
                        Notify(Lang.T($"Jira ticket check failed {_ticketPollFailures} times in a row.",
                            $"Kiểm tra ticket Jira lỗi {_ticketPollFailures} lần liên tiếp."), ToolTipIcon.Warning);
                }
                else
                {
                    _ticketPollFailures = 0;
                    _lastTicketCheck = DateTime.Now;
                    if (result.NewTickets.Count != 0)
                        Notify(Lang.T("New ticket(s): ", "Ticket mới: ") + string.Join(", ", result.NewTickets.Select(jt => jt.Key)), ToolTipIcon.Info);
                    if (result.FinishedTickets.Count != 0)
                        Notify(Lang.T("Done in Jira (move them to Done in Tasks): ", "Đã Done trên Jira (chuyển sang Done trong Tasks): ")
                            + string.Join(", ", result.FinishedTickets.Select(jt => jt.Key)), ToolTipIcon.Info);
                    foreach (var ticket in result.NewTickets) await _teams.PostNewTicketAsync(ticket);
                }
            }
            catch (Exception e)
            {
                AppLogger.Error($"[tickets] Check failed: {e}");
            }

            // Azure DevOps does not depend on Jira, so PRs are checked even after a failed poll.
            try
            {
                await CheckPrsAsync();
            }
            catch (Exception e)
            {
                AppLogger.Error($"[ado] PR check failed: {e}");
            }
        }
        finally
        {
            _checkingTickets = false;
            try
            {
                _tasksPage?.SetChecking(false, _lastTicketCheck);
                _dashboardPage?.SetChecking(false, _lastTicketCheck);
                RefreshTicketViews();
            }
            catch (Exception e)
            {
                AppLogger.Error($"[tickets] Refreshing the Tasks page failed: {e}");
            }
        }
    }

    private async Task CheckPrsAsync()
    {
        // Captured: a Settings save may swap them mid-check.
        var ado = _ado;
        var teams = _teams;
        if (ado == null || _ticketStore == null) return;
        // The first check only records current PR state, so pre-existing PRs do not flood notices.
        var baselineOnly = !_ticketStore.IsPrBaselineDone();
        var prs = await PrUpdateChecker.CheckAsync(ado, _ticketStore, baselineOnly);
        // Only a complete pass is a usable baseline; otherwise the next pass would announce everything it missed.
        if (baselineOnly && prs.IsComplete) _ticketStore.MarkPrBaselineDone();

        var commentAlertsToAdd = new List<(string TicketKey, int NewCommentCount, string PrUrl)>();
        foreach (var (ticketKey, notification) in prs.Notifications)
        {
            var branch = $"{DashboardDataBuilder.RepoLabel(notification.Pr.Repository)} {notification.Pr.Branch}"; // e.g. "FE dev"
            var url = notification.Pr.Url;
            if (notification.IsMerged)
            {
                Notify(Lang.T($"{ticketKey}: PR ({branch}) merged.", $"{ticketKey}: PR ({branch}) đã merge."), ToolTipIcon.Info);
                await teams.PostPrMergedAsync(ticketKey, branch, url);
            }
            if (notification.NewCommentCount != 0)
            {
                var newCommentCount = notification.NewCommentCount;
                commentAlertsToAdd.Add((ticketKey, newCommentCount, url));
                Notify(Lang.T($"{ticketKey}: {newCommentCount} new PR comment(s) ({branch}).", $"{ticketKey}: {newCommentCount} comment PR mới ({branch})."), ToolTipIcon.Info);
                await teams.PostNewPrCommentAsync(ticketKey, branch, url, newCommentCount);
            }
        }
        MergePrCommentAlerts(commentAlertsToAdd);
        foreach (var ticketKey in prs.DevMergedNeedingReadyToTest)
            Notify(Lang.T($"{ticketKey} merged into development - move it to Ready to Test once CI deployed.",
                $"{ticketKey} đã merge vào development - chuyển sang Ready to Test khi CI deploy xong."), ToolTipIcon.Info);
    }

    // Synchronous on purpose (no await): a "seen"/Open handled during the Teams awaits above is not overwritten.
    private void MergePrCommentAlerts(List<(string TicketKey, int NewCommentCount, string PrUrl)> commentAlertsToAdd)
    {
        var ticketStore = _ticketStore;
        if (ticketStore == null) return;
        try
        {
            var loadedAlerts = ticketStore.LoadPrCommentAlerts();
            var alerts = loadedAlerts;
            var atHcm = Hcm.Now();
            foreach (var (ticketKey, newCommentCount, prUrl) in commentAlertsToAdd)
                alerts = PrCommentAlerts.Add(alerts, ticketKey, newCommentCount, prUrl, atHcm);
            var boardKeys = ticketStore.LoadPending().Keys
                .Concat(ticketStore.LoadStarted().Keys)
                .Concat(ticketStore.LoadDone().Keys)
                .ToHashSet();
            alerts = PrCommentAlerts.KeepOnly(alerts, boardKeys);
            if (!PrCommentAlerts.AreSame(loadedAlerts, alerts)) ticketStore.SavePrCommentAlerts(alerts);
        }
        catch (Exception e)
        {
            AppLogger.Error($"[ado] Saving PR comment alerts failed: {e.Message}");
        }
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
            $"Có bản cập nhật v{update.Latest} - mở menu khay để tải."), ToolTipIcon.Info);
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

    // The daily scheduled fire (at the configured time). If something is queued, drain
    // it as usual. If nothing is queued and it's a weekday, open the window as a
    // reminder to log manually; on weekends stay silent.
    private async Task OnScheduledFireAsync()
    {
        if (_service == null) return;

        if (TicketQueue.Read().Count != 0)
        {
            await DrainAsync(fromUser: false);
            return;
        }

        if (!IsWeekday(Hcm.Now()))
        {
            Log("[scheduler] Nothing queued (weekend); no reminder.");
            return;
        }

        // Read-only HRM lookup, no TSC write. GetOffDatesAsync never throws - a failed
        // call returns empty and the reminder still opens, since nagging is the safe
        // direction when a day off cannot be told from a missed working day.
        var today = Hcm.Today();
        var offDates = await _service.GetOffDatesAsync(today, today, Log);
        if (offDates.Contains(today))
        {
            Log("[scheduler] Today is an approved day off; no reminder.");
            return;
        }

        Log("[scheduler] Nothing queued on a weekday; opening the window as a reminder.");
        RunOnUi(() =>
        {
            ShowShell(ShellPage.Log);
            Notify(Lang.T("Reminder: nothing is queued for today - log your work before you leave.",
                "Nhắc nhở: hôm nay chưa có gì trong hàng đợi - hãy log công việc trước khi về."), ToolTipIcon.Info);
        });
    }

    private static bool IsWeekday(DateTimeOffset t) =>
        t.DayOfWeek != DayOfWeek.Saturday && t.DayOfWeek != DayOfWeek.Sunday;

    private async Task DrainAsync(bool fromUser)
    {
        if (_service == null)
        {
            if (fromUser) Notify(Lang.T("Config not loaded; cannot log.", "Chưa có cấu hình; không log được."), ToolTipIcon.Warning);
            return;
        }
        if (Interlocked.CompareExchange(ref _draining, 1, 0) != 0)
        {
            // A drain is already running; its snapshot may predate work just requested,
            // so flag one more pass to run after it finishes.
            Interlocked.Exchange(ref _redrainPending, 1);
            if (fromUser) Notify(Lang.T("A logging run is already in progress.", "Đang có một lượt log chạy."), ToolTipIcon.Info);
            return;
        }
        RunOnUi(() => { if (_dashboardPage != null && !_dashboardPage.IsDisposed) _dashboardPage.SetDraining(true); });

        var hadToday = false;
        try
        {
            var todayApiDate = Hcm.ApiDate(Hcm.Today());
            hadToday = TicketQueue.Read().Count(qe => qe.Date == todayApiDate) != 0;
            using var cts = new CancellationTokenSource(DrainTimeout);
            var r = await _service.DrainQueueAsync(Log, cts.Token);
            UpdateTooltip();
            RunOnUi(() => RefreshAfterDrain(hadToday));

            if (r.Total == 0)
            {
                if (fromUser) Notify(Lang.T("Queue is empty.", "Hàng đợi trống."), ToolTipIcon.Info);
            }
            else if (r.Kept > 0)
            {
                Notify(Lang.T($"Auto-log: {r.Logged} logged, {r.Kept} kept. Check TSC sign-in (Re-authenticate) - it retries automatically.",
                    $"Tự log: đã log {r.Logged}, giữ lại {r.Kept}. Kiểm tra đăng nhập TSC (Đăng nhập lại) - app sẽ tự thử lại."),
                    ToolTipIcon.Warning);
            }
            else
            {
                Notify(Lang.T($"Auto-log: {r.Logged} logged.", $"Tự log: đã log {r.Logged}."), ToolTipIcon.Info);
            }
        }
        catch (OperationCanceledException)
        {
            UpdateTooltip();
            RunOnUi(() => RefreshAfterDrain(hadToday));
            Log($"[drain] Timed out after {DrainTimeout.TotalMinutes:0} min; kept unlogged entries for retry.");
            Notify(Lang.T("Auto-log timed out; kept entries for retry. Check TSC sign-in if this repeats.",
                "Tự log quá thời gian; đã giữ lại để thử lại. Nếu lặp lại, hãy kiểm tra đăng nhập TSC."),
                ToolTipIcon.Warning);
        }
        catch (Exception e)
        {
            // The drain may have removed some entries before failing.
            UpdateTooltip();
            RunOnUi(() => RefreshAfterDrain(hadToday));
            Log($"[drain] Error: {e.Message}");
            Notify(Lang.T($"Auto-log error: {e.Message}", $"Tự log lỗi: {e.Message}"), ToolTipIcon.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _draining, 0);
            RunOnUi(() => { if (_dashboardPage != null && !_dashboardPage.IsDisposed) _dashboardPage.SetDraining(false); });
        }

        // If work was requested while the drain above was running, run one more pass to
        // pick up entries that arrived after that drain read its snapshot.
        if (Interlocked.Exchange(ref _redrainPending, 0) == 1)
            await DrainAsync(fromUser: false);
    }

    // UI thread only. Coverage re-reads without a sniff: the drain's Graph token is still cached.
    private void RefreshAfterDrain(bool hadToday)
    {
        if (_logPage != null && !_logPage.IsDisposed) _logPage.RefreshQueuedView();
        if (_dashboardPage == null || _dashboardPage.IsDisposed) return;
        _dashboardPage.Reload();
        if (hadToday && _dashboardPage.CoverageLoaded) _dashboardPage.ReloadCoverage(allowSniff: false);
    }

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

    // Tray "Settings...": the Settings page; the dialog only while there is no working config (no window to host it).
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
        _scheduler.Dispose();
        _tasksTimer.Dispose();
        _shell?.Dispose();
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
    private void Notify(string message, ToolTipIcon icon, NoticeKind? kind = null) =>
        RunOnUi(() =>
        {
            var noticeKind = kind ?? (icon == ToolTipIcon.Info ? NoticeKind.Info : NoticeKind.Error);
            var shell = _shell != null && !_shell.IsDisposed ? _shell : null;
            shell?.PostNotice(message, noticeKind);
            if (shell == null || !shell.IsForeground) _tray.ShowBalloonTip(5000, "NOIS Workday", message, icon);
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
            Lang.Changed -= ApplyLanguage;
            _tray.Visible = false;
            _tray.Dispose();
            _shell?.Dispose();
            _marshal.Dispose();
        }
        base.Dispose(disposing);
    }
}
