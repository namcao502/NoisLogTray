namespace NoisLogTray;

// An image from the conversation; Path stays null until it is saved to disk for Claude.
internal sealed record TeamsScreenshot(string Url, string SenderName, DateTimeOffset SentAt, string? Path = null);
