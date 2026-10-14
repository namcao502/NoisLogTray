namespace NoisLogTray;

// MDP's board transitions are global (any status reaches any other), so the app enforces
// the forward-only order itself: review walks Deployed to Dev, then Ready to Test.
internal static class JiraStatusFlow
{
    internal const string DeployedToDev = "Deployed to Dev";
    internal const string ReadyToTest = "Ready to Test";

    private const string PrAndCodeReview = "PR & Code Review";
    private static readonly string[] ReworkStatuses = { "In Progress", "QA Failed", "Reopened" };

    internal static IReadOnlyList<string> GetStepsToReadyToTest(string? status)
    {
        if (string.Equals(status, PrAndCodeReview, StringComparison.OrdinalIgnoreCase))
            return new[] { DeployedToDev, ReadyToTest };

        if (string.Equals(status, DeployedToDev, StringComparison.OrdinalIgnoreCase))
            return new[] { ReadyToTest };

        return Array.Empty<string>();
    }

    // A Done ticket was sent back when Jira moved it from review or later into a rework status. Only a move from past
    // pre-work counts, so a ticket marked Done while Jira still read In Progress is not pulled back.
    internal static bool IsSentBack(string? storedStatus, string? liveStatus)
    {
        if (string.IsNullOrEmpty(storedStatus) || string.Equals(storedStatus, liveStatus, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var isRework = ReworkStatuses.Any(rs => string.Equals(rs, liveStatus, StringComparison.OrdinalIgnoreCase));
        var wasPastPreWork = !JiraTicket.PreWorkStatuses.Any(pws => string.Equals(pws, storedStatus, StringComparison.OrdinalIgnoreCase));
        return isRework && wasPastPreWork;
    }

    // Lives here rather than in TaskRow so non-UI code (the Dashboard) can ask the same question.
    internal static string? ReadyToTestLabel(JiraTicket ticket, TicketSection section, bool isDevPrMerged)
    {
        if (section != TicketSection.InProgress && section != TicketSection.Done) return null;
        if (!isDevPrMerged) return null;
        var steps = GetStepsToReadyToTest(ticket.Status);
        return steps.Count != 0 ? string.Join(" ", steps.Select(step => $"→ {step}")) : null;
    }
}
