namespace NoisLogTray;

// The "check cmt" flows: a lead asks in Teams to check PR comments, the prompt goes to the ticket's Claude,
// and each finished turn counts the open PR threads. State lives for the app session only.
internal sealed partial class TrayApp
{
    private readonly Dictionary<(string TicketKey, string ConversationKey), TeamsCommentFlow> _commentFlows = new();

    // Null when handed over, else the error text for the page.
    private async Task<string?> HandCommentsToClaudeAsync(string ticketKey, string conversationKey, string prompt, CancellationToken cancellationToken)
    {
        if (!_commentFlows.TryGetValue((ticketKey, conversationKey), out var flow) || flow.Stage != TeamsCommentStage.Asked)
        {
            return Lang.T("That request is no longer waiting.", "Lời nhờ này không còn chờ nữa.");
        }

        if (!TeamsCommentFlows.IsSafePrompt(prompt))
        {
            return Lang.T("Use one line without quotes, $, backslash or backticks.", "Prompt chỉ một dòng, không có dấu nháy, $, \\ hay backtick.");
        }

        var error = await SendLineToClaude(ticketKey, prompt.Trim(), cancellationToken);
        if (error != null)
        {
            return error;
        }

        if (_commentFlows.TryGetValue((ticketKey, conversationKey), out var current))
        {
            _commentFlows[(ticketKey, conversationKey)] = current with { Stage = TeamsCommentStage.HandedOff, PromptSubmitted = false };
            PushCommentFlows(ticketKey);
        }

        return null;
    }

    private void MarkCommentFlowDone(string ticketKey, string conversationKey)
    {
        if (_commentFlows.TryGetValue((ticketKey, conversationKey), out var flow))
        {
            _commentFlows[(ticketKey, conversationKey)] = TeamsCommentFlows.AfterTurn(flow, 0);
            PushCommentFlows(ticketKey);
        }
    }

    private void DismissCommentFlow(string ticketKey, string conversationKey)
    {
        if (_commentFlows.Remove((ticketKey, conversationKey)))
        {
            PushCommentFlows(ticketKey);
        }
    }

    private void DropCommentFlows(string ticketKey)
    {
        var keys = _commentFlows.Keys.Where(key => key.TicketKey == ticketKey).ToList();
        foreach (var key in keys)
        {
            _commentFlows.Remove(key);
        }
    }

    // Runs on the UI thread after the Teams view applied a thread; the view gets its flow again here, which also covers a new view.
    private void OnCommentAskSeen(string ticketKey, string conversationKey, string? chatId, TeamsMessage? ask, bool lastIsMine)
    {
        var flowKey = (ticketKey, conversationKey);
        _commentFlows.TryGetValue(flowKey, out var current);
        TeamsCommentFlow? asked = null;
        if (ask != null)
        {
            asked = new TeamsCommentFlow(ticketKey, conversationKey, chatId ?? "", ask.Id, ask.SenderName, TeamsCommentStage.Asked);
        }

        var next = TeamsCommentFlows.OnAsk(current, asked, lastIsMine);
        if (next == null)
        {
            _commentFlows.Remove(flowKey);
        }
        else
        {
            _commentFlows[flowKey] = next;
        }

        PushCommentFlows(ticketKey);
    }

    // The flow of the conversation the ticket's Teams view shows (the post is "", else a chat's id).
    private void PushCommentFlows(string ticketKey)
    {
        var shownChatId = _teamsConversations.GetValueOrDefault(ticketKey, "");
        var shown = _commentFlows.Values.FirstOrDefault(cf => cf.TicketKey == ticketKey && (cf.ChatId ?? "") == shownChatId);
        _ticketPage?.SetCommentFlow(ticketKey, shown);
    }

    // Runs on the UI thread from OnClaudeHook for a ticket session; a Stop after the handed-off prompt re-counts the open threads.
    private void OnCommentFlowHook(string ticketKey, ClaudeHookEvent hookEvent, bool turnFinished)
    {
        var handedOffKeys = _commentFlows.Where(cf => cf.Key.TicketKey == ticketKey && cf.Value.Stage == TeamsCommentStage.HandedOff)
            .Select(cf => cf.Key).ToList();
        if (handedOffKeys.Count == 0)
        {
            return;
        }

        var turnKeys = new List<(string TicketKey, string ConversationKey)>();
        foreach (var key in handedOffKeys)
        {
            var flow = TeamsCommentFlows.Next(_commentFlows[key], hookEvent);
            if (turnFinished && flow.PromptSubmitted)
            {
                flow = flow with { PromptSubmitted = false };
                turnKeys.Add(key);
            }

            _commentFlows[key] = flow;
        }

        if (turnKeys.Count != 0)
        {
            _ = CheckCommentFlowsAsync(ticketKey, turnKeys);
        }
    }

    private async Task CheckCommentFlowsAsync(string ticketKey, List<(string TicketKey, string ConversationKey)> flowKeys)
    {
        int? openThreadCount;
        try
        {
            var prs = new List<TicketPr>();
            if (_ticketStore != null)
            {
                try
                {
                    prs = DashboardDataBuilder.LatestPrs(ticketKey, _ticketStore.LoadPrTracking());
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    AppLogger.Error($"[teams] {ticketKey}: reading the PRs failed: {e.Message}");
                }
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            openThreadCount = await TeamsCommentFlows.CountOpenThreadsAsync(_ado, prs, timeout.Token);
        }
        catch (Exception e)
        {
            AppLogger.Error($"[teams] {ticketKey}: counting the open PR threads failed: {e.Message}");
            openThreadCount = null;
        }

        AppLogger.Info($"[teams] {ticketKey}: open PR threads after the turn: {(openThreadCount?.ToString() ?? "unknown")}");
        foreach (var key in flowKeys)
        {
            if (!_commentFlows.TryGetValue(key, out var flow) || flow.Stage != TeamsCommentStage.HandedOff)
            {
                continue;
            }

            var after = TeamsCommentFlows.AfterTurn(flow, openThreadCount);
            _commentFlows[key] = after;
            if (after.Stage == TeamsCommentStage.Done)
            {
                var chatId = after.ChatId;
                Notify(Lang.T($"{ticketKey}: Claude finished the PR comments. Reply in Teams?",
                    $"{ticketKey}: Claude đã xử lý xong comment PR. Trả lời trên Teams?"), ToolTipIcon.Info,
                    open: () => ShowTeamsConversation(ticketKey, chatId));
            }
        }

        PushCommentFlows(ticketKey);
    }
}
