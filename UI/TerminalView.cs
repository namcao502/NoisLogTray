using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NoisLogTray;

// A terminal inside the window, a PowerShell that runs claude: xterm.js in a WebView2 draws what the ClaudeSessionHost
// (owned by TrayApp, so it outlives this view) runs under ConPTY.
internal sealed class TerminalView : UserControl
{
    private const int FlushIntervalMs = 16;
    private static readonly Regex PathLinkPattern = new(@"^(?<path>.+?)(?::(?<line>\d+))?(?::\d+)?$", RegexOptions.Compiled);

    private readonly ClaudeSessionHost _host;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    // Only on a view whose session may move (the Claude page): ticket tabs are pinned to their worktree.
    private readonly Func<IReadOnlyList<string>>? _folderChoices;
    private readonly MacButton? _folderButton;
    private readonly ContextMenuStrip _folderMenu = ThemedMenuRenderer.CreateMenu();
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
    // Raised before the session restarts, so the host's working-directory func can already return the new folder.
    internal event Action<string>? FolderChosen;
    // Ctrl+click on a path in the terminal, only for a file that exists: its full path and the line, when one was given.
    internal event Action<string, int?>? FileRequested;

    internal TerminalView(ClaudeSessionHost host, Func<IReadOnlyList<string>>? folderChoices = null)
    {
        _host = host;
        _folderChoices = folderChoices;
        if (folderChoices != null) _folderButton = MacButton.Secondary(FolderButtonText());
        BackColor = Theme.WindowBg;
        _status.Font = UiScale.Font("Segoe UI", 10F);
        _status.ForeColor = Theme.TextSecondary;
        _status.Text = Lang.T("Starting the terminal...", "Đang mở terminal...");
        _webView.Visible = false;
        Controls.Add(_webView);
        Controls.Add(_status);
        if (_folderButton != null) BuildFolderButton(_folderButton);
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
            _folderMenu.Dispose();
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

    // The Folder menu's button, which the Claude page puts in the shell header (HeaderControl); the menu's checked
    // item names the folder, so no path is shown.
    internal MacButton? FolderButton => _folderButton;

    private void BuildFolderButton(MacButton folderButton)
    {
        folderButton.OnWindow = true;
        // Fits the longer label of either language plus the chevron, so a language switch never resizes it.
        var labelWidth = Math.Max(TextRenderer.MeasureText("Folder", folderButton.Font).Width,
            TextRenderer.MeasureText("Thư mục", folderButton.Font).Width);
        folderButton.Width = labelWidth + Icons.Small.Height + UiScale.S(30);
        folderButton.Glyph = Icons.ChevronDown;
        folderButton.GlyphAfterText = true;
        folderButton.Click += (_, _) => ShowFolderMenu();
        _folderMenu.ShowCheckMargin = true;
    }

    private static string FolderButtonText() => Lang.T("Folder", "Thư mục");

    // Rebuilt on every open, so a folder picked elsewhere or deleted since shows up right.
    private void ShowFolderMenu()
    {
        if (_folderChoices == null || _folderButton == null) return;
        _folderMenu.Items.Clear();
        foreach (var folder in _folderChoices())
        {
            var item = new ToolStripMenuItem(folder)
            {
                Checked = string.Equals(folder, _host.CurrentDirectory, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) => ChooseFolder(folder);
            _folderMenu.Items.Add(item);
        }
        _folderMenu.Items.Add(new ToolStripSeparator());
        var browse = new ToolStripMenuItem(Lang.T("Choose another folder...", "Chọn thư mục khác..."));
        browse.Click += (_, _) => BrowseFolder();
        _folderMenu.Items.Add(browse);
        ThemedMenuRenderer.ShowBelow(_folderMenu, _folderButton);
    }

    private void BrowseFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = Lang.T("Choose the folder Claude runs in", "Chọn thư mục để chạy Claude"),
            UseDescriptionForTitle = true,
        };
        if (_host.CurrentDirectory != null && Directory.Exists(_host.CurrentDirectory)) dialog.InitialDirectory = _host.CurrentDirectory;
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK) ChooseFolder(dialog.SelectedPath);
    }

    // A running terminal (maybe Claude mid-task) is only stopped after a Yes.
    private void ChooseFolder(string folder)
    {
        if (string.Equals(folder, _host.CurrentDirectory, StringComparison.OrdinalIgnoreCase) && _host.IsRunning) return;
        if (_host.IsRunning)
        {
            var changeFolder = ConfirmDialog.Ask(FindForm(), Lang.T("Change folder?", "Đổi thư mục?"),
                Lang.T($"The terminal is running. Stop it and start Claude in {folder}?", $"Terminal đang chạy. Dừng nó và mở Claude ở {folder}?"),
                Lang.T("Change folder", "Đổi thư mục"), destructive: true);
            if (!changeFolder) return;
        }
        FolderChosen?.Invoke(folder);
        if (_ready) RestartSession();
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
            core.Navigate(WebViewSetup.PageUrl(
                $"terminal/index.html?fontSize={fontSize}&theme={Uri.EscapeDataString(JsonSerializer.Serialize(ThemeMessage()))}"));
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
                PostLabels();
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
                    // After the shell exits, Enter starts a new one (and claude in it).
                    if (data.Contains('\r')) StartSession();
                    break;
                }
                _host.Write(Encoding.UTF8.GetBytes(data));
                break;
            case "pasteRequest":
                PasteClipboard();
                break;
            case "openPath":
                var link = ResolvePathLink(root.GetProperty("text").GetString() ?? "", _host.CurrentDirectory);
                if (link != null) FileRequested?.Invoke(link.Value.FullPath, link.Value.Line);
                break;
            case "openUrl":
                // Text claude printed: only a web link leaves the app.
                var webUrl = TaskRow.WebUrl(root.GetProperty("url").GetString());
                if (webUrl != null) TaskRow.OpenUrl(webUrl);
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
        2 => ShellPage.Work,
        3 => ShellPage.Claude,
        4 => ShellPage.Ticket,
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
            PostText(Lang.T($"Could not start the terminal: {ex.Message}", $"Không mở được terminal: {ex.Message}"));
        }
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
            PostText(Lang.T("The terminal exited. Press Enter to start it again with Claude.",
                "Terminal đã thoát. Nhấn Enter để mở lại cùng Claude."));
        });
    }

    // Ctrl+V or a right-click in the terminal: the clipboard is read here, since the page would need a clipboard-read
    // permission; text goes back to the page so xterm.js applies bracketed paste.
    private void PasteClipboard()
    {
        try
        {
            var files = Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToList() : new List<string>();
            var (text, keys) = PasteFor(Clipboard.ContainsText() ? Clipboard.GetText() : null, Clipboard.ContainsImage(), files);
            if (text != null) _webView.CoreWebView2?.PostWebMessageAsString(JsonSerializer.Serialize(new { type = "paste", text }));
            if (keys != null && _host.IsRunning) _host.Write(Encoding.UTF8.GetBytes(keys));
        }
        catch (ExternalException ex)
        {
            AppLogger.Error($"[claude] reading the clipboard failed: {ex.Message}");
        }
    }

    // "UI/a.cs:42:7" as printed by claude: a relative path is against the session's folder; null unless the file exists.
    internal static (string FullPath, int? Line)? ResolvePathLink(string text, string? workingDirectory)
    {
        var match = PathLinkPattern.Match(text);
        if (!match.Success) return null;
        var path = match.Groups["path"].Value;
        if (!Path.IsPathRooted(path) && workingDirectory == null) return null;
        int? line = match.Groups["line"].Success && int.TryParse(match.Groups["line"].Value, out var number) && number != 0 ? number : null;
        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(workingDirectory ?? "", path));
            return File.Exists(fullPath) ? (fullPath, line) : null;
        }
        catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
        {
            return null;
        }
    }

    // Text wins (Excel copies cells as text and as a picture). For an image, claude's own paste-image key, Alt+V on
    // Windows (ESC v): claude then reads the clipboard itself. Files copied in Explorer paste as their quoted paths.
    internal static (string? Text, string? Keys) PasteFor(string? text, bool hasImage, IReadOnlyCollection<string> files)
    {
        if (!string.IsNullOrEmpty(text)) return (text, null);
        if (hasImage) return (null, "\u001bv");
        if (files.Count != 0) return (string.Join(" ", files.Select(path => $"\"{path}\"")), null);
        return (null, null);
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
        if (_folderButton != null) _folderButton.Text = FolderButtonText();
        PostLabels();
    }

    private void PostLabels()
    {
        if (!_ready || _webView.CoreWebView2 == null) return;
        var labels = new { type = "labels", jumpToBottom = Lang.T("Jump to the bottom", "Cuộn xuống cuối") };
        _webView.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(labels));
    }

    private void ApplyTheme()
    {
        BackColor = Theme.WindowBg;
        _folderButton?.Invalidate();
        if (!_ready || _webView.CoreWebView2 == null) return;
        _webView.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(ThemeMessage()));
    }

    // Also put on the page's URL, so the terminal's first paint is in these colors rather than xterm's defaults.
    private static object ThemeMessage()
    {
        // The scrollbar matches the app's own (ThemedScrollBar, the web views' CSS): the secondary text color, faint until used.
        var muted = Theme.TextSecondary;
        return new
        {
            type = "theme",
            theme = new
            {
                background = ColorTranslator.ToHtml(Theme.WindowBg),
                foreground = ColorTranslator.ToHtml(Theme.TextPrimary),
                cursor = ColorTranslator.ToHtml(Theme.Accent),
                selectionBackground = Theme.Dark ? "#3a5a8c" : "#b4d2ff",
                scrollbarSliderBackground = $"rgba({muted.R}, {muted.G}, {muted.B}, 0.4)",
                scrollbarSliderHoverBackground = $"rgba({muted.R}, {muted.G}, {muted.B}, 0.6)",
                scrollbarSliderActiveBackground = $"rgba({muted.R}, {muted.G}, {muted.B}, 0.8)",
            },
            // The jump-to-bottom button is a small card over the terminal, so it takes the card colors.
            jump = new
            {
                background = ColorTranslator.ToHtml(Theme.CardSurface),
                foreground = ColorTranslator.ToHtml(Theme.TextPrimary),
                border = ColorTranslator.ToHtml(Theme.CardBorder),
                hover = ColorTranslator.ToHtml(Theme.Hover),
            },
        };
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
