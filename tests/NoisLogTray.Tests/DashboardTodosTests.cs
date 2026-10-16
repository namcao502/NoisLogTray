namespace NoisLogTray.Tests;

public class DashboardTodosTests
{
    // 2026-10-14 is a Wednesday.
    private static readonly DateTimeOffset Morning = new(2026, 10, 14, 10, 0, 0, TimeSpan.FromHours(7));
    private static readonly DateTimeOffset Evening = new(2026, 10, 14, 19, 0, 0, TimeSpan.FromHours(7));
    private static readonly TimeOnly SixPm = new(18, 0);
    private static readonly DateOnly Monday = new(2026, 10, 12);
    private static readonly DateOnly Tuesday = new(2026, 10, 13);
    private static readonly DateOnly Wednesday = new(2026, 10, 14);
    private static readonly IReadOnlyDictionary<string, JiraTicket> NoTickets = new Dictionary<string, JiraTicket>();

    private static JiraTicket Ticket(string key, string status = "In Progress", string category = "indeterminate", string? due = null) =>
        new(key, "s", due, null, Status: status, StatusCategory: category);

    private static DashboardData Data(IEnumerable<JiraTicket>? started = null, IReadOnlyList<QueueEntry>? queue = null,
        IReadOnlyDictionary<string, TrackedPr>? prs = null, DateTimeOffset? now = null) =>
        DashboardDataBuilder.Build(NoTickets, (started ?? Array.Empty<JiraTicket>()).ToDictionary(jt => jt.Key), NoTickets,
            prs ?? new Dictionary<string, TrackedPr>(), new Dictionary<string, PrCommentAlert>(), queue ?? new List<QueueEntry>(),
            now ?? Morning, SixPm);

    // Monday through Wednesday, every day fully logged unless replaced.
    private static List<DayCoverage> Week(params DayCoverage[] replaced) =>
        new[] { Monday, Tuesday, Wednesday }
            .Select(date => replaced.FirstOrDefault(dc => dc.Date == date) ?? new DayCoverage(date, 8, "MDP-1"))
            .ToList();

    private static List<TodoItem> Todos(DashboardData data, IReadOnlyList<DayCoverage>? week, DateTimeOffset? now = null,
        TimeOnly? logTime = null, bool isTodayWritten = false, bool isDraining = false, IReadOnlyList<ClaudeWait>? waits = null,
        IReadOnlyList<string>? cleanupKeys = null, IReadOnlySet<string>? busyKeys = null,
        IReadOnlyList<TeamsAttentionItem>? teamsMessages = null) =>
        DashboardTodos.Build(data, waits ?? Array.Empty<ClaudeWait>(), teamsMessages ?? Array.Empty<TeamsAttentionItem>(), week, cleanupKeys ?? Array.Empty<string>(),
            busyKeys ?? new HashSet<string>(), now ?? Morning, logTime ?? SixPm, isTodayWritten, isDraining);

    private static bool HasQueueToday(List<TodoItem> todos) => todos.Exists(ti => ti.Kind == TodoKind.QueueToday);

    [Fact]
    public void Build_OrdersByKind_ClaudeQuestionsBeforeFinishedTurns()
    {
        var prs = new Dictionary<string, TrackedPr> { ["MDP-1|dev|5"] = new("completed", 0) };
        var data = Data(new[] { Ticket("MDP-1", "Deployed to Dev"), Ticket("MDP-2", due: "2026-10-01") }, prs: prs);
        var waits = new[]
        {
            new ClaudeWait("MDP-7", "MDP-7", ClaudeWaitReason.Finished),
            new ClaudeWait("claude", "Terminal NoisWorkday", ClaudeWaitReason.Permission),
        };

        var todos = Todos(data, Week(new DayCoverage(Monday, 0, null)), waits: waits, cleanupKeys: new[] { "MDP-3" });

        Assert.Equal(
            new[]
            {
                TodoKind.ClaudeWaiting, TodoKind.ClaudeWaiting, TodoKind.ReadyToTest, TodoKind.Overdue, TodoKind.QueueToday,
                TodoKind.UnloggedDay, TodoKind.WorktreeCleanup,
            },
            todos.Select(ti => ti.Kind));
        Assert.Equal("claude", todos[0].Wait!.SessionName);
        Assert.Equal(new[] { "MDP-3" }, todos[^1].TicketKeys);
    }

