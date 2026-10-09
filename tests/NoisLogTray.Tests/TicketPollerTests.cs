namespace NoisLogTray.Tests;

public class TicketPollerTests : IDisposable
{
    private readonly TempDirectory _stateDir = new();
    private readonly TicketStore _ticketStore;
    private readonly TicketBoard _ticketBoard;
    private readonly TicketPoller _ticketPoller;
    private readonly FakeJiraClient _jira = new();

    public TicketPollerTests()
    {
        _ticketStore = new TicketStore(_stateDir.Path);
        _ticketBoard = new TicketBoard(_ticketStore);
        _ticketPoller = new TicketPoller(_ticketStore, _ticketBoard);
    }

    public void Dispose()
    {
        _stateDir.Dispose();
    }

    [Fact]
    public async Task PollAsync_AddsNewTicketToPending_WhenSeenFileExists()
    {
        _ticketStore.SaveSeen(new List<string> { "MDP-1" });
        _jira.Assigned["MDP-1"] = Ticket("MDP-1");
        _jira.Assigned["MDP-2"] = Ticket("MDP-2");

        var result = await _ticketPoller.PollAsync(_jira);

        Assert.NotNull(result);
        Assert.Equal(new[] { "MDP-2" }, result.NewTickets.Select(t => t.Key));
        Assert.Equal(new[] { "MDP-2" }, _ticketStore.LoadPending().Keys);
        Assert.Contains("MDP-2", _ticketStore.LoadSeen()!);
    }

    [Fact]
    public async Task PollAsync_RecordsBaselineOnly_OnFirstRun()
    {
        _jira.Assigned["MDP-1"] = Ticket("MDP-1");

        var result = await _ticketPoller.PollAsync(_jira);

        Assert.NotNull(result);
        Assert.Empty(result.NewTickets);
        Assert.Empty(_ticketStore.LoadPending());
        Assert.Equal(new[] { "MDP-1" }, _ticketStore.LoadSeen());
    }

    [Fact]
    public async Task PollAsync_RecordsBaselineOnly_WhenSeenCorrupt()
    {
        File.WriteAllText(Path.Combine(_stateDir.Path, ".jira-assignment-seen.json"), "[not json");
        _jira.Assigned["MDP-1"] = Ticket("MDP-1");

        var result = await _ticketPoller.PollAsync(_jira);

        Assert.NotNull(result);
        Assert.Empty(result.NewTickets);
        Assert.Empty(_ticketStore.LoadPending());
        Assert.Equal(new[] { "MDP-1" }, _ticketStore.LoadSeen());
    }

    [Fact]
    public async Task PollAsync_RecordsBaselineOnly_WhenSeenEmptyFile()
    {
        File.WriteAllText(Path.Combine(_stateDir.Path, ".jira-assignment-seen.json"), "");
        _jira.Assigned["MDP-1"] = Ticket("MDP-1");

        var result = await _ticketPoller.PollAsync(_jira);

        Assert.NotNull(result);
        Assert.Empty(result.NewTickets);
        Assert.Empty(_ticketStore.LoadPending());
        Assert.Equal(new[] { "MDP-1" }, _ticketStore.LoadSeen());
    }

    [Fact]
    public async Task PollAsync_SkipsKeyAlreadyOnBoard()
    {
        _ticketStore.SaveSeen(new List<string>());
        _ticketStore.SaveStarted(new Dictionary<string, JiraTicket> { ["MDP-1"] = Ticket("MDP-1") });
        _jira.Assigned["MDP-1"] = Ticket("MDP-1");

        var result = await _ticketPoller.PollAsync(_jira);

        Assert.NotNull(result);
        Assert.Empty(result.NewTickets);
        Assert.Empty(_ticketStore.LoadPending());
        Assert.Contains("MDP-1", _ticketStore.LoadSeen()!);
    }

    [Fact]
    public async Task PollAsync_ReportsFinished_WhenStartedTicketTurnsDone()
    {
        _ticketStore.SaveSeen(new List<string> { "MDP-1" });
        _ticketStore.SaveStarted(new Dictionary<string, JiraTicket> { ["MDP-1"] = Ticket("MDP-1", "indeterminate") });
        _jira.Live["MDP-1"] = Ticket("MDP-1", "done");

        var result = await _ticketPoller.PollAsync(_jira);

        Assert.NotNull(result);
        Assert.Equal(new[] { "MDP-1" }, result.FinishedTickets.Select(t => t.Key));
        Assert.True(_ticketStore.LoadStarted()["MDP-1"].IsStatusDone);
    }

    [Fact]
    public async Task PollAsync_DoesNotRefetchDoneTickets()
    {
        _ticketStore.SaveSeen(new List<string> { "MDP-1" });
        _ticketStore.SaveDone(new Dictionary<string, JiraTicket> { ["MDP-1"] = Ticket("MDP-1", "done") });

        var result = await _ticketPoller.PollAsync(_jira);

        Assert.NotNull(result);
        Assert.Empty(result.FinishedTickets);
        Assert.Empty(_jira.FetchedKeys);
    }

