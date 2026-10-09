namespace NoisLogTray.Tests;

public class TicketBoardTests : IDisposable
{
    private readonly TempDirectory _stateDir = new();
    private readonly TicketStore _ticketStore;
    private readonly TicketBoard _ticketBoard;
    private readonly JiraTicket _ticket = new("MDP-1", "Summary", "2026-09-30", null);

    public TicketBoardTests()
    {
        _ticketStore = new TicketStore(_stateDir.Path);
        _ticketBoard = new TicketBoard(_ticketStore);
    }

    public void Dispose()
    {
        _stateDir.Dispose();
    }

    [Fact]
    public void Start_MovesTicketFromPendingToStarted_WhenTicketIsPending()
    {
        _ticketStore.SavePending(new Dictionary<string, JiraTicket> { ["MDP-1"] = _ticket });

        _ticketBoard.Start("MDP-1");

        Assert.Empty(_ticketStore.LoadPending());
        Assert.Equal(_ticket, _ticketStore.LoadStarted()["MDP-1"]);
    }

    [Fact]
    public void Start_ChangesNothing_WhenTicketIsNotPending()
    {
        _ticketBoard.Start("MDP-1");

        Assert.Empty(_ticketStore.LoadStarted());
    }

    [Fact]
    public void MarkDone_MovesTicketFromStartedToDone_WhenTicketIsStarted()
    {
        _ticketStore.SaveStarted(new Dictionary<string, JiraTicket> { ["MDP-1"] = _ticket });

        _ticketBoard.MarkDone("MDP-1");

        Assert.Empty(_ticketStore.LoadStarted());
        Assert.Equal(_ticket, _ticketStore.LoadDone()["MDP-1"]);
    }

    [Fact]
    public void Add_PutsTicketInPendingAndSeen_WhenTicketIsNotOnBoard()
    {
        _ticketStore.SaveSeen(new List<string>());

        var isAdded = _ticketBoard.Add(_ticket);

        Assert.True(isAdded);
        Assert.Equal(_ticket, _ticketStore.LoadPending()["MDP-1"]);
        Assert.Contains("MDP-1", _ticketStore.LoadSeen()!);
    }

    [Fact]
    public void Add_DoesNotWriteSeen_WhenSeenFileMissing()
    {
        Assert.True(_ticketBoard.Add(_ticket));

        Assert.False(_ticketStore.HasSeenFile());
    }

    [Fact]
    public void Add_DoesNotWriteSeen_WhenSeenFileCorrupt()
    {
        var seenPath = Path.Combine(_stateDir.Path, ".jira-assignment-seen.json");
        File.WriteAllText(seenPath, "[not json");

        Assert.True(_ticketBoard.Add(_ticket));

        Assert.Equal("[not json", File.ReadAllText(seenPath));
    }

    [Theory]
    [InlineData((int)TicketSection.New)]
    [InlineData((int)TicketSection.InProgress)]
    [InlineData((int)TicketSection.Done)]
    public void Add_ReturnsFalse_WhenTicketIsAlreadyInAnySection(int sectionValue)
    {
        var section = (TicketSection)sectionValue;
        SaveToSection(section);

        var isAdded = _ticketBoard.Add(_ticket with { Summary = "Other" });

        Assert.False(isAdded);
        Assert.Equal(_ticket, LoadSection(section)["MDP-1"]);
    }

    [Fact]
    public void Add_ReturnsFalse_WhenTicketIsDismissed()
    {
        _ticketStore.SaveDismissed(new Dictionary<string, DismissedTicket>
        {
            ["MDP-1"] = new DismissedTicket(_ticket, TicketSection.New),
        });

        Assert.False(_ticketBoard.Add(_ticket));
        Assert.Empty(_ticketStore.LoadPending());
    }

