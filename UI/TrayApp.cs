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
    private readonly SixPmScheduler _scheduler;
    private readonly ToolStripMenuItem _updateItem;
    private readonly ToolStripSeparator _updateSeparator;
    private UpdateInfo? _pendingUpdate; // set when a newer GitHub Release is found
    private MainForm? _form;
    private WeeklyCheckForm? _weeklyForm;
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
        AddMenuItem(menu, "Open", "Mở", (_, _) => ShowForm());
        AddMenuItem(menu, "Weekly check...", "Kiểm tra tuần...", (_, _) => ShowWeeklyCheck());
        AddMenuItem(menu, "Settings...", "Cài đặt...", (_, _) => EditCredentials());
        menu.Items.Add(new ToolStripSeparator());
        AddMenuItem(menu, "Quit", "Thoát", (_, _) => Quit());

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Load(16),
            Visible = true,
            Text = "NOIS Daily Log",
            ContextMenuStrip = menu,
        };
        // MouseClick (not the bare Click event) so a right-click to open the
        // ContextMenuStrip doesn't also open the window -- only a left click does.
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowForm();
        };

        ApplyLanguage();
        Lang.Changed += ApplyLanguage;

        _scheduler = new SixPmScheduler(config?.LogTime ?? AppConfig.DefaultLogTime, OnScheduledFireAsync, Log);
        _scheduler.Start();
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
        _scheduler.SetFireTime(config?.LogTime ?? AppConfig.DefaultLogTime);

        if (_form != null && !_form.IsDisposed)
        {
            _form.Dispose();
            _form = null;
        }
        if (_service != null) ShowForm();
        UpdateTooltip();
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

    private void ShowForm()
    {
        if (_form == null || _form.IsDisposed)
        {
            _form = new MainForm(_service, _configError);
            _form.QueueChanged += UpdateTooltip;
            _form.ReauthSucceeded += CatchUpIfDue; // retry due entries once TSC is signed in
            _form.DrainRequested += () => _ = DrainAsync(fromUser: true); // "Log queue now"
            // Deferred: saving disposes and rebuilds the window, so not inside its own click.
            _form.SettingsRequested += () => _marshal.BeginInvoke(new Action(EditCredentials));
            _form.StatusRaised += (message, kind) =>
                Notify(message, kind == NoticeKind.Error ? ToolTipIcon.Warning : ToolTipIcon.Info, kind);
        }
        _form.Show();
        _form.WindowState = FormWindowState.Normal;
        _form.Activate();
        _form.BringToFront();
    }

    // Open (or re-focus) the read-only weekly coverage window.
    private void ShowWeeklyCheck()
    {
        if (_service == null) { Notify(Lang.T("Config not loaded; set up credentials first.", "Chưa có cấu hình; hãy thiết lập thông tin đăng nhập trước."), ToolTipIcon.Warning); return; }
        if (_weeklyForm == null || _weeklyForm.IsDisposed)
        {
            _weeklyForm = new WeeklyCheckForm(_service);
            // Clicking an under-logged day opens the main window focused on that date.
            _weeklyForm.LogDayRequested += date => { ShowForm(); _form?.PrepareForDate(date); };
        }
        _weeklyForm.Show();
        _weeklyForm.WindowState = FormWindowState.Normal;
        _weeklyForm.Activate();
        _weeklyForm.BringToFront();
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
        _tray.Text = count > 0 ? Lang.T($"NOIS Daily Log ({count} queued)", $"NOIS Daily Log ({count} đang chờ)") : "NOIS Daily Log";
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
            ShowForm();
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

        try
        {
            using var cts = new CancellationTokenSource(DrainTimeout);
            var r = await _service.DrainQueueAsync(Log, cts.Token);
            UpdateTooltip();
            RunOnUi(() => { if (_form != null && !_form.IsDisposed) _form.RefreshQueuedView(); });

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
            RunOnUi(() => { if (_form != null && !_form.IsDisposed) _form.RefreshQueuedView(); });
            Log($"[drain] Timed out after {DrainTimeout.TotalMinutes:0} min; kept unlogged entries for retry.");
            Notify(Lang.T("Auto-log timed out; kept entries for retry. Check TSC sign-in if this repeats.",
                "Tự log quá thời gian; đã giữ lại để thử lại. Nếu lặp lại, hãy kiểm tra đăng nhập TSC."),
                ToolTipIcon.Warning);
        }
        catch (Exception e)
        {
            Log($"[drain] Error: {e.Message}");
            Notify(Lang.T($"Auto-log error: {e.Message}", $"Tự log lỗi: {e.Message}"), ToolTipIcon.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _draining, 0);
        }

        // If work was requested while the drain above was running, run one more pass to
        // pick up entries that arrived after that drain read its snapshot.
        if (Interlocked.Exchange(ref _redrainPending, 0) == 1)
            await DrainAsync(fromUser: false);
    }

    // Show the credentials dialog; on save, write the per-user config into settings.json.
    // Returns true if saved. Used both at first run (config missing) and from the menu.
    private bool PromptForCredentials(bool firstRun)
    {
        var initial = AppConfig.ReadUserValues();
        using var dialog = new CredentialsForm(initial, firstRun);
        // Center on the window when it is open (the gear), else on screen (tray / first run).
        var owner = _form != null && !_form.IsDisposed && _form.Visible ? _form : null;
        if (owner != null) dialog.StartPosition = FormStartPosition.CenterParent;
        if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
        AppConfig.SaveUserConfig(dialog.Values);
        return true;
    }

    // Settings (header gear or tray "Settings..."): edit the config, then rebuild the service and show the window as
    // confirmation the save took effect.
    private void EditCredentials()
    {
        if (!PromptForCredentials(firstRun: false)) return;
        ReloadServiceAndShow();
        Notify(_service != null ? Lang.T("Settings saved.", "Đã lưu cài đặt.")
            : Lang.T($"Saved, but config still invalid: {_configError}", $"Đã lưu, nhưng cấu hình vẫn chưa hợp lệ: {_configError}"),
            _service != null ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    private void Quit()
    {
        _scheduler.Dispose();
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
            var form = _form != null && !_form.IsDisposed ? _form : null;
            form?.PostNotice(message, noticeKind);
            if (form == null || !form.IsForeground) _tray.ShowBalloonTip(5000, "NOIS Daily Log", message, icon);
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
            Lang.Changed -= ApplyLanguage;
            _tray.Visible = false;
            _tray.Dispose();
            _form?.Dispose();
            _marshal.Dispose();
        }
        base.Dispose(disposing);
    }
}
