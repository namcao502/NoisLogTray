using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NoisLogTray;

// An interactive `claude` session inside the window: xterm.js in a WebView2 draws what the
// ClaudeSessionHost (owned by TrayApp, so it outlives this view) runs under ConPTY.
internal sealed class TerminalView : UserControl
{
    private const int FlushIntervalMs = 16;
    private static readonly Font LabelFont = UiScale.Font("Segoe UI", 9F);

    private readonly ClaudeSessionHost _host;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Panel _bar = new() { Dock = DockStyle.Top };
    private readonly Label _directory = new() { AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly MacButton _restart = MacButton.Secondary(Lang.T("Restart", "Khởi động lại"));
    private readonly System.Windows.Forms.Timer _flushTimer = new() { Interval = FlushIntervalMs };
    private readonly object _pendingLock = new();
    private readonly MemoryStream _pending = new();
    private bool _flushScheduled;
    private bool _initStarted;
    private bool _ready;
    private short _columns = 120;
    private short _rows = 30;

    // Ctrl+digit typed inside the terminal (WebView2 swallows them before the form's ProcessCmdKey).
    internal event Action<ShellPage>? PageRequested;

    internal TerminalView(ClaudeSessionHost host)
    {
        _host = host;
        BackColor = Theme.WindowBg;
        _status.Font = UiScale.Font("Segoe UI", 10F);
        _status.ForeColor = Theme.TextSecondary;
        _status.Text = Lang.T("Starting the terminal...", "Đang mở terminal...");
        _webView.Visible = false;
        BuildBar();
        Controls.Add(_webView);
        Controls.Add(_status);
        Controls.Add(_bar);
        _flushTimer.Tick += (_, _) => FlushOutput();
        _host.Exited += OnHostExited;
        Theme.Changed += ApplyTheme;
        Lang.Changed += ApplyLanguage;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= ApplyTheme;
            Lang.Changed -= ApplyLanguage;
            _host.Exited -= OnHostExited;
            _host.Detach(OnHostOutput);
            _flushTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    internal void Activate()
    {
        if (!_initStarted)
        {
            _initStarted = true;
            _ = InitializeAsync();
            return;
        }
        // The window may have been resized while this view was hidden; the page answers with a resize.
        if (_ready && _webView.CoreWebView2 != null) _webView.CoreWebView2.PostWebMessageAsString("{\"type\":\"fit\"}");
        FocusTerminal();
    }

    private void BuildBar()
    {
        var barH = UiScale.S(40);
        var buttonH = UiScale.S(28);
        var restartW = ButtonWidths.ToolbarWidth(_restart.Font);
        _bar.Height = barH;
        _bar.Padding = new Padding(UiScale.S(12), 0, UiScale.S(8), 0);
        _directory.Font = LabelFont;
        _directory.ForeColor = Theme.TextSecondary;
        _directory.BackColor = Color.Transparent;
        _directory.Bounds = new Rectangle(UiScale.S(12), 0, UiScale.S(400), barH);
        _restart.OnWindow = true;
        _restart.Size = new Size(restartW, buttonH);
        _restart.Location = new Point(UiScale.S(400) - restartW, (barH - buttonH) / 2);
        _restart.Click += (_, _) => RestartSession();
        _bar.Controls.Add(_directory);
        _bar.Controls.Add(_restart);
        _bar.Resize += (_, _) => LayoutBar();
        UpdateDirectory();
    }

    private void LayoutBar()
    {
        _restart.Left = _bar.ClientSize.Width - _restart.Width - UiScale.S(8);
        _directory.Width = Math.Max(0, _restart.Left - _directory.Left - UiScale.S(12));
    }

    private void UpdateDirectory()
    {
        var directory = _host.CurrentDirectory;
        _directory.Text = directory == null ? "" : directory + (_host.IsRunning ? "" : Lang.T("  (stopped)", "  (đã dừng)"));
    }

    private async Task InitializeAsync()
    {
        try
        {
            await WebViewSetup.InitializeAsync(_webView);
            if (IsDisposed) return;
            var core = _webView.CoreWebView2;
            core.WebMessageReceived += OnWebMessage;
            var fontSize = (int)Math.Round(13 * UiScale.Factor);
            core.Navigate(WebViewSetup.PageUrl($"terminal/index.html?fontSize={fontSize}"));
            _webView.Visible = true;
            _status.Visible = false;
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or COMException)
        {
            if (IsDisposed) return; // the tab closed while WebView2 was still starting
            AppLogger.Error($"[claude] terminal init failed: {ex.Message}");
            _status.Text = Lang.T("Could not start the terminal (WebView2 runtime missing?).",
                "Không mở được terminal (thiếu WebView2 runtime?).");
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var message = JsonDocument.Parse(e.TryGetWebMessageAsString());
        var root = message.RootElement;
        switch (root.GetProperty("type").GetString())
        {
            case "ready":
                _ready = true;
                ReadSize(root);
                ApplyTheme();
                AttachToHost();
                FocusTerminal();
                break;
            case "resize":
                ReadSize(root);
                _host.Resize(_columns, _rows);
                break;
            case "input":
                var data = root.GetProperty("data").GetString() ?? "";
                if (!_host.IsRunning)
                {
                    // After the CLI exits, Enter starts a new session.
                    if (data.Contains('\r')) StartSession();
                    break;
                }
                _host.Write(Encoding.UTF8.GetBytes(data));
                break;
            case "page":
                var page = PageForShortcut(root.GetProperty("index").GetInt32());
                if (page != null) PageRequested?.Invoke(page.Value);
                break;
        }
    }

    internal static ShellPage? PageForShortcut(int index) => index switch
    {
        1 => ShellPage.Dashboard,
        2 => ShellPage.Log,
        3 => ShellPage.Tasks,
        4 => ShellPage.Week,
        5 => ShellPage.Claude,
        6 => ShellPage.Ticket,
        _ => null,
    };

    private void ReadSize(JsonElement root)
    {
        _columns = (short)Math.Max(20, root.GetProperty("cols").GetInt32());
        _rows = (short)Math.Max(5, root.GetProperty("rows").GetInt32());
    }

    // A rebuilt view replays what the running session already printed, then nudges a repaint.
    private void AttachToHost()
    {
        var replay = _host.Attach(OnHostOutput);
        if (_host.IsRunning)
        {
            if (replay.Length != 0) PostOutput(replay);
            _host.Resize(_columns, _rows);
            UpdateDirectory();
            return;
        }
        StartSession();
    }

    private void StartSession()
    {
        try
        {
            _host.Start(_columns, _rows);
        }
        catch (Exception ex) when (ex is Win32Exception or DirectoryNotFoundException)
        {
            AppLogger.Error($"[claude] start failed: {ex.Message}");
            PostText(Lang.T($"Could not start claude: {ex.Message}", $"Không chạy được claude: {ex.Message}"));
        }
        UpdateDirectory();
    }

    private void RestartSession()
    {
        if (!_ready) return;
        _host.Stop();
        lock (_pendingLock) _pending.SetLength(0);
        _webView.CoreWebView2?.PostWebMessageAsString("{\"type\":\"reset\"}");
        StartSession();
        FocusTerminal();
    }

    // Pty read thread: buffer and let the UI flush at most once per FlushIntervalMs.
    private void OnHostOutput(byte[] bytes)
    {
        lock (_pendingLock)
        {
            _pending.Write(bytes);
            if (_flushScheduled) return;
            _flushScheduled = true;
        }
        RunOnUi(() => _flushTimer.Start());
    }

    private void FlushOutput()
    {
        _flushTimer.Stop();
        byte[] bytes;
        lock (_pendingLock)
        {
            bytes = _pending.ToArray();
            _pending.SetLength(0);
            _flushScheduled = false;
        }
        if (bytes.Length != 0) PostOutput(bytes);
    }

    private void OnHostExited()
    {
        RunOnUi(() =>
        {
            FlushOutput();
            UpdateDirectory();
            PostText(Lang.T("Claude exited. Press Enter to start again.", "Claude đã thoát. Nhấn Enter để mở lại."));
        });
    }

    private void PostText(string text)
    {
        PostOutput(Encoding.UTF8.GetBytes($"\r\n\x1b[90m{text}\x1b[0m\r\n"));
    }

    private void PostOutput(byte[] bytes)
    {
        if (!_ready || _webView.CoreWebView2 == null) return;
        _webView.CoreWebView2.PostWebMessageAsString(
            JsonSerializer.Serialize(new { type = "output", data = Convert.ToBase64String(bytes) }));
    }

    internal void FocusTerminal()
    {
        if (!_ready || _webView.CoreWebView2 == null) return;
        _webView.Focus();
        _webView.CoreWebView2.PostWebMessageAsString("{\"type\":\"focus\"}");
    }

    private void ApplyLanguage()
    {
        _restart.Text = Lang.T("Restart", "Khởi động lại");
        _restart.Width = ButtonWidths.ToolbarWidth(_restart.Font);
        LayoutBar();
        UpdateDirectory();
    }

    private void ApplyTheme()
    {
        BackColor = Theme.WindowBg;
        _bar.BackColor = Theme.WindowBg;
        _directory.ForeColor = Theme.TextSecondary;
        _restart.Invalidate();
        if (!_ready || _webView.CoreWebView2 == null) return;
        var theme = new
        {
            background = ColorTranslator.ToHtml(Theme.WindowBg),
            foreground = ColorTranslator.ToHtml(Theme.TextPrimary),
            cursor = ColorTranslator.ToHtml(Theme.Accent),
            selectionBackground = Theme.Dark ? "#3a5a8c" : "#b4d2ff",
        };
        _webView.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(new { type = "theme", theme }));
    }

    private void RunOnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // The handle went away between the check and the call: the page is closing.
        }
    }
}
