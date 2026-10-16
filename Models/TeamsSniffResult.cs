namespace NoisLogTray;

internal sealed record TeamsSniffResult(TeamsSession? Session, bool SignedOut, string? Error);
