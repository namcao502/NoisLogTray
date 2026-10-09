namespace NoisLogTray.Tests;

public class PrTrackerTests
{
    [Fact]
    public void DetectChanges_OnlyRecordsBaseline_WhenPrSeenForFirstTime()
    {
        var tracking = new Dictionary<string, TrackedPr>();

        var notifications = PrTracker.DetectChanges(tracking, "MDP-1", new[] { Pr(1, "main", "completed", 3) });

        Assert.Empty(notifications);
        Assert.Equal(new TrackedPr("completed", 3, "Backend-DotNet"), tracking["MDP-1|main|1"]);
    }

    [Fact]
    public void DetectChanges_ReportsMergeAndNewComments_WhenPrChanged()
    {
        var tracking = new Dictionary<string, TrackedPr> { ["MDP-1|main|1"] = new("active", 1) };

        var notifications = PrTracker.DetectChanges(tracking, "MDP-1", new[] { Pr(1, "main", "completed", 3) });

        var notification = Assert.Single(notifications);
        Assert.True(notification.IsMerged);
        Assert.Equal(2, notification.NewCommentCount);
    }

    [Fact]
    public void DetectChanges_ReportsNothing_WhenTwoPrsOnSameBranchAreUnchanged()
    {
        // Regression: both PRs used to share one "MDP-1|main" key, so each poll
        // compared one against the other and re-posted merge/comment cards.
        var tracking = new Dictionary<string, TrackedPr>();
        var prs = new[] { Pr(1, "main", "abandoned", 0), Pr(2, "main", "completed", 5) };
        PrTracker.DetectChanges(tracking, "MDP-1", prs);

        var notifications = PrTracker.DetectChanges(tracking, "MDP-1", prs);

        Assert.Empty(notifications);
    }

    [Fact]
    public void RemoveLegacyKeys_DropsKeysWithoutPrId()
    {
        var tracking = new Dictionary<string, TrackedPr>
        {
            ["MDP-1|main"] = new("active", 0),
            ["MDP-1|main|1"] = new("active", 0),
        };

        var isChanged = PrTracker.RemoveLegacyKeys(tracking);

        Assert.True(isChanged);
        Assert.Equal(new[] { "MDP-1|main|1" }, tracking.Keys);
    }

    [Fact]
    public void AreAllPrsClosed_ReturnsTrue_WhenEveryPrIsCompletedOrAbandoned()
    {
        var tracking = new Dictionary<string, TrackedPr>
        {
            ["MDP-1|main|1"] = new("completed", 2),
            ["MDP-1|dev|2"] = new("abandoned", 0),
        };

        Assert.True(PrTracker.AreAllPrsClosed(tracking, "MDP-1"));
    }

    [Fact]
    public void AreAllPrsClosed_ReturnsFalse_WhenAnyPrIsActive()
    {
        var tracking = new Dictionary<string, TrackedPr>
        {
            ["MDP-1|main|1"] = new("completed", 2),
            ["MDP-1|dev|2"] = new("active", 0),
        };

        Assert.False(PrTracker.AreAllPrsClosed(tracking, "MDP-1"));
    }

    [Fact]
    public void AreAllPrsClosed_ReturnsFalse_WhenOnlyAKeyWithTheSamePrefixHasPrs()
    {
        // MDP-10's PRs must not count as MDP-1's.
        var tracking = new Dictionary<string, TrackedPr> { ["MDP-10|main|1"] = new("completed", 0) };

        Assert.False(PrTracker.AreAllPrsClosed(tracking, "MDP-1"));
    }

    [Fact]
    public void IsDevPrMerged_ReturnsTrue_WhenADevPrIsCompleted()
    {
        var tracking = new Dictionary<string, TrackedPr>
        {
            ["MDP-1|main|1"] = new("active", 0),
            ["MDP-1|dev|2"] = new("completed", 0),
        };

        Assert.True(PrTracker.IsDevPrMerged(tracking, "MDP-1"));
    }

    [Fact]
    public void IsDevPrMerged_ReturnsFalse_WhenOnlyTheMainPrIsCompleted()
    {
        var tracking = new Dictionary<string, TrackedPr>
        {
            ["MDP-1|main|1"] = new("completed", 0),
            ["MDP-1|dev|2"] = new("active", 0),
        };

        Assert.False(PrTracker.IsDevPrMerged(tracking, "MDP-1"));
    }

    [Fact]
    public void IsDevPrMerged_ReturnsFalse_WhenBackendMergedButFrontendDevPrStillOpen()
    {
        var tracking = new Dictionary<string, TrackedPr>
        {
            ["MDP-1|dev|1"] = new("completed", 0, "Backend-DotNet"),
            ["MDP-1|dev|2"] = new("active", 0, "Portal-Angular"),
        };

        Assert.False(PrTracker.IsDevPrMerged(tracking, "MDP-1"));
    }

    [Fact]
    public void IsDevPrMerged_IgnoresAbandonedDevPrs()
    {
        var tracking = new Dictionary<string, TrackedPr>
        {
            ["MDP-1|dev|1"] = new("completed", 0, "Backend-DotNet"),
            ["MDP-1|dev|2"] = new("abandoned", 0, "Portal-Angular"),
        };

        Assert.True(PrTracker.IsDevPrMerged(tracking, "MDP-1"));
    }

    [Fact]
    public void IsDevPrMerged_IgnoresOtherTickets_WhenKeyIsAPrefixOfAnother()
    {
        var tracking = new Dictionary<string, TrackedPr> { ["MDP-10|dev|2"] = new("completed", 0) };

        Assert.False(PrTracker.IsDevPrMerged(tracking, "MDP-1"));
    }

    private static PrStatus Pr(int pullRequestId, string branch, string status, int commentCount)
    {
        return new PrStatus(pullRequestId, branch, status, commentCount, $"https://example.test/{pullRequestId}");
    }
}
