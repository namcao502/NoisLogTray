namespace NoisLogTray;

// The ticket tab's Teams view wiring: thread, analysis, edits and deletes, all through Teams' chat API on the NOIS profile.
internal sealed partial class TrayApp
{
    // (ticket key, conversation key) -> the last message the analysis answers, and it; only a run that gave one is kept.
    private readonly Dictionary<(string TicketKey, string ConversationKey), (string LastMessageId, TeamsAnalysis Analysis)> _teamsAnalyses = new();

    // Ticket key -> the conversation last shown in the ticket's Teams view this session: "" the channel post, else a chat's id.
    private readonly Dictionary<string, string> _teamsConversations = new();
    private readonly Dictionary<string, string> _teamsTabsToOpen = new(); // tabs a Teams notice opens, which start on Teams

    private TeamsThreadView CreateTeamsThreadView(string ticketKey)
    {
        // The wait after a post is one read: the service saved the post's location when it went up.
        var view = new TeamsThreadView(ticketKey,
            (chatId, quiet, cancellationToken) => ReadTeamsThreadAsync(ticketKey, chatId, quiet, cancellationToken),
            cancellationToken => ReadTeamsThreadAsync(ticketKey, null, false, cancellationToken),
            (teamsThread, myId, mainPoint, force, cancellationToken) =>
                AnalyzeTeamsConversationAsync(ticketKey, teamsThread, myId, mainPoint, force, cancellationToken),
            (teamsThread, draft, cancellationToken) => SendTeamsReplyAsync(ticketKey, teamsThread, draft, cancellationToken),
            (subject, draft, cancellationToken) => PostTeamsAnnouncementAsync(ticketKey, subject, draft, cancellationToken),
            (teamsThread, messageId, subject, draft, cancellationToken) =>
                EditTeamsMessageAsync(ticketKey, teamsThread, messageId, subject, draft, cancellationToken),
            (teamsThread, messageId, cancellationToken) => DeleteTeamsMessageAsync(ticketKey, teamsThread, messageId, cancellationToken),
            (teamsThread, messageId, cancellationToken) => HandToClaudeAsync(ticketKey, teamsThread, messageId, cancellationToken),
            () => LoadTeamsDraft(ticketKey),
            (url, cancellationToken) => _teamsThreads.GetImageDataUrlAsync(url, cancellationToken),
            (mri, displayName, cancellationToken) => _teamsThreads.GetAvatarDataUrlAsync(mri, displayName, cancellationToken),
            SignInToTeamsAsync,
            channelId => _teamsThreads.SelectChannel(channelId),
            _teamsConversations.GetValueOrDefault(ticketKey),
            (conversationKey, chatId, ask, lastIsMine) => OnCommentAskSeen(ticketKey, conversationKey, chatId, ask, lastIsMine),
            (conversationKey, prompt, cancellationToken) => HandCommentsToClaudeAsync(ticketKey, conversationKey, prompt, cancellationToken),
            (conversationKey, action) =>
            {
                if (action == "done")
                {
                    MarkCommentFlowDone(ticketKey, conversationKey);
                }
                else
                {
                    DismissCommentFlow(ticketKey, conversationKey);
                }
            });
        view.Posted += draft => RecordTeamsPost(ticketKey, draft);
        view.ConversationSeen += MarkTeamsSeen;
        view.ConversationChanged += chatId => _teamsConversations[ticketKey] = chatId ?? "";
        return view;
    }

    // A quiet read (the 10s refresh) is neither busy work nor a log line.
    private async Task<TeamsThreadResult> ReadTeamsThreadAsync(string ticketKey, string? chatId, bool quiet, CancellationToken cancellationToken)
    {
        TeamsThreadResult result;
        if (quiet)
        {
            result = await _teamsThreads.ReadAsync(ticketKey, chatId, cancellationToken, quiet: true);
        }
        else
        {
            using var busyWork = BusyWork.Shared.Begin(Lang.T($"Reading Teams {ticketKey}...", $"Đang đọc Teams {ticketKey}..."));
            result = await _teamsThreads.ReadAsync(ticketKey, chatId, cancellationToken);
        }
        if (result.State != TeamsThreadState.SignedOut)
        {
            _teamsSignedOut = false;
        }
        return result;
    }

