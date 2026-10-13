using System.Drawing;
using System.Globalization;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NoisLogTray;

// The ticket's branch changes across its worktrees, diffed by Monaco. Lists again on every Activate / Refresh.
internal sealed class ChangesView : UserControl
{
    private readonly string _ticketKey;
    private readonly Func<IReadOnlyList<TicketWorktree>> _worktrees;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Dictionary<string, (string WorktreePath, ChangeSet Set)> _changeSets = new();
    private readonly Dictionary<(string Repository, string Path), FileChange> _files = new();
    private bool _initStarted;
    private bool _ready;
    private bool _loading;
    private bool _refreshAgain;
    private int _selectVersion;

    // Ctrl+digit typed inside the diff page (WebView2 swallows them before the form's ProcessCmdKey).
    internal event Action<ShellPage>? PageRequested;

    internal ChangesView(string ticketKey, Func<IReadOnlyList<TicketWorktree>> worktrees)
    {
        _ticketKey = ticketKey;
        _worktrees = worktrees;
        BackColor = Theme.WindowBg;
        _status.Font = UiScale.Font("Segoe UI", 10F);
        _status.ForeColor = Theme.TextSecondary;
        _status.Text = Lang.T("Loading the changes...", "Đang tải thay đổi...");
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

    internal void Activate()
    {
        if (!_initStarted)
        {
            _initStarted = true;
            // Created on the first Changes click, it may not have a handle yet; WebView2 needs one to initialize.
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
            var loading = Uri.EscapeDataString(Lang.T("Reading the changes...", "Đang đọc thay đổi..."));
            var settings = AppSettings.Load();
            var listWidth = settings.DiffListWidthEm?.ToString(CultureInfo.InvariantCulture) ?? "";
            core.Navigate(WebViewSetup.PageUrl($"diff/index.html?fontSize={fontSize}&loading={loading}&listWidth={listWidth}"
                + $"&listHidden={(settings.DiffListHidden ? 1 : 0)}&inline={(settings.DiffInline ? 1 : 0)}"
                + $"&dark={(Theme.Dark ? 1 : 0)}&colors={Uri.EscapeDataString(JsonSerializer.Serialize(ThemeColors()))}"));
            _webView.Visible = true;
            _status.Visible = false;
        }
        catch (Exception ex)
        {
            if (IsDisposed) return; // the tab closed while WebView2 was still starting
            AppLogger.Error($"[changes] diff view init failed: {ex.Message}");
            _status.Text = Lang.T("Could not show the changes (WebView2 runtime missing?).",
                "Không hiển thị được thay đổi (thiếu WebView2 runtime?).");
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
                if (root.TryGetProperty("listWidth", out var listWidth)) settings.DiffListWidthEm = listWidth.GetDouble();
                if (root.TryGetProperty("listHidden", out var listHidden)) settings.DiffListHidden = listHidden.GetBoolean();
                if (root.TryGetProperty("inline", out var inline)) settings.DiffInline = inline.GetBoolean();
                AppSettings.Save(settings);
                break;
            case "refresh":
                _ = RefreshAsync();
                break;
            case "select":
                _ = SelectAsync(root.GetProperty("repository").GetString() ?? "", root.GetProperty("path").GetString() ?? "");
                break;
            case "failed":
                AppLogger.Error($"[changes] diff page failed to start: {root.GetProperty("message").GetString()}");
                _webView.Visible = false;
                _status.Text = Lang.T("Could not show the changes (see log).", "Không hiển thị được thay đổi (xem log).");
                _status.Visible = true;
                break;
            case "page":
                var page = TerminalView.PageForShortcut(root.GetProperty("index").GetInt32());
                if (page != null) PageRequested?.Invoke(page.Value);
                break;
        }
    }

    // The strings in the toolbar and the file list follow the language, so a switch relabels and lists again.
    private void OnLanguageChanged()
    {
        PostLabels();
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
            inline = Lang.T("Inline", "Gộp 1 cột"),
            inlineTitle = Lang.T("One column instead of old | new side by side", "Một cột thay vì cũ | mới hai bên"),
            previousChange = Lang.T("Previous change (Shift+F7)", "Thay đổi trước (Shift+F7)"),
            nextChange = Lang.T("Next change (F7)", "Thay đổi kế (F7)"),
            previousFile = Lang.T("Previous file (Alt+Up)", "File trước (Alt+↑)"),
            nextFile = Lang.T("Next file (Alt+Down)", "File kế (Alt+↓)"),
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
        var busyWork = BusyWork.Shared.Begin(Lang.T($"Listing the changes of {_ticketKey}...", $"Đang liệt kê thay đổi của {_ticketKey}..."));
        try
        {
            await ListChangesAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[changes] listing {_ticketKey} failed: {ex.Message}");
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

    private async Task ListChangesAsync()
    {
        var worktrees = _worktrees().ToList(); // paired with the results after the await, so it must not change
        var changeSets = await Task.WhenAll(worktrees.Select(tw => Task.Run(() => GitChanges.List(tw.Path, _ticketKey))));
        if (IsDisposed || _webView.CoreWebView2 == null) return;
        _changeSets.Clear();
        _files.Clear();
        var groups = new List<object>();
        foreach (var (worktree, changeSet) in worktrees.Zip(changeSets))
        {
            if (changeSet != null)
            {
                _changeSets[worktree.Repository] = (worktree.Path, changeSet);
                foreach (var file in changeSet.Files) _files[(worktree.Repository, file.Path)] = file;
            }
            groups.Add(new
            {
                label = $"{worktree.RepoLabel} - {worktree.Repository}",
                repository = worktree.Repository,
                error = changeSet == null ? Lang.T("git failed (see log)", "git lỗi (xem log)") : null,
                files = changeSet == null ? [] : changeSet.Files.Select(fc => new
                {
                    path = fc.Path,
                    status = fc.Status switch
                    {
                        FileChangeStatus.Added => "A",
                        FileChangeStatus.Deleted => "D",
                        _ => "M",
                    },
                }).ToList(),
            });
        }
        Post(new
        {
            type = "files",
            groups,
            emptyText = Lang.T("No worktree for this ticket.", "Ticket này chưa có worktree."),
        });
    }

    private async Task SelectAsync(string repository, string path)
    {
        try
        {
            var version = ++_selectVersion;
            if (!_changeSets.TryGetValue(repository, out var entry) || !_files.TryGetValue((repository, path), out var file)) return;
            var changeSet = entry.Set;
            var worktreePath = entry.WorktreePath;
            var placeholder = Lang.T("(binary or too large to show)", "(file nhị phân hoặc quá lớn)");
            string? original;
            string? modified;
            try
            {
                // git returns LF for a CRLF working file under core.autocrlf, so compare both sides as LF.
                (original, modified) = await Task.Run(() => (
                    GitChanges.ReadOriginal(worktreePath, changeSet, file)?.Replace("\r\n", "\n"),
                    GitChanges.ReadModified(worktreePath, changeSet, file)?.Replace("\r\n", "\n")));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLogger.Error($"[changes] reading {path} in {worktreePath} failed: {ex.Message}");
                original = Lang.T("(could not read the file, see log)", "(không đọc được file, xem log)");
                modified = original;
            }
            if (IsDisposed || _webView.CoreWebView2 == null || version != _selectVersion) return;
            Post(new { type = "diff", repository, path, original = original ?? placeholder, modified = modified ?? placeholder });
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[changes] showing {path} failed: {ex.Message}");
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
    };
}
