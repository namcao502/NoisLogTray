namespace NoisLogTray;

// One problem raised in the conversation; Status is open, answered or fixed.
internal sealed record TeamsIssue(string Text, string Status);
