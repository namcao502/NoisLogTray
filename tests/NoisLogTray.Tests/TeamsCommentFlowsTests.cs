namespace NoisLogTray.Tests;

public class TeamsCommentFlowsTests
{
    private sealed class FakeAzureDevOpsClient : IAzureDevOpsClient
    {
        public Dictionary<int, List<PrThread>> ThreadsByPr { get; } = new();
        public bool Fails { get; set; }
        public List<int> Requested { get; } = new();

        public Task<PrLookupResult> GetTicketPrsAsync(string ticketKey)
        {
            return Task.FromResult(new PrLookupResult(new List<PrStatus>(), true));
        }

        public Task<IReadOnlyList<PrThread>> GetThreadsAsync(string repository, int pullRequestId, CancellationToken cancellationToken)
        {
            Requested.Add(pullRequestId);
            if (Fails)
            {
                throw new HttpRequestException("boom");
            }

            return Task.FromResult<IReadOnlyList<PrThread>>(ThreadsByPr.GetValueOrDefault(pullRequestId) ?? new List<PrThread>());
        }
    }

    private static TeamsCommentFlow Flow(TeamsCommentStage stage, string askMessageId = "m1", bool promptSubmitted = false)
    {
        return new TeamsCommentFlow("MDP-1", "conv", "chat", askMessageId, "Linh", stage, promptSubmitted);
    }

    private static PrThread Thread(int id, PrThreadStatus status)
    {
        return new PrThread(id, status, null, null, false, new List<PrComment>());
    }

    private static ClaudeHookEvent Hook(string eventName, string? agentId = null)
    {
        return new ClaudeHookEvent(eventName, null, null, agentId);
    }

    [Fact]
    public void IsSafePrompt_AcceptsPlainLine_RejectsQuotesDollarBackslashBacktickNewlineAndLong()
    {
        Assert.True(TeamsCommentFlows.IsSafePrompt("pr-comments MDP-1"));
        Assert.False(TeamsCommentFlows.IsSafePrompt("   "));
        Assert.False(TeamsCommentFlows.IsSafePrompt("it's"));
        Assert.False(TeamsCommentFlows.IsSafePrompt("say \"hi\""));
        Assert.False(TeamsCommentFlows.IsSafePrompt("a $b"));
        Assert.False(TeamsCommentFlows.IsSafePrompt("a\\b"));
        Assert.False(TeamsCommentFlows.IsSafePrompt("a`b"));
        Assert.False(TeamsCommentFlows.IsSafePrompt("a\nb"));
        Assert.True(TeamsCommentFlows.IsSafePrompt(new string('a', 500)));
        Assert.False(TeamsCommentFlows.IsSafePrompt(new string('a', 501)));
    }

    [Fact]
    public void Next_MarksPromptSubmitted_OnlyWhenHandedOff()
    {
        Assert.True(TeamsCommentFlows.Next(Flow(TeamsCommentStage.HandedOff), Hook("UserPromptSubmit")).PromptSubmitted);
        Assert.False(TeamsCommentFlows.Next(Flow(TeamsCommentStage.Asked), Hook("UserPromptSubmit")).PromptSubmitted);
        Assert.False(TeamsCommentFlows.Next(Flow(TeamsCommentStage.HandedOff), Hook("Stop")).PromptSubmitted);
    }

    [Fact]
    public void Next_IgnoresSubagentPrompt()
    {
        Assert.False(TeamsCommentFlows.Next(Flow(TeamsCommentStage.HandedOff), Hook("UserPromptSubmit", "agent-1")).PromptSubmitted);
    }

    [Fact]
    public void AfterTurn_Done_WhenNoOpenThreads_OrUnknown()
    {
        Assert.Equal(TeamsCommentStage.Done, TeamsCommentFlows.AfterTurn(Flow(TeamsCommentStage.HandedOff, promptSubmitted: true), 0).Stage);
        var unknown = TeamsCommentFlows.AfterTurn(Flow(TeamsCommentStage.HandedOff, promptSubmitted: true), null);
        Assert.Equal(TeamsCommentStage.Done, unknown.Stage);
        Assert.Equal(0, unknown.OpenThreadCount);
    }

    [Fact]
    public void AfterTurn_StaysHandedOff_WithOpenCount_AndResetsPromptSubmitted()
    {
        var after = TeamsCommentFlows.AfterTurn(Flow(TeamsCommentStage.HandedOff, promptSubmitted: true), 2);

        Assert.Equal(TeamsCommentStage.HandedOff, after.Stage);
        Assert.False(after.PromptSubmitted);
        Assert.Equal(2, after.OpenThreadCount);
    }

    [Fact]
    public void OnAsk_StartsNewAsk_ClearsAskedWhenIAnswer_KeepsHandedOff_ClearsDoneWhenIAnswer()
    {
        var asked = Flow(TeamsCommentStage.Asked, "m2");
        var handedOff = Flow(TeamsCommentStage.HandedOff, "m1");
        var done = Flow(TeamsCommentStage.Done, "m1");

        Assert.Equal(asked, TeamsCommentFlows.OnAsk(null, asked, false));
        Assert.Equal(asked, TeamsCommentFlows.OnAsk(Flow(TeamsCommentStage.Asked, "m1"), asked, false));
        Assert.Equal(handedOff, TeamsCommentFlows.OnAsk(handedOff, asked, false));
        Assert.Equal(done, TeamsCommentFlows.OnAsk(done, asked, false));

        Assert.Null(TeamsCommentFlows.OnAsk(Flow(TeamsCommentStage.Asked), null, true));
        Assert.Null(TeamsCommentFlows.OnAsk(done, null, true));
        Assert.Equal(handedOff, TeamsCommentFlows.OnAsk(handedOff, null, true));
        Assert.Equal(done, TeamsCommentFlows.OnAsk(done, null, false));
        Assert.Null(TeamsCommentFlows.OnAsk(null, null, true));
    }

    [Fact]
    public async Task CountOpenThreadsAsync_SkipsAbandoned_SumsOpen()
    {
        var ado = new FakeAzureDevOpsClient();
        ado.ThreadsByPr[1] = new List<PrThread> { Thread(1, PrThreadStatus.Active), Thread(2, PrThreadStatus.Fixed), Thread(3, PrThreadStatus.Pending) };
        ado.ThreadsByPr[2] = new List<PrThread> { Thread(4, PrThreadStatus.Active) };
        ado.ThreadsByPr[3] = new List<PrThread> { Thread(5, PrThreadStatus.Active) };
        var prs = new List<TicketPr>
        {
            new("Backend-DotNet", "main", 1, "active", 0),
            new("Backend-DotNet", "dev", 2, "active", 0),
            new("Portal-Angular", "main", 3, "abandoned", 0)
        };

        var count = await TeamsCommentFlows.CountOpenThreadsAsync(ado, prs, CancellationToken.None);

        Assert.Equal(3, count);
        Assert.DoesNotContain(3, ado.Requested);
    }

    [Fact]
    public async Task CountOpenThreadsAsync_Null_WhenNoClientOrReadFails()
    {
        var prs = new List<TicketPr> { new("Backend-DotNet", "main", 1, "active", 0) };

        Assert.Null(await TeamsCommentFlows.CountOpenThreadsAsync(null, prs, CancellationToken.None));
        Assert.Null(await TeamsCommentFlows.CountOpenThreadsAsync(new FakeAzureDevOpsClient { Fails = true }, prs, CancellationToken.None));
    }
}
