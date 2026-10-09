namespace NoisLogTray;

// Moves tickets between the New / In progress / Done / Dismissed lists.
internal sealed class TicketBoard
{
    private readonly TicketStore _ticketStore;

    internal TicketBoard(TicketStore ticketStore)
    {
        _ticketStore = ticketStore;
    }

    // Manual add for a ticket the poll didn't bring in; also marked seen so a later
    // assignment doesn't re-notify.
    internal bool Add(JiraTicket ticket)
    {
        var pending = _ticketStore.LoadPending();
        var isOnBoard = pending.ContainsKey(ticket.Key)
            || _ticketStore.LoadStarted().ContainsKey(ticket.Key)
            || _ticketStore.LoadDone().ContainsKey(ticket.Key)
            || _ticketStore.LoadDismissed().ContainsKey(ticket.Key);
        if (isOnBoard)
        {
            return false;
        }

        pending[ticket.Key] = ticket;
        _ticketStore.SavePending(pending);
        // Seen is left alone when absent or corrupt: writing it would fake a baseline the poller treats as non-first-run.
        var seen = _ticketStore.LoadSeen();
        if (_ticketStore.HasSeenFile() && seen != null)
        {
            _ticketStore.SaveSeen(seen.Append(ticket.Key));
        }

        return true;
    }

    internal void Start(string ticketKey)
    {
        var pending = _ticketStore.LoadPending();
        if (!pending.Remove(ticketKey, out var ticket))
        {
            return;
        }

        _ticketStore.SavePending(pending);

        var started = _ticketStore.LoadStarted();
        started[ticketKey] = ticket;
        _ticketStore.SaveStarted(started);
    }

    // In progress -> Done is a manual move only; polls never auto-remove.
    internal void MarkDone(string ticketKey)
    {
        var started = _ticketStore.LoadStarted();
        if (!started.Remove(ticketKey, out var ticket))
        {
            return;
        }

        _ticketStore.SaveStarted(started);

        var done = _ticketStore.LoadDone();
        done[ticketKey] = ticket;
        _ticketStore.SaveDone(done);
    }

    // Moved to Dismissed rather than dropped so a mis-click can be undone; the key
    // stays in seen, so a poll never re-adds it.
    internal void Dismiss(string ticketKey)
    {
        var dismissed = _ticketStore.LoadDismissed();

        var pending = _ticketStore.LoadPending();
        if (pending.Remove(ticketKey, out var pendingTicket))
        {
            _ticketStore.SavePending(pending);
            dismissed[ticketKey] = new DismissedTicket(pendingTicket, TicketSection.New);
        }

        var started = _ticketStore.LoadStarted();
        if (started.Remove(ticketKey, out var startedTicket))
        {
            _ticketStore.SaveStarted(started);
            dismissed[ticketKey] = new DismissedTicket(startedTicket, TicketSection.InProgress);
        }

        var done = _ticketStore.LoadDone();
        if (done.Remove(ticketKey, out var doneTicket))
        {
            _ticketStore.SaveDone(done);
            dismissed[ticketKey] = new DismissedTicket(doneTicket, TicketSection.Done);
        }

        _ticketStore.SaveDismissed(dismissed);
    }

    internal void Restore(string ticketKey)
    {
        var dismissed = _ticketStore.LoadDismissed();
        if (!dismissed.Remove(ticketKey, out var dismissedTicket))
        {
            return;
        }

        _ticketStore.SaveDismissed(dismissed);

        switch (dismissedTicket.Origin)
        {
            case TicketSection.InProgress:
                var started = _ticketStore.LoadStarted();
                started[ticketKey] = dismissedTicket.Ticket;
                _ticketStore.SaveStarted(started);
                break;
            case TicketSection.Done:
                var done = _ticketStore.LoadDone();
                done[ticketKey] = dismissedTicket.Ticket;
                _ticketStore.SaveDone(done);
                break;
            default:
                var pending = _ticketStore.LoadPending();
                pending[ticketKey] = dismissedTicket.Ticket;
                _ticketStore.SavePending(pending);
                break;
        }
    }

    // Refreshes stored tickets from Jira (the poll only adds new keys). Tickets Jira
    // no longer returns are left as-is; leaving a list stays a manual Done/Dismiss.
    internal bool UpdateTicketDetails(Dictionary<string, JiraTicket> jiraTickets)
    {
        var isChanged = false;

        var pending = _ticketStore.LoadPending();
        if (ReplaceChangedTickets(pending, jiraTickets))
        {
            _ticketStore.SavePending(pending);
            isChanged = true;
        }

        var started = _ticketStore.LoadStarted();
        if (ReplaceChangedTickets(started, jiraTickets))
        {
            _ticketStore.SaveStarted(started);
            isChanged = true;
        }

        var done = _ticketStore.LoadDone();
        if (ReplaceChangedTickets(done, jiraTickets))
        {
            _ticketStore.SaveDone(done);
            isChanged = true;
        }

        var dismissed = _ticketStore.LoadDismissed();
        var isDismissedChanged = false;
        foreach (var ticketKey in dismissed.Keys.ToList())
        {
            if (jiraTickets.TryGetValue(ticketKey, out var jiraTicket) && jiraTicket != dismissed[ticketKey].Ticket)
            {
                dismissed[ticketKey] = dismissed[ticketKey] with { Ticket = jiraTicket };
                isDismissedChanged = true;
            }
        }

        if (isDismissedChanged)
        {
            _ticketStore.SaveDismissed(dismissed);
            isChanged = true;
        }

        return isChanged;
    }

    private static bool ReplaceChangedTickets(
        Dictionary<string, JiraTicket> storedTickets,
        Dictionary<string, JiraTicket> jiraTickets)
    {
        var isChanged = false;
        foreach (var ticketKey in storedTickets.Keys.ToList())
        {
            if (jiraTickets.TryGetValue(ticketKey, out var jiraTicket) && jiraTicket != storedTickets[ticketKey])
            {
                storedTickets[ticketKey] = jiraTicket;
                isChanged = true;
            }
        }

        return isChanged;
    }
}
