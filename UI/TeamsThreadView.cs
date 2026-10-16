using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NoisLogTray;

// The ticket tab's Teams view: the conversation and Claude's analysis, or the draft while the channel has none; reads again after 60s, 5s while searching.
internal sealed partial class TeamsThreadView : UserControl
{
    private const int MaxAvatarNameChars = 200;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReadMaxAge = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StillSearchingReadDelay = TimeSpan.FromSeconds(5);

    private readonly string _ticketKey;
    private readonly Func<string?, bool, CancellationToken, Task<TeamsThreadResult>> _read; // chat id (null = the post), quiet: no busy work, no log line
    private readonly Func<CancellationToken, Task<TeamsThreadResult>> _waitForPost;
    private readonly Func<TeamsThread, string, string?, bool, CancellationToken, Task<(TeamsAnalysis? Analysis, string? Error)>> _suggest;
    private readonly Func<TeamsThread, TeamsDraft, CancellationToken, Task<TeamsReplyResult>> _sendReply;
    private readonly Func<string, TeamsDraft, CancellationToken, Task<TeamsReplyResult>> _post;
    private readonly Func<TeamsThread, string, string?, TeamsDraft, CancellationToken, Task<TeamsReplyResult>> _edit;
    private readonly Func<TeamsThread, string, CancellationToken, Task<TeamsReplyResult>> _delete;
    private readonly Func<TeamsThread, string, CancellationToken, Task<string?>> _handToClaude; // null result: handed over, else the error
    private readonly Func<(string Draft, DateTimeOffset? PostedAt)> _loadDraft;
    private readonly Func<string, CancellationToken, Task<string?>> _loadImage;
    private readonly Func<string, string, CancellationToken, Task<string?>> _loadAvatar;
    private readonly Func<Task<bool>> _signIn;
    private readonly Action<string> _selectChannel;
    private readonly Action<string, string?, TeamsMessage?, bool> _commentAskSeen; // conversation key, chat id, the ask or null, last message is mine
    private readonly Func<string, string, CancellationToken, Task<string?>> _handCommentsToClaude; // conversation key, prompt; null result: handed over, else the error
    private readonly Action<string, string> _commentFlowAction; // conversation key, "done" or "dismiss"
    private readonly CancellationTokenSource _readCancel = new();
    private readonly CancellationTokenSource _waitCancel = new();
    private readonly CancellationTokenSource _changeCancel = new(); // replies, posts, edits and deletes
    private readonly CancellationTokenSource _imageCancel = new();
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = (int)PollInterval.TotalMilliseconds };
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    private CancellationTokenSource? _suggestCancel; // the running suggestion's; each run disposes its own
    private string? _suggestingMessageId; // the last message of the thread the running suggestion answers
    private string? _chatId; // null: not chosen yet, "": the channel post, else the chat's id
    private IReadOnlyList<TeamsGroupChat>? _lastChats; // the last list the service gave; null until one was read
    private TeamsThreadResult? _found; // the thread on the page; null while the draft or a state (signed out, failed...) shows
    private TeamsThreadResult? _notPosted; // the result the draft shows; null while the thread does
    private DateTime? _lastReadAtUtc;
    private bool _initStarted;
    private bool _reading;
    private bool _readAgain;
    private bool _waiting;
    private bool _signingIn;
    private bool _stillSearchingReadPending;

    // After every read: true while the thread's last message is someone else's.
    internal event Action<bool>? NeedsReplyChanged;

    // Ctrl+digit typed inside the page (WebView2 swallows them before the form's ProcessCmdKey).
    internal event Action<ShellPage>? PageRequested;

    // A post went up; TrayApp records when.
    internal event Action<TeamsDraft>? Posted;

    // The conversation changed, by the user or by itself: "" is the channel post, else a chat's id.
    internal event Action<string?>? ConversationChanged;

    // The thread was applied while on screen in the foreground window: its key and its newest message's time.
    internal event Action<string, DateTimeOffset>? ConversationSeen;

    internal TeamsThreadView(string ticketKey,
        Func<string?, bool, CancellationToken, Task<TeamsThreadResult>> read,
        Func<CancellationToken, Task<TeamsThreadResult>> waitForPost,
        Func<TeamsThread, string, string?, bool, CancellationToken, Task<(TeamsAnalysis? Analysis, string? Error)>> suggest,
        Func<TeamsThread, TeamsDraft, CancellationToken, Task<TeamsReplyResult>> sendReply,
        Func<string, TeamsDraft, CancellationToken, Task<TeamsReplyResult>> post,
        Func<TeamsThread, string, string?, TeamsDraft, CancellationToken, Task<TeamsReplyResult>> edit,
        Func<TeamsThread, string, CancellationToken, Task<TeamsReplyResult>> delete,
        Func<TeamsThread, string, CancellationToken, Task<string?>> handToClaude,
        Func<(string Draft, DateTimeOffset? PostedAt)> loadDraft,
        Func<string, CancellationToken, Task<string?>> loadImage,
        Func<string, string, CancellationToken, Task<string?>> loadAvatar,
        Func<Task<bool>> signIn,
        Action<string> selectChannel,
        string? initialChatId,
        Action<string, string?, TeamsMessage?, bool> commentAskSeen,
        Func<string, string, CancellationToken, Task<string?>> handCommentsToClaude,
        Action<string, string> commentFlowAction)
    {
        _ticketKey = ticketKey;
        _read = read;
        _waitForPost = waitForPost;
        _suggest = suggest;
        _sendReply = sendReply;
        _post = post;
        _edit = edit;
        _delete = delete;
        _handToClaude = handToClaude;
        _loadDraft = loadDraft;
        _loadImage = loadImage;
        _loadAvatar = loadAvatar;
        _signIn = signIn;
        _selectChannel = selectChannel;
        _chatId = initialChatId;
        _commentAskSeen = commentAskSeen;
        _handCommentsToClaude = handCommentsToClaude;
        _commentFlowAction = commentFlowAction;
        BackColor = Theme.WindowBg;
        _status.Font = UiScale.Font("Segoe UI", 10F);
        _status.ForeColor = Theme.TextSecondary;
        _status.Text = Lang.T("Loading the Teams thread...", "Đang tải thread Teams...");
        _webView.Visible = false;
        Controls.Add(_webView);
        Controls.Add(_status);
        _pollTimer.Tick += OnPollTick;
        Theme.Changed += ApplyTheme;
        Lang.Changed += OnLanguageChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= ApplyTheme;
            Lang.Changed -= OnLanguageChanged;
            _pollTimer.Dispose();
            // A read, wait, suggestion, change or image load still running ends now; each continuation checks IsDisposed.
            _readCancel.Cancel();
            _readCancel.Dispose();
            _waitCancel.Cancel();
            _waitCancel.Dispose();
            _changeCancel.Cancel();
            _changeCancel.Dispose();
            _imageCancel.Cancel();
            _imageCancel.Dispose();
            _suggestCancel?.Cancel();
        }
        base.Dispose(disposing);
    }

    internal void Activate()
    {
        if (!_initStarted)
        {
            _initStarted = true;
            // Created on the first Teams click, it may not have a handle yet; WebView2 needs one to initialize.
            if (!IsHandleCreated)
            {
                CreateControl();
            }
            _ = InitializeAsync();
        }

        _pollTimer.Enabled = Visible;
        var isStale = _lastReadAtUtc == null || DateTime.UtcNow - _lastReadAtUtc.Value > ReadMaxAge;
        if (!_reading && isStale)
        {
            _ = ReadAsync();
        }
        else if (_notPosted != null)
        {
            // Shown again: an untouched draft follows the file, which Claude may have rewritten.
            PostDraft(false);
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _pollTimer.Enabled = Visible && _initStarted;
    }

    // Every PollInterval while the thread is on screen in the foreground window, and nothing else is going on.
    private void OnPollTick(object? sender, EventArgs e)
    {
        var isBusy = _reading || _sending || _posting || _changingMessage || _waiting;
        if (ShouldPoll(Visible, IsWindowForeground(), _found != null, isBusy))
        {
            _ = ReadAsync(true);
        }
    }

    private bool IsWindowForeground()
    {
        var form = FindForm();
        return form is ShellForm shell ? shell.IsForeground : form != null && Form.ActiveForm == form;
    }

    internal static bool ShouldPoll(bool isVisible, bool isForeground, bool isThreadShown, bool isBusy)
    {
        return isVisible && isForeground && isThreadShown && !isBusy;
    }

    // Equal when nothing a reader sees changed: same count, ids and edit times.
    internal static bool HasSameMessages(TeamsThread? previous, TeamsThread next)
    {
        if (previous == null || previous.Messages.Count != next.Messages.Count)
        {
            return false;
        }
        for (var messageIndex = 0; messageIndex < next.Messages.Count; messageIndex++)
        {
            var oldMessage = previous.Messages[messageIndex];
            var newMessage = next.Messages[messageIndex];
            if (oldMessage.Id != newMessage.Id || oldMessage.EditedAt != newMessage.EditedAt
                || ReactionsText(oldMessage) != ReactionsText(newMessage))
            {
                return false;
            }
        }
        return true;
    }

    // A reaction changes no edit time, so the quiet read compares them on their own.
    private static string ReactionsText(TeamsMessage message)
    {
        if (message.Reactions == null)
        {
            return "";
        }
        return string.Join(";", message.Reactions.Select(tr => tr.Key + "=" + string.Join(",", tr.UserMris)));
    }

    private async Task InitializeAsync()
    {
        try
        {
            await WebViewSetup.InitializeAsync(_webView);
            if (IsDisposed || _webView.CoreWebView2 == null)
            {
                return;
            }
            var core = _webView.CoreWebView2;
            core.WebMessageReceived += OnWebMessage;
            core.Navigate(WebViewSetup.PageUrl($"teams/index.html?dark={(Theme.Dark ? 1 : 0)}"
                + $"&colors={Uri.EscapeDataString(JsonSerializer.Serialize(ThemeColors()))}"));
            _webView.Visible = true;
            _status.Visible = false;
        }
        catch (Exception ex)
        {
            if (IsDisposed)
            {
                return; // the tab closed while WebView2 was still starting
            }
            AppLogger.Error($"[teams] thread view init failed: {ex.Message}");
            _status.Text = Lang.T("Could not show the Teams thread (WebView2 runtime missing?).",
                "Không hiển thị được thread Teams (thiếu WebView2 runtime?).");
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var message = JsonDocument.Parse(e.TryGetWebMessageAsString());
        HandleMessage(message.RootElement);
    }

    internal void HandleMessage(JsonElement root)
    {
        switch (GetString(root, "type"))
        {
            case "ready":
                // Everything shown so far was kept for this moment, the newest of each kind.
                _ready = true;
                ApplyTheme();
                PostLabels();
                PostKeptMessages();
                break;
            case "refresh":
                _ = ReadAsync();
                break;
            case "signIn":
                _ = SignInAsync();
                break;
            case "selectChannel":
                var channelId = GetString(root, "channelId");
                if (!string.IsNullOrEmpty(channelId))
                {
                    _selectChannel(channelId);
                    _ = ReadAsync();
                }
                break;
            case "selectConversation":
                SelectConversation(GetString(root, "chatId") ?? "");
                break;
            case "reloadDraft":
                PostDraft(true);
                break;
            case "suggest":
                var mainPoint = GetString(root, "mainPoint")?.Trim();
                _ = SuggestAsync(string.IsNullOrEmpty(mainPoint) ? null : mainPoint, force: true);
                break;
            case "send":
                var (_, replyDraft, replyError) = ParseDraft(root);
                if (replyDraft == null)
                {
                    ShowSendState("failed", replyError);
                }
                else
                {
                    _ = SendAsync(replyDraft);
                }
                break;
            case "post":
                var (subject, postDraft, postError) = ParseDraft(root);
                if (postDraft == null)
                {
                    ShowPostState("failed", postError);
                }
                else
                {
                    _ = PostToTeamsAsync(subject ?? "", postDraft);
                }
                break;
            case "edit":
                var editMessageId = GetString(root, "messageId") ?? "";
                var (editSubject, editDraft, editError) = ParseDraft(root);
                if (editDraft == null)
                {
                    PostChangeState("editState", editMessageId, "failed", editError);
                }
                else
                {
                    _ = EditAsync(editMessageId, editSubject, editDraft);
                }
                break;
            case "delete":
                var deleteMessageId = GetString(root, "messageId");
                if (!string.IsNullOrEmpty(deleteMessageId) && IsHandleCreated)
                {
                    // Not inside WebView2's message callback: a modal dialog there would re-enter it.
                    BeginInvoke(() => ConfirmDelete(deleteMessageId));
                }
                break;
            case "handToClaude":
                _ = HandToClaudeAsync(GetString(root, "messageId") ?? "");
                break;
            case "handCommentsToClaude":
                var handKey = GetString(root, "conversationKey");
                if (handKey != null && handKey == ShownConversationKey())
                {
                    _ = HandCommentsToClaudeAsync(handKey, GetString(root, "prompt") ?? "");
                }
                break;
            case "commentFlowDone":
            case "dismissCommentFlow":
                var flowKey = GetString(root, "conversationKey");
                if (flowKey != null && flowKey == ShownConversationKey())
                {
                    _commentFlowAction(flowKey, GetString(root, "type") == "commentFlowDone" ? "done" : "dismiss");
                }
                break;
            case "image":
                _ = LoadImageAsync(GetString(root, "url") ?? "");
                break;
            case "avatar":
                _ = AnswerAvatarAsync(GetString(root, "mri") ?? "", GetString(root, "name") ?? "");
                break;
            case "openUrl":
                // Only a web link leaves the app, whatever a message in the thread links to.
                var webUrl = TaskRow.WebUrl(GetString(root, "url"));
                if (webUrl != null)
                {
                    TaskRow.OpenUrl(webUrl);
                }
                break;
            case "page":
                var page = TerminalView.PageForShortcut(GetInt(root, "digit"));
                if (page != null)
                {
                    PageRequested?.Invoke(page.Value);
                }
                break;
        }
    }

    // Found shows the thread, NotPosted the draft; a failed read while drafting is only a line above the draft.
    internal void ApplyResult(TeamsThreadResult result)
    {
        var needsReply = result.State == TeamsThreadState.Found && result.Thread != null
            && TeamsChat.NeedsReply(result.Thread.Messages, result.MyId);
        NeedsReplyChanged?.Invoke(needsReply);
        if (result.StillSearching)
        {
            _ = ReadAgainWhileSearchingAsync();
        }
        // Null when the read gave none (failed, signed out), so the page keeps the last list.
        if (result.People != null)
        {
            Keep(BuildPeopleMessage(result.People));
        }

        if (UpdateConversation(result))
        {
            return;
        }

        if (result.State == TeamsThreadState.Found && result.Thread != null)
        {
            ShowThread(result);
            var thread = result.Thread;
            var lastIsMine = thread.Messages.Count != 0
                && string.Equals(thread.Messages[thread.Messages.Count - 1].SenderId, result.MyId, StringComparison.Ordinal);
            _commentAskSeen(TeamsChat.ConversationKey(thread), _chatId, TeamsReply.CommentAsk(thread, result.MyId), lastIsMine);
            return;
        }

        _found = null;
        Forget("thread");
        if (result.State == TeamsThreadState.NotPosted)
        {
            _notPosted = result;
            ClearState();
            PostDraft(false);
            return;
        }
        if (result.State == TeamsThreadState.Failed && _notPosted != null)
        {
            AppLogger.Info($"[teams] {_ticketKey}: background read failed while drafting: {result.Error}");
            ShowState("failed", result.Error, true);
            return;
        }
        ShowState(result.State == TeamsThreadState.SignedOut ? "signedOut" : "failed", result.Error, false);
    }

    // True when the choice changed and a read for the new one is on its way, so this result is not shown.
    private bool UpdateConversation(TeamsThreadResult result)
    {
        if (result.Chats == null)
        {
            return false;
        }
        _lastChats = result.Chats;
        if (result.Chats.Count == 0)
        {
            Forget("conversations");
        }
        else
        {
            Keep(BuildConversationsMessage(result.Chats, _chatId));
        }

        if (!string.IsNullOrEmpty(_chatId) && !result.Chats.Any(gc => gc.Id == _chatId))
        {
            AppLogger.Info($"[teams] {_ticketKey}: the chosen chat is gone, back to the post");
            ChooseConversation("");
            _ = ReadAsync();
            return true;
        }
        if (_chatId != null)
        {
            return false;
        }
        if (result.State == TeamsThreadState.Found)
        {
            ChooseConversation("");
            return false;
        }
        if (result.State == TeamsThreadState.NotPosted && result.Chats.Count != 0)
        {
            ChooseConversation(result.Chats[0].Id);
            _ = ReadAsync();
            return true;
        }
        return false;
    }

    private void ChooseConversation(string chatId)
    {
        _chatId = chatId;
        ConversationChanged?.Invoke(chatId);
        if (_lastChats != null && _lastChats.Count != 0)
        {
            Keep(BuildConversationsMessage(_lastChats, chatId));
        }
    }

    internal void SelectConversation(string chatId)
    {
        var isKnown = chatId.Length == 0 || (_lastChats != null && _lastChats.Any(gc => gc.Id == chatId));
        if (!isKnown)
        {
            AppLogger.Info($"[teams] {_ticketKey}: the page chose a chat that is not in the list");
            return;
        }
        if (chatId == _chatId)
        {
            return;
        }

        _suggestCancel?.Cancel();
        Keep(new JsonObject { ["type"] = "analysis", ["kind"] = "none" });
        _found = null;
        _notPosted = null;
        Forget("thread");
        SetCommentFlow(null);
        ChooseConversation(chatId);
        _ = ReadAsync();
    }

    private void ShowThread(TeamsThreadResult result)
    {
        _found = result;
        _notPosted = null;
        Forget("draft");
        Forget("postState");
        ClearState();
        Keep(BuildThreadMessage(result));
        var messages = result.Thread!.Messages;
        if (messages.Count != 0 && Visible && IsWindowForeground())
        {
            ConversationSeen?.Invoke(TeamsChat.ConversationKey(result.Thread), messages[messages.Count - 1].SentAt);
        }
        if (!TeamsChat.NeedsReply(messages, result.MyId))
        {
            _suggestCancel?.Cancel(); // the user has answered, so a suggestion still running is moot
            return;
        }
        // A re-read must not restart Claude on the same question, or an Activate every minute would never let it finish.
        if (_suggestCancel != null && _suggestingMessageId == messages[messages.Count - 1].Id)
        {
            return;
        }
        _ = SuggestAsync(null, force: false);
    }

    private void PostDraft(bool reload)
    {
        if (_notPosted == null)
        {
            return;
        }
        var (draft, postedAt) = _loadDraft();
        Keep(BuildDraftMessage(_ticketKey, _notPosted, draft, postedAt, reload));
    }

    private async Task ReadAsync(bool quiet = false)
    {
        if (IsDisposed || _waiting)
        {
            return; // the wait for a new post reads on its own
        }
        if (_reading)
        {
            _readAgain = true;
            return;
        }

        _reading = true;
        var readChatId = _chatId;
        if (!quiet)
        {
            ShowState("loading", null, false);
        }
        TeamsThreadResult result;
        try
        {
            result = await _read(string.IsNullOrEmpty(readChatId) ? null : readChatId, quiet, _readCancel.Token);
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
            return; // the tab closed while reading
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[teams] reading {_ticketKey} failed: {ex.Message}");
            result = FailedResult(ex.Message);
        }
        finally
        {
            _reading = false;
        }

        if (IsDisposed)
        {
            return;
        }
        _lastReadAtUtc = DateTime.UtcNow;
        // A read from before the post went up says NotPosted, which would send the page back to the draft.
        // A read of the conversation the user just left is dropped; the one for the new choice follows.
        if (!_waiting && readChatId == _chatId && !IsQuietReadUnchanged(quiet, result))
        {
            ApplyResult(result);
        }
        if (_readAgain)
        {
            _readAgain = false;
            await ReadAsync();
        }
    }

    // A quiet read changes the page only for new messages or a lost thread; a failed one keeps the thread on screen.
    private bool IsQuietReadUnchanged(bool quiet, TeamsThreadResult result)
    {
        if (!quiet)
        {
            return false;
        }
        if (result.State == TeamsThreadState.Failed)
        {
            return true;
        }
        if (result.State != TeamsThreadState.Found || result.Thread == null)
        {
            return false;
        }
        if (_found != null && HasSameMessages(_found.Thread, result.Thread))
        {
            return true;
        }
        AppLogger.Info($"[teams] {_ticketKey}: the thread changed, {result.Thread.Messages.Count} message(s)");
        return false;
    }

    // The service scans a channel's older pages a few at a time, so a search still under way reads again soon, not after 60s.
    private async Task ReadAgainWhileSearchingAsync()
    {
        if (_stillSearchingReadPending)
        {
            return;
        }

        _stillSearchingReadPending = true;
        try
        {
            await Task.Delay(StillSearchingReadDelay, _readCancel.Token);
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
            return; // the tab closed while waiting
        }
        finally
        {
            _stillSearchingReadPending = false;
        }

        if (IsDisposed)
        {
            return;
        }
        await ReadAsync();
    }

    // One read: the service saved the post's location when it went up.
    private async Task WaitForPostAsync()
    {
        if (_waiting || IsDisposed)
        {
            return;
        }

        _waiting = true;
        ShowState("waitingPost", null, false);
        TeamsThreadResult result;
        try
        {
            result = await _waitForPost(_waitCancel.Token);
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
            return; // the tab closed while waiting
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[teams] waiting for the {_ticketKey} post failed: {ex.Message}");
            result = FailedResult(ex.Message);
        }
        finally
        {
            _waiting = false;
        }

        if (IsDisposed)
        {
            return;
        }
        _lastReadAtUtc = DateTime.UtcNow;
        ApplyResult(result);
    }

    private async Task SuggestAsync(string? mainPoint, bool force)
    {
        var found = _found;
        if (IsDisposed || found == null || found.Thread == null)
        {
            return;
        }

        _suggestCancel?.Cancel();
        var suggestCancel = new CancellationTokenSource();
        _suggestCancel = suggestCancel;
        var messages = found.Thread.Messages;
        _suggestingMessageId = messages.Count == 0 ? null : messages[messages.Count - 1].Id;
        Keep(new JsonObject { ["type"] = "analysis", ["kind"] = "running" });
        try
        {
            var (analysis, error) = await _suggest(found.Thread, found.MyId, mainPoint, force, suggestCancel.Token);
            if (IsDisposed || suggestCancel.IsCancellationRequested)
            {
                return;
            }
            Keep(analysis != null
                ? BuildAnalysisMessage(analysis)
                : new JsonObject { ["type"] = "analysis", ["kind"] = "failed", ["error"] = error });
        }
        catch (OperationCanceledException) when (suggestCancel.IsCancellationRequested)
        {
            // Expected: a newer run, the user's own reply or a closed tab replaced this one, and owns the panel now.
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[teams] analyzing the conversation of {_ticketKey} failed: {ex.Message}");
            if (!IsDisposed)
            {
                Keep(new JsonObject
                {
                    ["type"] = "analysis",
                    ["kind"] = "failed",
                    ["error"] = Lang.T("Could not get the analysis (see log).", "Không lấy được phân tích (xem log)."),
                });
            }
        }
        finally
        {
            if (_suggestCancel == suggestCancel)
            {
                _suggestCancel = null;
            }
            suggestCancel.Dispose();
        }
    }

    // Always answers, so the page never keeps an image pending; null puts the placeholder in its place.
    private async Task LoadImageAsync(string url)
    {
        string? dataUrl = null;
        if (TeamsChat.IsAmsImageUrl(url))
        {
            try
            {
                dataUrl = await _loadImage(url, _imageCancel.Token);
            }
            catch (OperationCanceledException) when (IsDisposed)
            {
                return; // the tab closed while loading
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[teams] loading an image on {_ticketKey} failed: {ex.Message}");
            }
        }

        if (IsDisposed)
        {
            return;
        }
        Post(new JsonObject { ["type"] = "image", ["url"] = url, ["dataUrl"] = dataUrl });
    }

    // Always answers, kept off the "image" path: an avatar is asked for by MRI, not by an AMS url.
    private async Task AnswerAvatarAsync(string mri, string name)
    {
        string? dataUrl = null;
        try
        {
            dataUrl = await LoadAvatarAsync(mri, name, _imageCancel.Token);
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
            return; // the tab closed while loading
        }

        if (IsDisposed)
        {
            return;
        }
        Post(new JsonObject { ["type"] = "avatar", ["mri"] = mri, ["dataUrl"] = dataUrl });
    }

    // A bad MRI never reaches the loader; any failure of the loader is only "no picture".
    internal async Task<string?> LoadAvatarAsync(string mri, string name, CancellationToken cancellationToken)
    {
        if (!TeamsCompose.IsPersonMri(mri))
        {
            return null;
        }
        var shortName = name.Length > MaxAvatarNameChars ? name.Substring(0, MaxAvatarNameChars) : name;
        try
        {
            return await _loadAvatar(mri, shortName, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[teams] loading an avatar on {_ticketKey} failed: {ex.Message}");
            return null;
        }
    }

    private async Task SignInAsync()
    {
        if (_signingIn || IsDisposed)
        {
            return;
        }

        _signingIn = true;
        bool signedIn;
        try
        {
            signedIn = await _signIn();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[teams] signing in to Teams failed: {ex.Message}");
            signedIn = false;
        }
        finally
        {
            _signingIn = false;
        }

        if (IsDisposed)
        {
            return;
        }
        if (signedIn)
        {
            _ = ReadAsync();
            return;
        }
        ShowState("signedOut", Lang.T("Sign-in did not finish.", "Chưa đăng nhập xong."), false);
    }

    private static TeamsThreadResult FailedResult(string error)
    {
        return new TeamsThreadResult(TeamsThreadState.Failed, null, "", Array.Empty<TeamsChannel>(), null, error);
    }
}
