using System.Globalization;

namespace NoisLogTray;

internal sealed record JiraTicket(
    string Key,
    string Summary,
    string? DueDate,
    string? Eta,
    double? DevPriority = null,
    string? Status = null,
    long? TimeSpentSeconds = null,
    string? StatusCategory = null)
{
    // Statuses before any work is expected to be logged. Anything else
    // (PR & Code Review onward, QA Failed) means work happened. Hold is a
    // parked ticket, not necessarily a worked one.
    internal static readonly string[] PreWorkStatuses = { "Backlog", "Selected for Development", "In Progress", "Hold" };

    // Due today or within this many days counts as due soon.
    private const int DueSoonDays = 2;

    public bool HasEta => !string.IsNullOrEmpty(Eta);

    public bool HasDevPriority => DevPriority.HasValue;

    public DevPriorityLevel DevPriorityLevel
    {
        get
        {
            if (!DevPriority.HasValue) return DevPriorityLevel.None;
            if (DevPriority.Value <= 1) return DevPriorityLevel.Top;
            return DevPriority.Value <= 2 ? DevPriorityLevel.High : DevPriorityLevel.Normal;
        }
    }

    // A ticket Jira already finished is never flagged, whatever its due date.
    public DueUrgency GetDueUrgency(DateOnly today)
    {
        if (IsStatusDone
            || !DateOnly.TryParseExact(DueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var due))
            return DueUrgency.None;

        if (due < today) return DueUrgency.Overdue;
        return due.DayNumber - today.DayNumber <= DueSoonDays ? DueUrgency.Soon : DueUrgency.None;
    }

    // Jira's statusCategory key ("new" / "indeterminate" / "done"). Done covers
    // QA Confirmed, Approved for Production, Deployed to Production, Done and
    // Archived -- the same set the assigned-tickets JQL excludes.
    public bool IsStatusDone => string.Equals(StatusCategory, "done", StringComparison.OrdinalIgnoreCase);

    // A ticket past pre-work with zero logged time is the case most likely to be forgotten.
    public bool IsTimeLogMissing
    {
        get
        {
            if (string.IsNullOrEmpty(Status)) return false;

            var isPreWork = PreWorkStatuses.Any(s => string.Equals(s, Status, StringComparison.OrdinalIgnoreCase));
            return !isPreWork && (TimeSpentSeconds ?? 0) == 0;
        }
    }

    // Hours + minutes only; Jira's own "1d" is 8h, which reads wrong here.
    public static string FormatTimeSpent(long? seconds)
    {
        if (seconds == null || seconds.Value == 0) return "-";

        var hours = seconds.Value / 3600;
        var minutes = seconds.Value % 3600 / 60;
        if (hours == 0) return $"{minutes}m";

        return minutes == 0 ? $"{hours}h" : $"{hours}h {minutes}m";
    }
}
