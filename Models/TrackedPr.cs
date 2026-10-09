namespace NoisLogTray;

// Repository is null for entries saved before PRs were tracked across repos; those were all Backend-DotNet.
internal record TrackedPr(string Status, int CommentCount, string? Repository = null);
