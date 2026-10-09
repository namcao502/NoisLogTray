namespace NoisLogTray;

internal static class DashboardDataBuilder
{
    private const string MainBranchLabel = "main";

    internal static DashboardData Build(
        IReadOnlyDictionary<string, JiraTicket> pending,
        IReadOnlyDictionary<string, JiraTicket> started,
        IReadOnlyDictionary<string, JiraTicket> done,
        IReadOnlyDictionary<string, TrackedPr> prTracking,
        IReadOnlyDictionary<string, PrCommentAlert> commentAlerts,
        IReadOnlyList<QueueEntry> queue,
        DateTimeOffset nowHcm,
        TimeOnly logTime)
    {
        var today = DateOnly.FromDateTime(nowHcm.DateTime);
        var todayApiDate = Hcm.ApiDate(today);
        var startedSorted = TicketSort.ByDue(started.Values, ticket => ticket);
        var doneSorted = TicketSort.ByDue(done.Values, ticket => ticket);
        var pendingSorted = TicketSort.ByDue(pending.Values, ticket => ticket);

        var todayEntries = queue.Where(entry => entry.Date == todayApiDate).ToList();
        var todayKeys = todayEntries.SelectMany(entry => entry.Tickets).ToHashSet();

        var untilLogTime = logTime.ToTimeSpan() - nowHcm.TimeOfDay;
        TimeSpan? untilLogTimeOrNull = untilLogTime > TimeSpan.Zero ? untilLogTime : null;

        var inProgress = startedSorted
            .Select(ticket => (ticket, PrSummary(ticket.Key, prTracking)))
            .ToList();

        var attention = new List<AttentionItem>();
        AddReadyToTest(attention, startedSorted, TicketSection.InProgress, prTracking);
        AddReadyToTest(attention, doneSorted, TicketSection.Done, prTracking);
        foreach (var ticket in startedSorted.Where(ticket => ticket.IsStatusDone))
            attention.Add(new AttentionItem(AttentionKind.JiraDone, ticket, null, 0, null));
        foreach (var ticket in startedSorted.Concat(pendingSorted).Where(ticket => ticket.GetDueUrgency(today) == DueUrgency.Overdue))
            attention.Add(new AttentionItem(AttentionKind.Overdue, ticket, null, 0, null));
        foreach (var alert in commentAlerts.Values.OrderByDescending(alert => alert.LastAtHcm))
        {
            var ticket = FindTicket(alert.TicketKey, pending, started, done);
            if (ticket != null)
                attention.Add(new AttentionItem(AttentionKind.PrComments, ticket, null, alert.NewCommentCount, alert.PrUrl));
        }

        return new DashboardData(
            queue.Sum(entry => entry.Tickets.Count),
            todayKeys.Count,
            todayKeys,
            untilLogTimeOrNull,
            inProgress,
            attention,
            pendingSorted);
    }

    // "BE main ✓ · dev ● | FE dev ✓": per repo (BE first), the latest PR of each branch (completed ✓,
    // active ●, abandoned ✕; anything else "?"), main first.
    internal static string PrSummary(string ticketKey, IReadOnlyDictionary<string, TrackedPr> prTracking)
    {
        var latestPerRepoBranch = new Dictionary<(string Repo, string Branch), (int PullRequestId, TrackedPr Pr)>();
        foreach (var entry in prTracking)
        {
            var parts = entry.Key.Split('|');
            if (parts.Length != 3 || parts[0] != ticketKey) continue;
            if (!int.TryParse(parts[2], out var pullRequestId)) continue;

            var repoBranch = (RepoLabel(entry.Value.Repository), parts[1]);
            var isNewer = !latestPerRepoBranch.TryGetValue(repoBranch, out var existing) || pullRequestId > existing.PullRequestId;
            if (isNewer)
                latestPerRepoBranch[repoBranch] = (pullRequestId, entry.Value);
        }

        return string.Join(" | ", latestPerRepoBranch
            .GroupBy(rb => rb.Key.Repo)
            .OrderBy(group => group.Key == "BE" ? 0 : 1)
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key + " " + string.Join(" · ", group
                .OrderBy(rb => rb.Key.Branch == MainBranchLabel ? 0 : 1)
                .ThenBy(rb => rb.Key.Branch, StringComparer.Ordinal)
                .Select(rb => $"{rb.Key.Branch} {StatusGlyph(rb.Value.Pr.Status)}"))));
    }

    // Backend-DotNet (or an entry saved before repos were tracked) is "BE"; an Angular repo is "FE".
    internal static string RepoLabel(string? repository)
    {
        if (repository == null || repository == AzureDevOpsClient.BackendRepository) return "BE";
        if (repository.EndsWith("-Angular", StringComparison.OrdinalIgnoreCase)) return "FE";
        return repository;
    }

    private static string StatusGlyph(string status) => status switch
    {
        "completed" => "✓",
        "active" => "●",
        "abandoned" => "✕",
        _ => "?",
    };

    private static void AddReadyToTest(
        List<AttentionItem> attention,
        IEnumerable<JiraTicket> tickets,
        TicketSection section,
        IReadOnlyDictionary<string, TrackedPr> prTracking)
    {
        foreach (var ticket in tickets)
        {
            var label = JiraStatusFlow.ReadyToTestLabel(ticket, section, PrTracker.IsDevPrMerged(prTracking, ticket.Key));
            if (label != null)
                attention.Add(new AttentionItem(AttentionKind.ReadyToTest, ticket, label, 0, null));
        }
    }

    private static JiraTicket? FindTicket(
        string key,
        IReadOnlyDictionary<string, JiraTicket> pending,
        IReadOnlyDictionary<string, JiraTicket> started,
        IReadOnlyDictionary<string, JiraTicket> done)
    {
        if (pending.TryGetValue(key, out var pendingTicket)) return pendingTicket;
        if (started.TryGetValue(key, out var startedTicket)) return startedTicket;
        return done.TryGetValue(key, out var doneTicket) ? doneTicket : null;
    }
}