    // Not forced and the conversation still ends on the message the cached analysis answers: that, without running Claude again.
    private async Task<(TeamsAnalysis? Analysis, string? Error)> AnalyzeTeamsConversationAsync(string ticketKey, TeamsThread thread,
        string myId, string? mainPoint, bool force, CancellationToken cancellationToken)
    {
        var messages = thread.Messages;
        var lastMessageId = messages.Count == 0 ? "" : messages[messages.Count - 1].Id;
        var cacheKey = (ticketKey, TeamsChat.ConversationKey(thread));
        if (!force && _teamsAnalyses.TryGetValue(cacheKey, out var cached) && cached.LastMessageId == lastMessageId)
        {
            return (cached.Analysis, null);
        }

        var worktreePath = BackendWorktreePath(ticketKey);
        var workingDirectory = Directory.Exists(worktreePath) ? worktreePath : DevRepoPath;
        using var busyWork = BusyWork.Shared.Begin(Lang.T($"Claude is analyzing the Teams conversation of {ticketKey}...",
            $"Claude đang phân tích hội thoại Teams của {ticketKey}..."));
        var screenshots = TeamsReply.ScreenshotsForAnalysis(thread, myId);
        var extraDirectories = new List<string>();
        if (screenshots.Count != 0)
        {
            var screenshotsDirectory = Path.Combine(AppPaths.TeamsShotsDir(ticketKey), "analysis");
            screenshots = await _teamsThreads.SaveImagesAsync(screenshots, screenshotsDirectory, cancellationToken);
            if (screenshots.Count != 0)
            {
                extraDirectories.Add(screenshotsDirectory);
            }
        }

        var prompt = TeamsReply.BuildPrompt(ticketKey, FindTicketSummary(ticketKey), thread, myId, mainPoint, screenshots);
        var (analysis, error) = await TeamsReplySuggester.SuggestAsync(workingDirectory, prompt, extraDirectories, cancellationToken);
        // A cancelled run belongs to a closed tab or a newer run, so it must not land in the cache.
        if (analysis != null && !cancellationToken.IsCancellationRequested)
        {
            _teamsAnalyses[cacheKey] = (lastMessageId, analysis);
        }
        return (analysis, error);
    }

    private void DropTeamsAnalyses(string ticketKey)
    {
        foreach (var cacheKey in _teamsAnalyses.Keys.Where(key => key.TicketKey == ticketKey).ToList())
        {
            _teamsAnalyses.Remove(cacheKey);
        }
    }

    private async Task<TeamsReplyResult> SendTeamsReplyAsync(string ticketKey, TeamsThread thread, TeamsDraft draft, CancellationToken cancellationToken)
    {
        using var busyWork = BusyWork.Shared.Begin(Lang.T($"Replying on {ticketKey} in Teams...", $"Đang trả lời {ticketKey} trên Teams..."));
        return await _teamsThreads.SendReplyAsync(ticketKey, thread, draft, cancellationToken);
    }

    private async Task<TeamsReplyResult> EditTeamsMessageAsync(string ticketKey, TeamsThread thread, string messageId, string? subject,
        TeamsDraft draft, CancellationToken cancellationToken)
    {
        using var busyWork = BusyWork.Shared.Begin(Lang.T($"Saving the edit on {ticketKey} in Teams...", $"Đang lưu tin đã sửa của {ticketKey} trên Teams..."));
        return await _teamsThreads.EditAsync(ticketKey, thread, messageId, subject, draft, cancellationToken);
    }

    private async Task<TeamsReplyResult> DeleteTeamsMessageAsync(string ticketKey, TeamsThread thread, string messageId, CancellationToken cancellationToken)
    {
        using var busyWork = BusyWork.Shared.Begin(Lang.T($"Deleting a message on {ticketKey} in Teams...", $"Đang xoá tin của {ticketKey} trên Teams..."));
        return await _teamsThreads.DeleteAsync(ticketKey, thread, messageId, cancellationToken);
    }

    // A visible Chrome on the NOIS profile, off the UI thread; a new sign-in drops the cached session.
    private async Task<bool> SignInToTeamsAsync()
    {
        using var busyWork = BusyWork.Shared.Begin(Lang.T("Signing in to Teams...", "Đang đăng nhập Teams..."));
        var (success, error) = await Task.Run(() => TeamsWebSniffer.SignInAsync());
        if (!success)
        {
            AppLogger.Info($"[teams] sign-in did not finish: {error}");
            return false;
        }
        _teamsChat.InvalidateSession();
        _teamsSignedOut = false;
        return true;
    }

    // The board's summary for the Claude prompt; a store read failure only leaves it out.
    private string? FindTicketSummary(string ticketKey)
    {
        if (_ticketStore == null)
        {
            return null;
        }
        try
        {
            var found = TicketWorkspaceView.FindTicket(ticketKey, _ticketStore.LoadStarted(), _ticketStore.LoadDone(),
                _ticketStore.LoadPending(), _ticketStore.LoadDismissed());
            return found?.Ticket.Summary;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            AppLogger.Error($"[teams] reading the board for {ticketKey} failed: {e.Message}");
            return null;
        }
    }
}
