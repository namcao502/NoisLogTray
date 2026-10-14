namespace NoisLogTray;

// The Dashboard's to-do list: what the user should do next, built from local data, the week's coverage and the Claude
// waits. Pure, so the page only draws it.
internal static class DashboardTodos
{
    // week: Monday to today as read, or null until the read is done; busyKeys: tickets a Start / Open / Ready to Test runs on.
    internal static List<TodoItem> Build(DashboardData data, IReadOnlyList<ClaudeWait> waits, IReadOnlyList<DayCoverage>? week,
        IReadOnlyList<string> cleanupKeys, IReadOnlySet<string> busyKeys, DateTimeOffset nowHcm, TimeOnly logTime,
        bool isTodayWritten, bool isDraining)
    {
        var todos = new List<TodoItem>();
        // Claude blocked on a permission or a question first; a finished turn can wait a little.
        todos.AddRange(waits
            .OrderBy(cw => cw.Reason == ClaudeWaitReason.Finished ? 1 : 0)
            .ThenBy(cw => cw.Label, StringComparer.Ordinal)
            .Select(cw => new TodoItem(TodoKind.ClaudeWaiting, Wait: cw)));
        todos.AddRange(data.TicketTodos);

        var today = DateOnly.FromDateTime(nowHcm.DateTime);
        var todayCoverage = week?.FirstOrDefault(dc => dc.Date == today);
        // Not knowing whether today is a day off, offer nothing: queueing a day off would log it.
        var queueKeys = data.InProgress
            .Select(ip => ip.Ticket)
            .Where(jt => !jt.IsStatusDone && !busyKeys.Contains(jt.Key) && TicketQueue.CanQueue(jt.Key))
            .Select(jt => jt.Key)
            .ToList();
        var canQueueToday = TicketQueue.TodayRefusal(nowHcm, logTime) == null && data.TodayQueuedCount == 0 && !isTodayWritten
            && !isDraining && todayCoverage != null && !todayCoverage.IsOff;
        if (canQueueToday && queueKeys.Count != 0) todos.Add(new TodoItem(TodoKind.QueueToday, TicketKeys: queueKeys));

        if (week != null)
        {
            // Today waits for its own queue entry and a running drain before it counts as missed.
            var isTodayLeftToLog = data.TodayQueuedCount == 0 && !isDraining;
            todos.AddRange(WeekStatus(week, nowHcm, logTime).Unlogged
                .Where(dc => dc.Date != today || isTodayLeftToLog)
                .Select(dc => new TodoItem(TodoKind.UnloggedDay, Day: dc)));
        }
        if (cleanupKeys.Count != 0) todos.Add(new TodoItem(TodoKind.WorktreeCleanup, TicketKeys: cleanupKeys));
        return todos.OrderBy(ti => ti.Kind).ToList();
    }

    // The weekdays that should be logged by now (before today, and today once HRM takes it and the auto-log time has
    // passed), and those of them still short of a full log or an OFF marker.
    internal static (IReadOnlyList<DayCoverage> Due, IReadOnlyList<DayCoverage> Unlogged) WeekStatus(IReadOnlyList<DayCoverage> week,
        DateTimeOffset nowHcm, TimeOnly logTime)
    {
        var today = DateOnly.FromDateTime(nowHcm.DateTime);
        var todayOpensAt = logTime > TicketQueue.HrmTodayOpensAt ? logTime : TicketQueue.HrmTodayOpensAt;
        var isTodayDue = TimeOnly.FromDateTime(nowHcm.DateTime) >= todayOpensAt;
        var due = week
            .Where(dc => dc.Date.DayOfWeek != DayOfWeek.Saturday && dc.Date.DayOfWeek != DayOfWeek.Sunday)
            .Where(dc => dc.Date < today || (dc.Date == today && isTodayDue))
            .ToList();
        return (due, due.Where(dc => WeekCard.IsActionable(dc, future: false)).ToList());
    }

    // Everything a to-do row shows, so the page rebuilds its rows only when one of them changed.
    internal static string Signature(IReadOnlyList<TodoItem> todos) =>
        string.Join("\n", todos.Select(ti => string.Join("|", ti.Kind, ti.Ticket?.Key, ti.Ticket?.Summary, ti.Ticket?.Status,
            ti.Ticket?.DueDate, ti.Section, ti.ActionLabel, ti.CommentCount, ti.PrUrl, ti.Day, ti.Wait,
            ti.TicketKeys == null ? null : string.Join(",", ti.TicketKeys))));
}
