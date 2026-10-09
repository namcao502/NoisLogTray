namespace NoisLogTray;

// One Jira poll: files newly assigned tickets under New and spots started tickets that Jira finished.
internal sealed class TicketPoller
{
    private readonly TicketStore _ticketStore;
    private readonly TicketBoard _ticketBoard;

    internal TicketPoller(TicketStore ticketStore, TicketBoard ticketBoard)
    {
        _ticketStore = ticketStore;
        _ticketBoard = ticketBoard;
    }

    // Null when the assigned query failed. No ConfigureAwait(false): store access must stay on the UI thread.
    internal async Task<TicketPollResult?> PollAsync(IJiraClient jira, CancellationToken ct = default)
    {
        Dictionary<string, JiraTicket> assigned;
        try
        {
            assigned = await jira.GetAssignedTicketsAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            AppLogger.Error($"Assigned tickets poll failed: {ex.Message}");
            return null;
        }

        _ticketBoard.UpdateTicketDetails(assigned);

        var seen = _ticketStore.LoadSeen();
        var isFirstRun = !_ticketStore.HasSeenFile() || seen == null;
        var seenKeys = new HashSet<string>(seen ?? new List<string>());
        var keysNewToSeen = assigned.Keys.Where(k => !seenKeys.Contains(k)).ToList();

        var pending = _ticketStore.LoadPending();
        var started = _ticketStore.LoadStarted();
        var newTickets = new List<JiraTicket>();
        if (!isFirstRun)
        {
            var done = _ticketStore.LoadDone();
            var dismissed = _ticketStore.LoadDismissed();
            foreach (var key in keysNewToSeen)
            {
                var isOnBoard = pending.ContainsKey(key) || started.ContainsKey(key)
                    || done.ContainsKey(key) || dismissed.ContainsKey(key);
                if (isOnBoard)
                {
                    continue;
                }

                pending[key] = assigned[key];
                newTickets.Add(assigned[key]);
            }

            if (newTickets.Count != 0)
            {
                _ticketStore.SavePending(pending);
            }
        }

        _ticketStore.SaveSeen(seenKeys.Concat(keysNewToSeen));

        var finishedTickets = await RefreshOffPollTicketsAsync(jira, assigned, pending, started, ct);
        return new TicketPollResult(newTickets, finishedTickets);
    }

    // Tickets that left the assigned query (e.g. closed) are fetched one by one; those turning Done are reported.
    private async Task<List<JiraTicket>> RefreshOffPollTicketsAsync(
        IJiraClient jira,
        Dictionary<string, JiraTicket> assigned,
        Dictionary<string, JiraTicket> pending,
        Dictionary<string, JiraTicket> started,
        CancellationToken ct)
    {
        var finishedTickets = new List<JiraTicket>();
        // Tickets already stored as Done in Jira have nothing left to refresh, so they are not refetched.
        var pendingKeys = pending.Where(kv => !kv.Value.IsStatusDone).Select(kv => kv.Key);
        var startedKeys = started.Where(kv => !kv.Value.IsStatusDone).Select(kv => kv.Key);
        var offPollKeys = pendingKeys.Concat(startedKeys).Where(k => !assigned.ContainsKey(k)).Distinct().ToList();
        if (offPollKeys.Count == 0)
        {
            return finishedTickets;
        }

        var liveTickets = new Dictionary<string, JiraTicket>();
        foreach (var key in offPollKeys)
        {
            try
            {
                var liveTicket = await jira.GetTicketAsync(key, ct);
                if (liveTicket != null)
                {
                    liveTickets[key] = liveTicket;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                AppLogger.Error($"Refreshing {key} failed: {ex.Message}");
            }
        }

        // Reloaded: the user may have moved a ticket while the fetches were awaited.
        var currentStarted = _ticketStore.LoadStarted();
        foreach (var liveTicket in liveTickets.Values)
        {
            if (currentStarted.TryGetValue(liveTicket.Key, out var storedTicket) && liveTicket.IsStatusDone && !storedTicket.IsStatusDone)
            {
                finishedTickets.Add(liveTicket);
            }
        }

        _ticketBoard.UpdateTicketDetails(liveTickets);
        return finishedTickets;
    }
}
