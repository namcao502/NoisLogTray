namespace NoisLogTray;

// The ticket tab's Teams view: a draft sent as the user through their workflow.
internal sealed partial class TrayApp
{
    private static readonly TimeSpan TeamsPostedAtKeep = TimeSpan.FromDays(180);

    // Null (with a notice) until the workflow URL is set, so the tab stays on its current view.
    private TeamsPostView? CreateTeamsPostView(string ticketKey)
    {
        if (!_teams.CanPostAsUser)
        {
            Notify(Lang.T("Set the Teams post workflow URL in Settings first.", "Hãy nhập URL Workflow đăng Teams trong Settings trước."), ToolTipIcon.Warning);
            return null;
        }
        // _teams is read at send time: a Settings save replaces it.
        var view = new TeamsPostView(ticketKey,
            () => TeamsPost.ReadDraft(DevRepoPath, BackendWorktreePath(ticketKey), ticketKey) ?? BuildTeamsDraft(ticketKey),
            () => AppSettings.Load().TeamsPostedAt.TryGetValue(ticketKey, out var postedAt) ? postedAt : null,
            (text, cancellationToken) => _teams.PostAsUserAsync(text, cancellationToken));
        view.Posted += () => RecordTeamsPost(ticketKey);
        return view;
    }

    private void RecordTeamsPost(string ticketKey)
    {
        // Entries older than TeamsPostedAtKeep are dropped, so the map stays small.
        var settings = AppSettings.Load();
        var now = Hcm.Now();
        settings.TeamsPostedAt = settings.TeamsPostedAt.Where(kvp => now - kvp.Value < TeamsPostedAtKeep).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        settings.TeamsPostedAt[ticketKey] = now;
        AppSettings.Save(settings);
        Notify(Lang.T($"Posted {ticketKey} to Teams.", $"Đã đăng {ticketKey} lên Teams."), ToolTipIcon.Info, NoticeKind.Success);
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
