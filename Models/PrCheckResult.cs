namespace NoisLogTray;

internal sealed record PrCheckResult(
    IReadOnlyList<(string TicketKey, PrNotification Notification)> Notifications,
    IReadOnlyList<string> DevMergedNeedingReadyToTest,
    bool IsComplete);
