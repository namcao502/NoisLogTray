using System.Globalization;

namespace NoisLogTray;

internal static class SettingsValidation
{
    // Offline format checks run before the network verification; null = ok, else a Lang.T message.
    internal static string? FormatError(string baseUrl, string email, string jiraToken, string hrmKey,
        IReadOnlyList<string> columns, string logTimeText)
    {
        if (!baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return Lang.T("Jira site URL must start with http.", "Địa chỉ Jira phải bắt đầu bằng http.");
        if (!email.Contains('@'))
            return Lang.T("Enter a valid Jira email.", "Hãy nhập email Jira hợp lệ.");
        if (jiraToken.Length == 0 || hrmKey.Length == 0)
            return Lang.T("Jira API token and HRM API key are required.", "Cần nhập API token Jira và API key HRM.");
        if (columns.Count == 0)
            return Lang.T("Enter at least one TSC column (e.g. M, J).", "Hãy nhập ít nhất một cột TSC (vd. M, J).");
        if (!TimeOnly.TryParseExact(logTimeText, AppConfig.LogTimeFormats,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return Lang.T("Enter the daily log time in 12-hour format, e.g. 6:00 PM.", "Hãy nhập giờ tự log theo dạng 12 giờ, vd. 6:00 PM.");
        return null;
    }
}
