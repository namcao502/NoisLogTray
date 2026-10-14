namespace NoisLogTray;

internal static class DashboardDataBuilder
{
    private const string MainBranchLabel = "main";

    // The teams-post skill's app names; any other "-Angular" repo is named without the suffix.
    private static readonly Dictionary<string, string> FrontendAppNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Portal-Angular"] = "Portal",
        ["ExternalPortal-Angular"] = "External",
        ["LicensorPortal-Angular"] = "Licensor",
        ["MobileApp-Angular"] = "Mobile",
        ["WebApp-Angular"] = "WebApp",
    };

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
            .Select(ticket => (ticket, (IReadOnlyList<TicketPr>)LatestPrs(ticket.Key, prTracking)))
            .ToList();

        var ticketTodos = new List<TodoItem>();
        AddReadyToTest(ticketTodos, startedSorted, TicketSection.InProgress, prTracking);
        AddReadyToTest(ticketTodos, doneSorted, TicketSection.Done, prTracking);
        foreach (var ticket in startedSorted.Where(ticket => ticket.IsStatusDone))
            ticketTodos.Add(new TodoItem(TodoKind.JiraDone, ticket));
        foreach (var ticket in startedSorted.Where(ticket => ticket.GetDueUrgency(today) == DueUrgency.Overdue))
            ticketTodos.Add(new TodoItem(TodoKind.Overdue, ticket));
        foreach (var ticket in pendingSorted.Where(ticket => ticket.GetDueUrgency(today) == DueUrgency.Overdue))
            ticketTodos.Add(new TodoItem(TodoKind.Overdue, ticket, TicketSection.New));
        foreach (var alert in commentAlerts.Values.OrderByDescending(alert => alert.LastAtHcm))
        {
            var ticket = FindTicket(alert.TicketKey, pending, started, done);
            if (ticket != null)
                ticketTodos.Add(new TodoItem(TodoKind.PrComments, ticket, CommentCount: alert.NewCommentCount, PrUrl: alert.PrUrl));
        }

        return new DashboardData(
            queue.Sum(entry => entry.Tickets.Count),
            todayKeys.Count,
            todayKeys,
            untilLogTimeOrNull,
            inProgress,
            pendingSorted,
            ticketTodos,
            ShippedKeys(done, prTracking));
    }

    // Done tickets whose main PRs all merged: the latest per repo, abandoned ones left out, at least one left.
    internal static List<string> ShippedKeys(IReadOnlyDictionary<string, JiraTicket> done, IReadOnlyDictionary<string, TrackedPr> prTracking)
    {
        var shippedKeys = new List<string>();
        foreach (var ticketKey in done.Keys.Order(StringComparer.Ordinal))
        {
            var mainPrs = LatestPrs(ticketKey, prTracking).Where(tp => tp.Branch == MainBranchLabel && tp.Status != "abandoned").ToList();
            if (mainPrs.Count != 0 && mainPrs.All(tp => tp.Status == "completed")) shippedKeys.Add(ticketKey);
        }
        return shippedKeys;
    }

    // The latest PR per repo + branch; BE first, then repo name, main before other branches.
    internal static List<TicketPr> LatestPrs(string ticketKey, IReadOnlyDictionary<string, TrackedPr> prTracking)
    {
        var latest = new Dictionary<(string Repository, string Branch), TicketPr>();
        foreach (var entry in prTracking)
        {
            var parts = entry.Key.Split('|');
            if (parts.Length != 3 || parts[0] != ticketKey || !int.TryParse(parts[2], out var pullRequestId)) continue;
            var repository = entry.Value.Repository ?? AzureDevOpsClient.BackendRepository;
            if (latest.TryGetValue((repository, parts[1]), out var existing) && existing.PullRequestId >= pullRequestId) continue;
            latest[(repository, parts[1])] = new TicketPr(repository, parts[1], pullRequestId, entry.Value.Status, entry.Value.CommentCount);
        }
        return latest.Values
            .OrderBy(pr => pr.RepoLabel == "BE" ? 0 : 1).ThenBy(pr => pr.RepoName, StringComparer.Ordinal)
            .ThenBy(pr => pr.Branch == MainBranchLabel ? 0 : 1).ThenBy(pr => pr.Branch, StringComparer.Ordinal)
            .ToList();
    }

    // Backend-DotNet (or an entry saved before repos were tracked) is "BE"; an Angular repo is "FE".
    internal static string RepoLabel(string? repository)
    {
        if (repository == null || repository == AzureDevOpsClient.BackendRepository) return "BE";
        if (repository.EndsWith("-Angular", StringComparison.OrdinalIgnoreCase)) return "FE";
        return repository;
    }

    // RepoLabel plus the front-end app ("FE Portal", "FE External"), so two FE repos' PRs stay apart.
    internal static string RepoName(string? repository)
    {
        var label = RepoLabel(repository);
        if (repository == null || label != "FE") return label;
        var appName = FrontendAppNames.GetValueOrDefault(repository) ?? repository[..^"-Angular".Length];
        return $"FE {appName}";
    }

    internal static string StatusGlyph(string status) => status switch
    {
        "completed" => "✓",
        "active" => "●",
        "abandoned" => "✕",
        _ => "?",
    };

    private static void AddReadyToTest(
        List<TodoItem> ticketTodos,
        IEnumerable<JiraTicket> tickets,
        TicketSection section,
        IReadOnlyDictionary<string, TrackedPr> prTracking)
    {
        foreach (var ticket in tickets)
        {
            var label = JiraStatusFlow.ReadyToTestLabel(ticket, section, PrTracker.IsDevPrMerged(prTracking, ticket.Key));
            if (label != null)
                ticketTodos.Add(new TodoItem(TodoKind.ReadyToTest, ticket, section, label));
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
