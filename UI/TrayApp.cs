using System.Drawing;

namespace NoisLogTray;

// Tray-resident application context. Owns the NotifyIcon, the capture window, and
// the 18:00 scheduler. Background work (sniff, MCP, drain) runs off the UI thread
// and is marshaled back through a hidden control for tooltip/balloon/log updates.
internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly Control _marshal;
    private LoggingService? _service;
    private string? _configError;
    private readonly SixPmScheduler _scheduler;
    private readonly ToolStripMenuItem _startupItem;
    private MainForm? _form;
    private int _draining; // 0 = idle, 1 = a drain is running

    internal TrayApp()
    {
        _marshal = new Control();
        _ = _marshal.Handle; // force handle creation on the UI thread

        var config = AppConfig.TryLoad(out var error);
        _configError = error;
        _service = config != null ? new LoggingService(config) : null;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowForm());
        menu.Items.Add("Log queue now", null, async (_, _) => await DrainAsync(fromUser: true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Check TSC session", null, async (_, _) => await CheckTscAsync());
        menu.Items.Add("Re-authenticate TSC", null, async (_, _) => await ReauthAsync());
        menu.Items.Add("Edit credentials...", null, (_, _) => EditCredentials());
        menu.Items.Add(new ToolStripSeparator());
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup())
        {
            Checked = StartupService.IsEnabled(),
            CheckOnClick = false,
        };
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Load(16),
            Visible = true,
            Text = "NOIS Daily Log",
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowForm();

        UpdateTooltip();

        _scheduler = new SixPmScheduler(() => DrainAsync(fromUser: false), Log);
        _scheduler.Start();
        CatchUpIfDue();

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
            Notify("Setup skipped - logging is disabled. Use \"Edit credentials...\" to set it up.",
                ToolTipIcon.Warning);
            return;
        }
        ReloadServiceAndShow();
        Notify(_service != null
            ? "Setup complete. Use \"Re-authenticate TSC\" to finish signing in."
            : $"Saved, but config is still invalid: {_configError}",
            _service != null ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    // Reload config into a fresh service (which also drops the cached Graph token),
    // rebuild any open window so it uses it, and show the window as confirmation.
    private void ReloadServiceAndShow()
    {
        var config = AppConfig.TryLoad(out var error);
        _configError = error;
        _service = config != null ? new LoggingService(config) : null;

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
        }
        _form.Show();
        _form.WindowState = FormWindowState.Normal;
        _form.Activate();
        _form.BringToFront();
    }

    private void UpdateTooltip() => RunOnUi(() =>
    {
        var count = TicketQueue.Read().Count;
        _tray.Text = count > 0 ? $"NOIS Daily Log ({count} queued)" : "NOIS Daily Log";
    });

    private async Task DrainAsync(bool fromUser)
    {
        if (_service == null)
        {
            if (fromUser) Notify("Config not loaded; cannot log.", ToolTipIcon.Warning);
            return;
        }
        if (Interlocked.CompareExchange(ref _draining, 1, 0) != 0)
        {
            if (fromUser) Notify("A logging run is already in progress.", ToolTipIcon.Info);
            return;
        }

        try
        {
            var r = await _service.DrainQueueAsync(Log);
            UpdateTooltip();
            RunOnUi(() => { if (_form != null && !_form.IsDisposed) _form.RefreshQueuedView(); });

            if (r.Total == 0)
            {
                if (fromUser) Notify("Queue is empty.", ToolTipIcon.Info);
            }
            else if (r.Kept > 0)
            {
                Notify($"Auto-log: {r.Logged} logged, {r.Kept} kept. Check TSC sign-in (Re-authenticate) - it retries automatically.",
                    ToolTipIcon.Warning);
            }
            else
            {
                Notify($"Auto-log: {r.Logged} logged.", ToolTipIcon.Info);
            }
        }
        catch (Exception e)
        {
            Log($"[drain] Error: {e.Message}");
            Notify($"Auto-log error: {e.Message}", ToolTipIcon.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _draining, 0);
        }
    }

    private async Task CheckTscAsync()
    {
        Notify("Checking TSC session...", ToolTipIcon.Info);
        var (loggedIn, error) = await TscTokenSniffer.CheckCredentialsAsync();
        if (error != null)
            Notify($"TSC check failed: {error}", ToolTipIcon.Warning);
        else
            Notify(loggedIn ? "TSC session is valid." : "TSC session is logged out. Use Re-authenticate.",
                loggedIn ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    private async Task ReauthAsync()
    {
        Notify("Opening a browser for TSC sign-in...", ToolTipIcon.Info);
        var (ok, error) = await TscTokenSniffer.ReauthenticateAsync(Log);
        if (ok) _service?.InvalidateGraphToken();
        Notify(ok ? "TSC session saved." : $"Re-auth failed: {error}",
            ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
        if (ok) CatchUpIfDue(); // retry any queue entries that were waiting on sign-in
    }

    // Show the credentials dialog; on save, write the per-user .env. Returns true if
    // saved. Used both at first run (config missing) and from the tray menu.
    private bool PromptForCredentials(bool firstRun)
    {
        var initial = AppConfig.ReadUserValues();
        using var dialog = new CredentialsForm(initial, firstRun);
        if (dialog.ShowDialog() != DialogResult.OK) return false;
        AppConfig.SaveUserEnv(dialog.Values);
        return true;
    }

    // Tray menu: edit credentials, then rebuild the service and show the window as
    // confirmation the save took effect.
    private void EditCredentials()
    {
        if (!PromptForCredentials(firstRun: false)) return;
        ReloadServiceAndShow();
        Notify(_service != null ? "Credentials saved." : $"Saved, but config still invalid: {_configError}",
            _service != null ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    private void ToggleStartup()
    {
        var target = !_startupItem.Checked;
        var result = StartupService.TrySet(target);
        if (result.Success)
        {
            _startupItem.Checked = target;
            Notify(target ? "Will start with Windows." : "Won't start with Windows.", ToolTipIcon.Info);
        }
        else
        {
            Notify(result.ErrorMessage ?? "Startup change failed.", ToolTipIcon.Warning);
        }
    }

    private void Quit()
    {
        _scheduler.Dispose();
        ExitThread();
    }

    private void Log(string line)
    {
        AppLogger.Info(line);
        RunOnUi(() => { if (_form is { IsDisposed: false }) _form.AppendLog(line); });
    }

    private void Notify(string message, ToolTipIcon icon) =>
        RunOnUi(() => _tray.ShowBalloonTip(5000, "NOIS Daily Log", message, icon));

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
            _tray.Visible = false;
            _tray.Dispose();
            _form?.Dispose();
            _marshal.Dispose();
        }
        base.Dispose(disposing);
    }
}
