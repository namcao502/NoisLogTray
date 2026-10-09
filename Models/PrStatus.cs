namespace NoisLogTray;

// Repository is the Azure DevOps repo name (Backend-DotNet, Portal-Angular, ...); PRs are searched project-wide.
internal record PrStatus(int PullRequestId, string Branch, string Status, int CommentCount, string Url,
    string Repository = AzureDevOpsClient.BackendRepository);
