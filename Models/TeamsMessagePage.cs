namespace NoisLogTray;

internal sealed record TeamsMessagePage(IReadOnlyList<TeamsMessage> Messages, string? BackwardLink);
