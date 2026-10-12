namespace NoisLogTray;

// The ticket tab's "Post to Teams..." button: a draft in TeamsPostForm, sent as the user through their workflow.
internal sealed partial class TrayApp
{
    private static readonly TimeSpan TeamsPostedAtKeep = TimeSpan.FromDays(180);

    private void OpenTeamsPost(string ticketKey)
    {
        if (!_teams.CanPostAsUser)
        {
            Notify(Lang.T("Set the Teams post workflow URL in Settings first.", "Hãy nhập URL Workflow đăng Teams trong Settings trước."), ToolTipIcon.Warning);
            return;
        }
        var draft = TeamsPost.ReadDraft(DevRepoPath, BackendWorktreePath(ticketKey), ticketKey) ?? BuildTeamsDraft(ticketKey);
        var postedAt = AppSettings.Load().TeamsPostedAt.TryGetValue(ticketKey, out var at) ? at : (DateTimeOffset?)null;
        using var form = new TeamsPostForm(ticketKey, draft, postedAt, _teams.PostAsUserAsync);
        if (form.ShowDialog(_shell) != DialogResult.OK) return;
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