    [Theory]
    [InlineData((int)TicketSection.New)]
    [InlineData((int)TicketSection.InProgress)]
    [InlineData((int)TicketSection.Done)]
    public void DismissThenRestore_ReturnsTicketToOriginalSection_WhenDismissedFromAnySection(int sectionValue)
    {
        var section = (TicketSection)sectionValue;
        SaveToSection(section);

        _ticketBoard.Dismiss("MDP-1");

        Assert.Empty(LoadSection(section));
        Assert.Equal(new DismissedTicket(_ticket, section), _ticketStore.LoadDismissed()["MDP-1"]);

        _ticketBoard.Restore("MDP-1");

        Assert.Empty(_ticketStore.LoadDismissed());
        Assert.Equal(_ticket, LoadSection(section)["MDP-1"]);
    }

    [Fact]
    public void Restore_ChangesNothing_WhenTicketWasNotDismissed()
    {
        _ticketBoard.Restore("MDP-1");

        Assert.Empty(_ticketStore.LoadPending());
        Assert.Empty(_ticketStore.LoadStarted());
        Assert.Empty(_ticketStore.LoadDone());
    }

    [Theory]
    [InlineData((int)TicketSection.New)]
    [InlineData((int)TicketSection.InProgress)]
    [InlineData((int)TicketSection.Done)]
    public void UpdateTicketDetails_ReplacesStoredTicket_WhenJiraDetailsChanged(int sectionValue)
    {
        var section = (TicketSection)sectionValue;
        SaveToSection(section);
        var updatedTicket = _ticket with { DueDate = "2026-10-15", Eta = "2026-10-14", DevPriority = 2 };

        var isChanged = _ticketBoard.UpdateTicketDetails(new Dictionary<string, JiraTicket> { ["MDP-1"] = updatedTicket });

        Assert.True(isChanged);
        Assert.Equal(updatedTicket, LoadSection(section)["MDP-1"]);
    }

    [Fact]
    public void UpdateTicketDetails_ReplacesTicketInsideDismissed_WhenJiraDetailsChanged()
    {
        _ticketStore.SaveDismissed(new Dictionary<string, DismissedTicket>
        {
            ["MDP-1"] = new DismissedTicket(_ticket, TicketSection.InProgress),
        });
        var updatedTicket = _ticket with { Summary = "Renamed" };

        _ticketBoard.UpdateTicketDetails(new Dictionary<string, JiraTicket> { ["MDP-1"] = updatedTicket });

        Assert.Equal(new DismissedTicket(updatedTicket, TicketSection.InProgress), _ticketStore.LoadDismissed()["MDP-1"]);
    }

    [Fact]
    public void UpdateTicketDetails_ReturnsFalse_WhenNothingChanged()
    {
        _ticketStore.SaveStarted(new Dictionary<string, JiraTicket> { ["MDP-1"] = _ticket });

        var isChanged = _ticketBoard.UpdateTicketDetails(new Dictionary<string, JiraTicket> { ["MDP-1"] = _ticket });

        Assert.False(isChanged);
    }

    [Fact]
    public void UpdateTicketDetails_KeepsTicket_WhenMissingFromJiraResult()
    {
        // No longer returned (unassigned or closed in Jira): left alone, the user removes it manually.
        _ticketStore.SaveStarted(new Dictionary<string, JiraTicket> { ["MDP-1"] = _ticket });

        _ticketBoard.UpdateTicketDetails(new Dictionary<string, JiraTicket>());

        Assert.Equal(_ticket, _ticketStore.LoadStarted()["MDP-1"]);
    }

    private void SaveToSection(TicketSection section)
    {
        var tickets = new Dictionary<string, JiraTicket> { ["MDP-1"] = _ticket };
        switch (section)
        {
            case TicketSection.InProgress:
                _ticketStore.SaveStarted(tickets);
                break;
            case TicketSection.Done:
                _ticketStore.SaveDone(tickets);
                break;
            default:
                _ticketStore.SavePending(tickets);
                break;
        }
    }

    private Dictionary<string, JiraTicket> LoadSection(TicketSection section)
    {
        switch (section)
        {
            case TicketSection.InProgress:
                return _ticketStore.LoadStarted();
            case TicketSection.Done:
                return _ticketStore.LoadDone();
            default:
                return _ticketStore.LoadPending();
        }
    }
}
