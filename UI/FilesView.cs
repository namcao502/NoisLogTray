using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NoisLogTray;

// The ticket's docs and worktree files, read-only: text in Monaco, markdown rendered, images inline. Lists again on every Activate / Refresh.
internal sealed class FilesView : UserControl
{
    private const string DocsGroup = "docs";
    private readonly string _ticketKey;
    private readonly Func<IReadOnlyList<TicketWorktree>> _worktrees;
    private readonly Func<string?> _docsPath;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Dictionary<string, string> _roots = new(); // group id -> its folder
    private readonly HashSet<(string Group, string Path)> _files = new();
    private (string Group, string Path, DateTime WriteTimeUtc, long Length)? _shown; // the file the page shows, as last sent
    private (string Group, string Path, string FullPath, int? Line)? _pendingReveal; // a Ctrl+clicked terminal path, until a listing
    private bool _initStarted;
    private bool _ready;
    private bool _loading;
    private bool _refreshAgain;
    private int _selectVersion;

    // Ctrl+digit typed inside the files page (WebView2 swallows them before the form's ProcessCmdKey).
    internal event Action<ShellPage>? PageRequested;

    internal FilesView(string ticketKey, Func<IReadOnlyList<TicketWorktree>> worktrees, Func<string?> docsPath)
    {
        _ticketKey = ticketKey;
        _worktrees = worktrees;
        _docsPath = docsPath;
        BackColor = Theme.WindowBg;
        _status.Font = UiScale.Font("Segoe UI", 10F);
        _status.ForeColor = Theme.TextSecondary;
        _status.Text = Lang.T("Loading the files...", "Đang tải file...");
        _webView.Visible = false;
        Controls.Add(_webView);
        Controls.Add(_status);
        Theme.Changed += ApplyTheme;
        Lang.Changed += OnLanguageChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= ApplyTheme;
            Lang.Changed -= OnLanguageChanged;
        }
        base.Dispose(disposing);
    }

    // The only way from a page message to a disk path: a pair the host listed itself, never a path the page made up.
    internal static string? FullPath(IReadOnlyDictionary<string, string> roots, IReadOnlySet<(string Group, string Path)> files,
        string group, string path) =>
        files.Contains((group, path)) && roots.TryGetValue(group, out var root)
            ? Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))
            : null;

    // The groups a disk path can belong to, in the order Locate tries them: the docs folder and each worktree's copy of
    // it first, since a worktree's docs\ is gitignored and never listed under the worktree.
    internal static List<(string Group, string Root)> RootsFor(string ticketKey, string? docsPath, IReadOnlyList<TicketWorktree> worktrees)
    {
        var roots = new List<(string Group, string Root)>();
        if (docsPath != null)
        {
            roots.Add((DocsGroup, docsPath));
            foreach (var worktree in worktrees) roots.Add((DocsGroup, Path.Combine(worktree.Path, "docs", ticketKey)));
        }
        foreach (var worktree in worktrees) roots.Add((worktree.Repository, worktree.Path));
        return roots;
    }

    // A disk path as the list's (group, path) pair, from the first root holding it; null outside every root.
    internal static (string Group, string Path)? Locate(string fullPath, IReadOnlyList<(string Group, string Root)> roots)
    {
        foreach (var (group, root) in roots)
        {
            var relative = Path.GetRelativePath(root, fullPath);
            var outside = relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)
                || Path.IsPathRooted(relative);
            if (!outside) return (group, relative.Replace(Path.DirectorySeparatorChar, '/'));
        }
        return null;
    }

    // Answered after the next listing, which the caller's Activate starts: the page opens the file's folders and shows
    // it, or VS Code opens a file the list does not hold (gitignored, or its group failed to list).
    internal void Reveal(string group, string path, string fullPath, int? line)
    {
        _pendingReveal = (group, path, fullPath, line);
    }

    // The docs group lists the dev repo's copy of a worktree's docs (the docs hook mirrors them); a copy that differs
    // must not stand in for the file the user clicked.
    internal static bool ShowsSameContent(string listedPath, string clickedPath)
    {
        if (string.Equals(Path.GetFullPath(listedPath), Path.GetFullPath(clickedPath), StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var listed = new FileInfo(listedPath);
            var clicked = new FileInfo(clickedPath);
            return listed.Exists && clicked.Exists && listed.Length == clicked.Length
                && File.ReadAllBytes(listedPath).AsSpan().SequenceEqual(File.ReadAllBytes(clickedPath));
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The view could not start, so no listing will answer a Ctrl+clicked path: VS Code opens it instead.
    private void OpenPendingRevealInVsCode()
    {
        if (_pendingReveal == null) return;
        var reveal = _pendingReveal.Value;
        _pendingReveal = null;
        TicketLauncher.TryOpenVsCode(reveal.FullPath, reveal.Line);
    }

    internal void Activate()
    {
        if (!_initStarted)
        {
            _initStarted = true;
            // Created on the first Files click, it may not have a handle yet; WebView2 needs one to initialize.
            if (!IsHandleCreated) CreateControl();
            _ = InitializeAsync();
            return;
        }
        _ = RefreshAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await WebViewSetup.InitializeAsync(_webView);
            if (IsDisposed || _webView.CoreWebView2 == null) return;
            var core = _webView.CoreWebView2;
            core.WebMessageReceived += OnWebMessage;
            var fontSize = (int)Math.Round(13 * UiScale.Factor);
            var loading = Uri.EscapeDataString(Lang.T("Reading the files...", "Đang đọc danh sách file..."));
            var settings = AppSettings.Load();
            var listWidth = settings.FilesListWidthEm?.ToString(CultureInfo.InvariantCulture) ?? "";
            core.Navigate(WebViewSetup.PageUrl($"files/index.html?fontSize={fontSize}&loading={loading}&listWidth={listWidth}"
                + $"&listHidden={(settings.FilesListHidden ? 1 : 0)}"
                + $"&dark={(Theme.Dark ? 1 : 0)}&colors={Uri.EscapeDataString(JsonSerializer.Serialize(ThemeColors()))}"));
            _webView.Visible = true;
            _status.Visible = false;
        }
        catch (Exception ex)
        {
            if (IsDisposed) return; // the tab closed while WebView2 was still starting
            AppLogger.Error($"[files] files view init failed: {ex.Message}");
            _status.Text = Lang.T("Could not show the files (WebView2 runtime missing?).",
                "Không hiển thị được file (thiếu WebView2 runtime?).");
            OpenPendingRevealInVsCode();
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
                ApplyTheme();
                PostLabels();
                _ = RefreshAsync();
                break;
            case "layout":
                // One changed setting per message, kept across restarts; a tab opened later starts from it.
                var settings = AppSettings.Load();
                if (root.TryGetProperty("listWidth", out var listWidth)) settings.FilesListWidthEm = listWidth.GetDouble();
                if (root.TryGetProperty("listHidden", out var listHidden)) settings.FilesListHidden = listHidden.GetBoolean();
                AppSettings.Save(settings);
                break;
            case "refresh":
                _ = RefreshAsync();
                break;
            case "select":
                _ = SelectAsync(root.GetProperty("group").GetString() ?? "", root.GetProperty("path").GetString() ?? "");
                break;
            case "image":
                _ = SendImageAsync(root.GetProperty("group").GetString() ?? "", root.GetProperty("path").GetString() ?? "");
                break;
            case "open":
                Open(root.GetProperty("group").GetString() ?? "", root.GetProperty("path").GetString() ?? "",
                    root.GetProperty("target").GetString());
                break;
            case "openUrl":
                // Only a web link leaves the app, whatever the page sends.
                var webUrl = TaskRow.WebUrl(root.GetProperty("url").GetString());
                if (webUrl != null) TaskRow.OpenUrl(webUrl);
                break;
            case "failed":
                AppLogger.Error($"[files] files page failed to start: {root.GetProperty("message").GetString()}");
                _webView.Visible = false;
                _status.Text = Lang.T("Could not show the files (see log).", "Không hiển thị được file (xem log).");
                _status.Visible = true;
                OpenPendingRevealInVsCode();
                break;
            case "page":
                var page = TerminalView.PageForShortcut(root.GetProperty("index").GetInt32());
                if (page != null) PageRequested?.Invoke(page.Value);
                break;
        }
    }

    // The toolbar, the group errors and the file notes follow the language, so a switch relabels and sends the open file again.
    private void OnLanguageChanged()
    {
        PostLabels();
        _shown = null;
        _ = RefreshAsync();
    }

    private void PostLabels()
    {
        if (!_ready || _webView.CoreWebView2 == null) return;
        Post(new
        {
            type = "labels",
            refresh = Lang.T("Refresh", "Làm mới"),
            files = Lang.T("Show or hide the file list", "Ẩn / hiện danh sách file"),
            filter = Lang.T("Filter files", "Lọc file"),
            source = Lang.T("Source", "Mã nguồn"),
            rendered = Lang.T("Rendered", "Bản render"),
            vsCode = Lang.T("VS Code", "VS Code"),
            showInFolder = Lang.T("Show in folder", "Hiện trong thư mục"),
            tooMany = Lang.T("Showing the first 500 matches.", "Chỉ hiện 500 kết quả đầu."),
            noMatch = Lang.T("No file matches.", "Không có file nào khớp."),
        });
    }

    private async Task RefreshAsync()
    {
        if (!_ready) return; // the page asks for the first listing itself once it is up
        if (_loading)
        {
            _refreshAgain = true;
            return;
        }
        _loading = true;
        var busyWork = BusyWork.Shared.Begin(Lang.T($"Listing the files of {_ticketKey}...", $"Đang liệt kê file của {_ticketKey}..."));
        try
        {
            await ListFilesAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[files] listing {_ticketKey} failed: {ex.Message}");
        }
        finally
        {
            _loading = false;
            busyWork.Dispose();
        }
        if (_refreshAgain && !IsDisposed)
        {
            _refreshAgain = false;
            await RefreshAsync();
        }
    }

    private async Task ListFilesAsync()
    {
        // Paired with the results after the await, so it must not change.
        var sources = new List<(string Id, string Label, string Root, Func<List<string>?> ListFiles)>();
        var docsPath = _docsPath();
        if (docsPath != null) sources.Add((DocsGroup, $"Docs - {_ticketKey}", docsPath, () => TicketFiles.ListDocs(docsPath)));
        foreach (var worktree in _worktrees())
        {
            sources.Add((worktree.Repository, $"{worktree.RepoLabel} - {worktree.Repository}", worktree.Path,
                () => TicketFiles.ListWorktree(worktree.Path)));
        }
        var listings = await Task.WhenAll(sources.Select(source => Task.Run(source.ListFiles)));
        if (IsDisposed || _webView.CoreWebView2 == null) return;
        _roots.Clear();
        _files.Clear();
        var groups = new List<object>();
        foreach (var (source, files) in sources.Zip(listings))
        {
            if (files != null)
            {
                _roots[source.Id] = source.Root;
                foreach (var path in files) _files.Add((source.Id, path));
            }
            groups.Add(new
            {
                id = source.Id,
                label = source.Label,
                error = files == null ? Lang.T("listing failed (see log)", "liệt kê lỗi (xem log)") : null,
                files = files ?? [],
            });
        }
        // The page clears its viewer when the open file leaves the list, so a file listed again must be sent again.
        if (_shown != null && !_files.Contains((_shown.Value.Group, _shown.Value.Path))) _shown = null;
        Post(new
        {
            type = "files",
            groups,
            emptyText = Lang.T("No docs or worktree for this ticket.", "Ticket này chưa có docs hay worktree."),
        });
        if (_pendingReveal != null)
        {
            var reveal = _pendingReveal.Value;
            _pendingReveal = null;
            var listedPath = FullPath(_roots, _files, reveal.Group, reveal.Path);
            if (listedPath != null && ShowsSameContent(listedPath, reveal.FullPath))
                Post(new { type = "reveal", group = reveal.Group, path = reveal.Path, line = reveal.Line });
            else TicketLauncher.TryOpenVsCode(reveal.FullPath, reveal.Line);
        }
    }

    private async Task SelectAsync(string group, string path)
    {
        try
        {
            var version = ++_selectVersion;
            var fullPath = FullPath(_roots, _files, group, path);
            if (fullPath == null) return;
            (string Group, string Path, DateTime WriteTimeUtc, long Length)? stamp = null;
            TicketFileContent? file = null;
            try
            {
                var fileInfo = new FileInfo(fullPath);
                stamp = (group, path, fileInfo.LastWriteTimeUtc, fileInfo.Exists ? fileInfo.Length : -1);
                // Every listing asks for the open file again; unchanged on disk, the page keeps it and its scroll.
                if (_shown == stamp) return;
                // The page shows something else from here on (a newer click, or an error), so a later ask must be read.
                _shown = null;
                file = await Task.Run(() => TicketFiles.Read(fullPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLogger.Error($"[files] reading {fullPath} failed: {ex.Message}");
            }
            if (IsDisposed || _webView.CoreWebView2 == null || version != _selectVersion) return;
            Post(new
            {
                type = "file",
                group,
                path,
                kind = file == null ? "error" : file.Kind.ToString().ToLowerInvariant(),
                content = file?.Content,
                note = file == null
                    ? Lang.T("Could not read the file (see log).", "Không đọc được file (xem log).")
                    : file.Kind switch
                    {
                        TicketFileKind.Binary => Lang.T("Binary file: open it from its folder.", "File nhị phân: mở từ thư mục của nó."),
                        TicketFileKind.TooLarge => Lang.T("Too large to show here.", "File quá lớn để hiện ở đây."),
                        _ => null,
                    },
            });
            // Not remembered: a read error (maybe a passing lock), or a file a listing dropped while it was read.
            _shown = file != null && _files.Contains((group, path)) ? stamp : null;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[files] showing {path} failed: {ex.Message}");
        }
    }

    // An image in rendered markdown; it does not touch the open file's state, so it never races a select.
    private async Task SendImageAsync(string group, string path)
    {
        try
        {
            var fullPath = FullPath(_roots, _files, group, path);
            string? dataUrl = null;
            if (fullPath != null)
            {
                try
                {
                    var image = await Task.Run(() => TicketFiles.Read(fullPath));
                    if (image.Kind == TicketFileKind.Image) dataUrl = image.Content;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLogger.Error($"[files] reading {fullPath} failed: {ex.Message}");
                }
            }
            if (IsDisposed || _webView.CoreWebView2 == null) return;
            // null also for a path that was never listed, so the page shows the alt text instead of an empty image.
            Post(new { type = "image", group, path, dataUrl });
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[files] showing image {path} failed: {ex.Message}");
        }
    }

    private void Open(string group, string path, string? target)
    {
        var fullPath = FullPath(_roots, _files, group, path);
        if (fullPath == null) return;
        switch (target)
        {
            case "vscode":
                TicketLauncher.TryOpenVsCode(fullPath);
                break;
            case "folder":
                try
                {
                    using var process = Process.Start("explorer.exe", $"/select,\"{fullPath}\"");
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"[files] Showing {fullPath} in Explorer failed: {ex.Message}");
                }
                break;
        }
    }

    private void Post(object message)
    {
        _webView.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(message));
    }

    private void ApplyTheme()
    {
        BackColor = Theme.WindowBg;
        _status.ForeColor = Theme.TextSecondary;
        if (!_ready || _webView.CoreWebView2 == null) return;
        Post(new { type = "theme", dark = Theme.Dark, colors = ThemeColors() });
    }

    // Also put on the page's URL, so its first paint has these colors: the theme message only follows Monaco's load.
    private static object ThemeColors() => new
    {
        bg = ColorTranslator.ToHtml(Theme.WindowBg),
        fg = ColorTranslator.ToHtml(Theme.TextPrimary),
        muted = ColorTranslator.ToHtml(Theme.TextSecondary),
        line = ColorTranslator.ToHtml(Theme.Divider),
        selection = Theme.Dark ? "#3a5a8c" : "#b4d2ff",
        link = ColorTranslator.ToHtml(Theme.Accent),
    };
}
