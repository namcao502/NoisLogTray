namespace NoisLogTray;

internal sealed record TeamsSession(string ChatToken, string AggregatorToken, string ChatBaseUrl, string AggregatorBaseUrl, string MyId,
    string MyName, DateTimeOffset ExpiresAt, string UserAgent, string? ClientVersion,
    string? ProfileToken = null, string? ProfilePictureBaseUrl = null, DateTimeOffset? ProfileTokenExpiresAt = null);