    [Fact]
    public async Task PollAsync_ReturnsNull_WhenAssignedQueryThrows()
    {
        _jira.AssignedException = new HttpRequestException("boom");

        var result = await _ticketPoller.PollAsync(_jira);

        Assert.Null(result);
        Assert.False(_ticketStore.HasSeenFile());
    }

    [Fact]
    public async Task PollAsync_ReturnsNull_WhenAssignedQueryTimesOut()
    {
        _jira.AssignedException = new TaskCanceledException("timeout");

        var result = await _ticketPoller.PollAsync(_jira);

        Assert.Null(result);
    }

    [Fact]
    public async Task PollAsync_SkipsOffPollTicket_WhenFetchTimesOut()
    {
        _ticketStore.SaveSeen(new List<string>());
        _ticketStore.SaveStarted(new Dictionary<string, JiraTicket> { ["MDP-1"] = Ticket("MDP-1") });
        _jira.FetchException = new TaskCanceledException("timeout");

        var result = await _ticketPoller.PollAsync(_jira);

        Assert.NotNull(result);
        Assert.Empty(result.FinishedTickets);
    }

    [Fact]
    public async Task PollAsync_ReportsNothing_OnSecondPoll()
    {
        _ticketStore.SaveSeen(new List<string>());
        _ticketStore.SaveStarted(new Dictionary<string, JiraTicket> { ["MDP-1"] = Ticket("MDP-1", "indeterminate") });
        _jira.Assigned["MDP-2"] = Ticket("MDP-2");
        _jira.Live["MDP-1"] = Ticket("MDP-1", "done");

        var first = await _ticketPoller.PollAsync(_jira);
        var second = await _ticketPoller.PollAsync(_jira);

        Assert.Single(first!.NewTickets);
        Assert.Single(first.FinishedTickets);
        Assert.Empty(second!.NewTickets);
        Assert.Empty(second.FinishedTickets);
    }

    [Fact]
    public async Task PollAsync_Throws_WhenCallerCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _ticketPoller.PollAsync(_jira, cts.Token));
    }

    [Fact]
    public async Task PollAsync_DoesNotReAdd_WhenKeyIsDoneOrDismissed()
    {
        _ticketStore.SaveSeen(new List<string>());
        _ticketStore.SaveDone(new Dictionary<string, JiraTicket> { ["MDP-1"] = Ticket("MDP-1", "done") });
        _ticketStore.SaveDismissed(new Dictionary<string, DismissedTicket>
        {
            ["MDP-2"] = new DismissedTicket(Ticket("MDP-2"), TicketSection.New),
        });
        _jira.Assigned["MDP-1"] = Ticket("MDP-1", "done");
        _jira.Assigned["MDP-2"] = Ticket("MDP-2");

        var result = await _ticketPoller.PollAsync(_jira);

        Assert.Empty(result!.NewTickets);
        Assert.Empty(_ticketStore.LoadPending());
    }

    [Fact]
    public async Task PollAsync_RefreshesPendingTicket_WhenAbsentFromAssigned()
    {
        _ticketStore.SaveSeen(new List<string> { "MDP-1" });
        _ticketStore.SavePending(new Dictionary<string, JiraTicket> { ["MDP-1"] = Ticket("MDP-1") });
        _jira.Live["MDP-1"] = Ticket("MDP-1") with { Summary = "Renamed" };

        await _ticketPoller.PollAsync(_jira);

        Assert.Equal("Renamed", _ticketStore.LoadPending()["MDP-1"].Summary);
    }

    [Fact]
    public async Task PollAsync_DoesNotRefetchStartedTicket_WhenStoredAsDone()
    {
        _ticketStore.SaveSeen(new List<string> { "MDP-1" });
        _ticketStore.SaveStarted(new Dictionary<string, JiraTicket> { ["MDP-1"] = Ticket("MDP-1", "done") });

        await _ticketPoller.PollAsync(_jira);

        Assert.Empty(_jira.FetchedKeys);
    }

    private static JiraTicket Ticket(string key, string? statusCategory = null)
    {
        return new JiraTicket(key, "Summary", null, null, StatusCategory: statusCategory);
    }

    private sealed class FakeJiraClient : IJiraClient
    {
        internal Dictionary<string, JiraTicket> Assigned { get; } = new();
        internal Dictionary<string, JiraTicket> Live { get; } = new();
        internal List<string> FetchedKeys { get; } = new();
        internal Exception? AssignedException { get; set; }
        internal Exception? FetchException { get; set; }

        public Task<Dictionary<string, JiraTicket>> GetAssignedTicketsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (AssignedException != null) throw AssignedException;

            return Task.FromResult(Assigned);
        }

        public Task<bool> TransitionAsync(string ticketKey, string targetStatus, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<JiraTicket?> GetTicketAsync(string ticketKey, CancellationToken ct = default)
        {
            FetchedKeys.Add(ticketKey);
            if (FetchException != null) throw FetchException;
            return Task.FromResult(Live.GetValueOrDefault(ticketKey));
        }

        public Task<JiraVerifyResult> VerifyTicketAsync(string ticketId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<JiraSuggestion>> GetMyTicketsAsync(string? jql = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<JqlCheckResult> ValidateJqlAsync(string jql, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
