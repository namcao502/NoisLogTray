namespace NoisLogTray;

internal sealed record TicketPr(string Repository, string Branch, int PullRequestId, string Status, int CommentCount)
{
    public string RepoLabel => DashboardDataBuilder.RepoLabel(Repository);
    public string RepoName => DashboardDataBuilder.RepoName(Repository);
    public string Url => AzureDevOpsClient.PullRequestUrl(Repository, PullRequestId);
}
