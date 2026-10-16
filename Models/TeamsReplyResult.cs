namespace NoisLogTray;

internal sealed record TeamsReplyResult(TeamsReplyState State, TeamsThreadResult? After, string? Error);
