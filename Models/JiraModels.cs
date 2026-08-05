namespace NoisLogTray;

internal sealed record JiraVerifyResult(bool Valid, string? Summary);

internal sealed record JiraSuggestion(string Key, string Summary);
