namespace NoisLogTray;

// The Teams view's draft: the ticket announcement, posted as the user into the picked channel.
internal sealed partial class TrayApp
{
    private static readonly TimeSpan TeamsPostedAtKeep = TimeSpan.FromDays(180);

    // The draft file Claude wrote, else one built from the board; and when the ticket was last posted.
    private (string Draft, DateTimeOffset? PostedAt) LoadTeamsDraft(string ticketKey)
    {
        var draft = TeamsPost.ReadDraft(DevRepoPath, BackendWorktreePath(ticketKey), ticketKey) ?? BuildTeamsDraft(ticketKey);
        var settings = AppSettings.Load();
        var isPosted = settings.TeamsPostedAt.TryGetValue(ticketKey, out var postedAt);
        var greeted = TeamsPost.WithGreeting(draft, TeamsPost.ResolvePostLead(settings.TeamsPostLead, settings.TeamsPeople));
        return (greeted, isPosted ? postedAt : null);
    }

    private async Task<TeamsReplyResult> PostTeamsAnnouncementAsync(string ticketKey, string subject, TeamsDraft draft, CancellationToken cancellationToken)
    {
        using var busyWork = BusyWork.Shared.Begin(Lang.T($"Posting {ticketKey} to Teams...", $"Đang đăng {ticketKey} lên Teams..."));
        return await _teamsThreads.PostAnnouncementAsync(ticketKey, subject, draft, cancellationToken);
    }

    private void RecordTeamsPost(string ticketKey, TeamsDraft draft)
    {
        // Entries older than TeamsPostedAtKeep are dropped, so the map stays small.
        var settings = AppSettings.Load();
        var now = Hcm.Now();
        settings.TeamsPostedAt = settings.TeamsPostedAt.Where(kvp => now - kvp.Value < TeamsPostedAtKeep).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        settings.TeamsPostedAt[ticketKey] = now;
        if (draft.Mentions.Count != 0)
        {
            settings.TeamsPostLead = new TeamsPerson(draft.Mentions[0].Mri, draft.Mentions[0].DisplayName);
        }
        AppSettings.Save(settings);
        Notify(Lang.T($"Posted {ticketKey} to Teams.", $"Đã đăng {ticketKey} lên Teams."), ToolTipIcon.Info, NoticeKind.Success,
            () => ShowTicketTab(ticketKey));
    }

    // Summary and PRs from the local board; a store read failure only leaves them out.
    private string BuildTeamsDraft(string ticketKey)
    {
        if (_ticketStore == null) return TeamsPost.Build(ticketKey, null, Array.Empty<TicketPr>());
        try
        {
            var found = TicketWorkspaceView.FindTicket(ticketKey, _ticketStore.LoadStarted(), _ticketStore.LoadDone(),
                _ticketStore.LoadPending(), _ticketStore.LoadDismissed());
            return TeamsPost.Build(ticketKey, found?.Ticket.Summary, DashboardDataBuilder.LatestPrs(ticketKey, _ticketStore.LoadPrTracking()));
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            AppLogger.Error($"[teams] reading the board for {ticketKey} failed: {e.Message}");
            return TeamsPost.Build(ticketKey, null, Array.Empty<TicketPr>());
        }
    }
}
