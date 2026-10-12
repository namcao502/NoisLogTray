namespace NoisLogTray;

internal sealed record TicketWorktree(string Repository, string Path)
{
    public string RepoLabel => DashboardDataBuilder.RepoLabel(Repository);
}
