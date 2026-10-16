namespace NoisLogTray;

// Content is the reviewer's markdown, as Azure DevOps stores it.
internal sealed record PrComment(string Author, string Content, DateTimeOffset PublishedAt);
