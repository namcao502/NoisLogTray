namespace NoisLogTray;

// The background Teams check: every few minutes it reads the Started tickets' posts and group chats and reminds the user
// of messages that wait for an answer (notice, dot on the ticket's Teams button, Dashboard to-do).
internal sealed partial class TrayApp
{
    private const int StartedTicketLimit = 10;

    private static readonly TimeSpan TeamsAttentionFirstDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TeamsAttentionInterval = TimeSpan.FromMinutes(2);

    private readonly System.Windows.Forms.Timer _teamsAttentionTimer = new() { Interval = (int)TeamsAttentionFirstDelay.TotalMilliseconds };
    private readonly CancellationTokenSource _teamsAttentionCancel = new();
    private IReadOnlyList<TeamsAttentionItem> _teamsAttention = Array.Empty<TeamsAttentionItem>();
    private readonly HashSet<string> _teamsReadConversationKeys = new(); // conversations the last check read, plus ones marked seen since
    private bool _checkingTeams;
    private bool _teamsSignedOut;

    private void StartTeamsAttentionWatch()
    {
        _teamsAttentionTimer.Tick += (_, _) =>
        {
            _teamsAttentionTimer.Interval = (int)TeamsAttentionInterval.TotalMilliseconds;
            _ = CheckTeamsAttentionAsync();
        };
        _teamsAttentionTimer.Start();
    }

    private void StopTeamsAttentionWatch()
    {
        _teamsAttentionTimer.Dispose();
        _teamsAttentionCancel.Cancel();
    }

    private async Task CheckTeamsAttentionAsync()
    {
        if (_checkingTeams || _teamsSignedOut || _ticketStore == null)
        {
            return;
        }

        _checkingTeams = true;
        try
        {
            var ticketKeys = TicketSort.ByDue(_ticketStore.LoadStarted().Values, jt => jt)
                .Select(jt => jt.Key)
                .Take(StartedTicketLimit)
                .ToList();
            if (ticketKeys.Count == 0)
            {
                return;
            }

            var cancellationToken = _teamsAttentionCancel.Token;
            var (myId, conversations) = await Task.Run(() => _teamsThreads.ReadConversationsAsync(ticketKeys, cancellationToken));
            ApplyTeamsConversations(myId, conversations);
        }
        catch (TeamsSignedOutException)
        {
            _teamsSignedOut = true;
            AppLogger.Info("[teams] attention: signed out, the background check pauses until the next sign-in");
        }
        catch (OperationCanceledException)
        {
            // The app is shutting down.
        }
        catch (Exception e) when (e is HttpRequestException || e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException)
        {
            AppLogger.Info($"[teams] attention: check failed: {e.Message}");
        }
        finally
        {
            _checkingTeams = false;
        }
    }

    // UI thread: settings, the pending list and the notices change together.
    private void ApplyTeamsConversations(string myId, IReadOnlyList<TeamsConversation> conversations)
    {
        var items = new List<TeamsAttentionItem>();
        var notices = new List<TeamsAttentionItem>();
        _teamsReadConversationKeys.Clear();
        foreach (var conversation in conversations)
        {
            _teamsReadConversationKeys.Add(TeamsChat.ConversationKey(conversation.Thread));
        }
        UpdateTeamsMarks(settings =>
        {
            foreach (var conversation in conversations)
            {
                var messages = conversation.Thread.Messages;
                if (messages.Count == 0)
                {
                    continue;
                }

                var conversationKey = TeamsChat.ConversationKey(conversation.Thread);
                DateTimeOffset? seenAt = settings.TeamsSeenAt.TryGetValue(conversationKey, out var seen) ? seen : null;
                DateTimeOffset? notifiedAt = settings.TeamsNotifiedAt.TryGetValue(conversationKey, out var notified) ? notified : null;
                var item = TeamsAttention.Pending(conversation, myId, seenAt);
                if (item != null)
                {
                    items.Add(item);
                    if (TeamsAttention.ShouldNotify(item, notifiedAt, settings.TeamsAttentionBaselineDone))
                    {
                        notices.Add(item);
                    }
                }
                settings.TeamsNotifiedAt[conversationKey] = messages.Max(tm => tm.SentAt);
            }
            settings.TeamsAttentionBaselineDone = true;
        });

        _teamsAttention = items;
        RefreshTeamsAttention();
        foreach (var notice in notices)
        {
            Notify(TeamsAttention.NoticeText(notice), ToolTipIcon.Info, open: () => ShowTeamsConversation(notice.TicketKey, notice.ChatId));
        }
    }

    private void UpdateTeamsMarks(Action<AppSettings> update)
    {
        try
        {
            var settings = AppSettings.Load();
            update(settings);
            var now = Hcm.Now();
            settings.TeamsSeenAt = TeamsAttention.Pruned(settings.TeamsSeenAt, now, _teamsReadConversationKeys);
            settings.TeamsNotifiedAt = TeamsAttention.Pruned(settings.TeamsNotifiedAt, now, _teamsReadConversationKeys);
            AppSettings.Save(settings);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            AppLogger.Error($"[teams] saving the reminder marks failed: {e.Message}");
        }
    }

    private void ShowTeamsConversation(string ticketKey, string? chatId)
    {
        _teamsConversations[ticketKey] = chatId ?? "";
        if (!_openTickets.Contains(ticketKey))
        {
            // Kept for a shell built by OpenTicketTab below, which creates its page after this line.
            _teamsTabsToOpen[ticketKey] = chatId ?? "";
            _ticketPage?.OpenOnTeams(ticketKey, chatId ?? "");
        }
        OpenTicketTab(ticketKey);
        _teamsTabsToOpen.Remove(ticketKey);
        _ticketPage?.ShowTeams(ticketKey, chatId ?? "");
    }

    private void MarkTeamsSeen(string conversationKey, DateTimeOffset sentAt)
    {
        _teamsReadConversationKeys.Add(conversationKey);
        UpdateTeamsMarks(settings =>
        {
            var hasSeen = settings.TeamsSeenAt.TryGetValue(conversationKey, out var seenAt);
            settings.TeamsSeenAt[conversationKey] = hasSeen && seenAt > sentAt ? seenAt : sentAt;
        });
        _teamsAttention = _teamsAttention.Where(tai => tai.ConversationKey != conversationKey || tai.SentAt > sentAt).ToList();
        RefreshTeamsAttention();
    }

    // Also run once the shell is built, so a rebuilt window shows the dots and the to-do rows at once.
    private void RefreshTeamsAttention()
    {
        _ticketPage?.SetTeamsAttention(_teamsAttention.Select(tai => tai.TicketKey).ToHashSet());
        _dashboardPage?.SetTeamsAttention(_teamsAttention);
    }
}
