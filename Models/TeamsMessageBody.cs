namespace NoisLogTray;

internal sealed record TeamsMessageBody(string Html, string? MentionsJson, IReadOnlyList<string> AmsReferences);