    private static TeamsAttentionItem TeamsItem(string conversationKey, bool mentionsMe, int minute) =>
        new("MDP-1", conversationKey, null, "", "An", new DateTimeOffset(2026, 10, 14, 9, minute, 0, TimeSpan.FromHours(7)), mentionsMe);

    [Fact]
    public void Build_PutsTeamsMessagesAfterClaudeWaits_MentionsFirst()
    {
        var waits = new[] { new ClaudeWait("MDP-7", "MDP-7", ClaudeWaitReason.Finished) };
        var teamsMessages = new[] { TeamsItem("old", false, 5), TeamsItem("new", false, 30), TeamsItem("mention", true, 1) };

        var todos = Todos(Data(), Week(), waits: waits, teamsMessages: teamsMessages);

        Assert.Equal(
            new[] { TodoKind.ClaudeWaiting, TodoKind.TeamsMessage, TodoKind.TeamsMessage, TodoKind.TeamsMessage },
            todos.Select(ti => ti.Kind));
        Assert.Equal(new[] { "mention", "new", "old" }, todos.Skip(1).Select(ti => ti.TeamsMessage!.ConversationKey));
    }

    [Fact]
    public void Signature_Changes_WhenTeamsMessageChanges()
    {
        var first = new List<TodoItem> { new(TodoKind.TeamsMessage, TeamsMessage: TeamsItem("a", false, 5)) };
        var same = new List<TodoItem> { new(TodoKind.TeamsMessage, TeamsMessage: TeamsItem("a", false, 5)) };
        var newer = new List<TodoItem> { new(TodoKind.TeamsMessage, TeamsMessage: TeamsItem("a", false, 6)) };
        var other = new List<TodoItem> { new(TodoKind.TeamsMessage, TeamsMessage: TeamsItem("b", false, 5)) };

        Assert.Equal(DashboardTodos.Signature(first), DashboardTodos.Signature(same));
        Assert.NotEqual(DashboardTodos.Signature(first), DashboardTodos.Signature(newer));
        Assert.NotEqual(DashboardTodos.Signature(first), DashboardTodos.Signature(other));
    }

    [Fact]
    public void QueueToday_OffersTheQueueableTicketsInProgress()
    {
        var data = Data(new[] { Ticket("MDP-1"), Ticket("MDP-2"), Ticket("MDP-3", category: "done"), Ticket("MDP-4"), Ticket("ABC-5") });

        var todos = Todos(data, Week(), busyKeys: new HashSet<string> { "MDP-4" });

        var queueToday = Assert.Single(todos, ti => ti.Kind == TodoKind.QueueToday);
        Assert.Equal(new[] { "MDP-1", "MDP-2" }, queueToday.TicketKeys);
    }

    [Fact]
    public void QueueToday_IsHidden_WhenTodayCannotTakeAnEntry()
    {
        var started = new[] { Ticket("MDP-1") };
        var saturday = new DateTimeOffset(2026, 10, 17, 10, 0, 0, TimeSpan.FromHours(7));

        Assert.True(HasQueueToday(Todos(Data(started), Week())));
        Assert.False(HasQueueToday(Todos(Data(started, now: saturday), Week(), now: saturday)));
        Assert.False(HasQueueToday(Todos(Data(started), Week(), now: Morning.AddHours(8))));
        Assert.False(HasQueueToday(Todos(Data(started), Week(), logTime: new TimeOnly(9, 30))));
        Assert.False(HasQueueToday(Todos(Data(started, new[] { new QueueEntry("2026-10-14", new[] { "MDP-9" }) }), Week())));
        Assert.False(HasQueueToday(Todos(Data(started), Week(), isTodayWritten: true)));
        Assert.False(HasQueueToday(Todos(Data(started), Week(), isDraining: true)));
        Assert.False(HasQueueToday(Todos(Data(started), Week(new DayCoverage(Wednesday, 0, null, IsOff: true)))));
        Assert.False(HasQueueToday(Todos(Data(started), null)));
        Assert.False(HasQueueToday(Todos(Data(new[] { Ticket("MDP-1", category: "done") }), Week())));
    }

    [Fact]
    public void UnloggedDay_ListsPastWeekdaysThatNeedLogging_ButNotTodayInTheMorning()
    {
        var week = Week(new DayCoverage(Monday, 8, null), new DayCoverage(Wednesday, 0, null));

        var unlogged = Todos(Data(), week).Where(ti => ti.Kind == TodoKind.UnloggedDay).ToList();

        Assert.Equal(new[] { Monday }, unlogged.Select(ti => ti.Day!.Date));
    }

    [Fact]
    public void UnloggedDay_CountsToday_OnceHrmTakesIt_AndNothingIsLeftToDrain()
    {
        var week = Week(new DayCoverage(Wednesday, 0, null));
        var lateMorning = Morning.AddHours(1);

        // LOG_TIME 9:00 has passed, but HRM takes today's hours only from 18:00.
        Assert.DoesNotContain(Todos(Data(now: lateMorning), week, now: lateMorning, logTime: new TimeOnly(9, 0)), ti => ti.Kind == TodoKind.UnloggedDay);
        Assert.Contains(Todos(Data(now: Evening), week, now: Evening, logTime: new TimeOnly(9, 0)), ti => ti.Kind == TodoKind.UnloggedDay);
        Assert.Contains(Todos(Data(now: Evening), week, now: Evening), ti => ti.Kind == TodoKind.UnloggedDay);
        Assert.DoesNotContain(Todos(Data(now: Evening), week, now: Evening, logTime: new TimeOnly(20, 0)), ti => ti.Kind == TodoKind.UnloggedDay);

        var queued = Data(queue: new[] { new QueueEntry("2026-10-14", new[] { "MDP-9" }) }, now: Evening);
        Assert.DoesNotContain(Todos(queued, week, now: Evening), ti => ti.Kind == TodoKind.UnloggedDay);
        Assert.DoesNotContain(Todos(Data(now: Evening), week, now: Evening, isDraining: true), ti => ti.Kind == TodoKind.UnloggedDay);
    }

    [Fact]
    public void WeekStatus_DueDays_SkipTheWeekend_AndTodayUntilItCanBeLogged()
    {
        var saturday = new DateTimeOffset(2026, 10, 17, 20, 0, 0, TimeSpan.FromHours(7));
        var fullWeek = Enumerable.Range(0, 6).Select(offset => new DayCoverage(Monday.AddDays(offset), 0, null)).ToList();

        Assert.Equal(2, DashboardTodos.WeekStatus(Week(), Morning, SixPm).Due.Count);
        Assert.Equal(3, DashboardTodos.WeekStatus(Week(), Evening, SixPm).Due.Count);
        var weekend = DashboardTodos.WeekStatus(fullWeek, saturday, SixPm);
        Assert.Equal(5, weekend.Due.Count);
        Assert.Equal(5, weekend.Unlogged.Count);
        Assert.Empty(DashboardTodos.WeekStatus(Week(), Evening, SixPm).Unlogged);
    }

    [Fact]
    public void WorktreeCleanup_OnlyWithShippedKeys()
    {
        Assert.DoesNotContain(Todos(Data(), Week()), ti => ti.Kind == TodoKind.WorktreeCleanup);
    }

    [Fact]
    public void Signature_ChangesWithWhatARowShows()
    {
        var first = new List<TodoItem> { new(TodoKind.PrComments, Ticket("MDP-1"), CommentCount: 1) };
        var same = new List<TodoItem> { new(TodoKind.PrComments, Ticket("MDP-1"), CommentCount: 1) };
        var more = new List<TodoItem> { new(TodoKind.PrComments, Ticket("MDP-1"), CommentCount: 2) };
        var keys = new List<TodoItem> { new(TodoKind.QueueToday, TicketKeys: new[] { "MDP-1" }) };
        var otherKeys = new List<TodoItem> { new(TodoKind.QueueToday, TicketKeys: new[] { "MDP-2" }) };

        Assert.Equal(DashboardTodos.Signature(first), DashboardTodos.Signature(same));
        Assert.NotEqual(DashboardTodos.Signature(first), DashboardTodos.Signature(more));
        Assert.NotEqual(DashboardTodos.Signature(keys), DashboardTodos.Signature(otherKeys));
    }
}
