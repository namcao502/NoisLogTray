namespace NoisLogTray.Tests;

public class WorktreeCleanupTests
{
    private static readonly WorktreeFacts CleanFacts = new(
        UnpushedCommits: 0, DirtyFiles: 0, TerminalRunning: false, InProgress: false, IsBroken: false,
        OtherBranch: null, BranchMerged: true, HasTrackedPrs: false, AllPrsClosed: false, AnyPrCompleted: false,
        MainPrMerged: false, LocalConfigDiffs: 0, InspectFailed: false);

    [Theory]
    [InlineData(@"C:\Project\Backend-DotNet\.claude\worktrees\MDP-1", "MDP-1")]
    [InlineData(@"C:\Project\Backend-DotNet\.claude\worktrees\MDP-1\src\Services", "MDP-1")]
    [InlineData(@"C:\Project\Backend-DotNet", null)]
    public void WorktreeKeyOf_ReadsTheTicketFromAWorktreeFolder(string directory, string? expected)
    {
        Assert.Equal(expected, WorktreeCleanup.WorktreeKeyOf(directory));
    }

    [Fact]
    public void MinedTickets_MarksMined_AndGivingUpUnmarks()
    {
        var log = "2026-10-14 09:00:02 Mining MDP-1 first\n"
            + "2026-10-14 09:00:03 Mining MDP-2 second\n"
            + "2026-10-14 09:20:00   Giving up on MDP-2 after 3 failed attempts; x\n"
            + "2026-10-14 09:30:00 Mining MDP-2 again\n"
            + "2026-10-14 09:40:00 Mining MDP-3 third\r\n"
            + "2026-10-14 09:50:00   Giving up on MDP-3 after 3 failed attempts; x\r\n";

        var mined = WorktreeCleanup.MinedTickets(log, "");

        Assert.Equal(new[] { "MDP-1", "MDP-2" }, mined.OrderBy(k => k));
    }

    [Fact]
    public void MinedTickets_DropsKeysInRetryFile_WithBom()
    {
        var log = "x Mining MDP-1 a\nx Mining MDP-2 b\n";

        var mined = WorktreeCleanup.MinedTickets(log, "\uFEFFMDP-1 1\r\n");

        Assert.Equal(new[] { "MDP-2" }, mined);
    }

    [Fact]
    public void MergedTickets_ReadsEveryKeyInTheMergeSubjects()
    {
        var subjects = "Merged PR 101: MDP-1 add export\n"
            + "Merged PR 102: MDP-22 MDP-23 fix both\r\n"
            + "Merged PR 103: main -> development\n";

        var merged = WorktreeCleanup.MergedTickets(subjects);

        Assert.Equal(new[] { "MDP-1", "MDP-22", "MDP-23" }, merged.OrderBy(k => k));
    }

    [Fact]
    public void Classify_ReadyWhenNothingToReport()
    {
        var (verdict, reasons) = WorktreeCleanup.Classify(CleanFacts);

        Assert.Equal(CleanupVerdict.Ready, verdict);
        Assert.Empty(reasons);
    }

    [Fact]
    public void Classify_KeepChecksInOrder()
    {
        var all = CleanFacts with { InspectFailed = true, TerminalRunning = true, UnpushedCommits = 2 };

        var (verdict, reasons) = WorktreeCleanup.Classify(all);
        Assert.Equal(CleanupVerdict.Keep, verdict);
        Assert.Equal(Lang.T("could not read git (see log)", "không đọc được git (xem log)"), Assert.Single(reasons));

        var (_, terminalReasons) = WorktreeCleanup.Classify(all with { InspectFailed = false });
        Assert.Equal(Lang.T("its terminal is running", "terminal của nó đang chạy"), Assert.Single(terminalReasons));

        var (unpushedVerdict, unpushedReasons) = WorktreeCleanup.Classify(all with { InspectFailed = false, TerminalRunning = false });
        Assert.Equal(CleanupVerdict.Keep, unpushedVerdict);
        Assert.Single(unpushedReasons);
    }

    [Fact]
    public void Classify_CollectsEveryReason()
    {
        var facts = CleanFacts with
        {
            IsBroken = true, DirtyFiles = 3, LocalConfigDiffs = 1, InProgress = true,
            OtherBranch = "MDP-9", BranchMerged = false
        };

        var (verdict, reasons) = WorktreeCleanup.Classify(facts);

        Assert.Equal(CleanupVerdict.Check, verdict);
        Assert.Equal(6, reasons.Count);
        Assert.Contains(reasons, r => r.Contains("MDP-9"));
        Assert.Contains(reasons, r => r.Contains('3'));
    }

    [Theory]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, true, false, false, true)]
    [InlineData(true, false, true, true, true)]
    [InlineData(false, false, false, false, true)]
    [InlineData(false, false, false, true, false)]
    public void Classify_NotShippedRule(bool hasPrs, bool allClosed, bool anyCompleted, bool branchMerged, bool expectReason)
    {
        var facts = CleanFacts with
        {
            HasTrackedPrs = hasPrs, AllPrsClosed = allClosed, AnyPrCompleted = anyCompleted,
            MainPrMerged = false, BranchMerged = branchMerged
        };

        var (verdict, reasons) = WorktreeCleanup.Classify(facts);

        Assert.Equal(expectReason, reasons.Count != 0);
        Assert.Equal(expectReason ? CleanupVerdict.Check : CleanupVerdict.Ready, verdict);
    }

    [Fact]
    public void Classify_NotShippedReasonVariants()
    {
        var abandoned = CleanFacts with { HasTrackedPrs = true, AllPrsClosed = true, AnyPrCompleted = false, BranchMerged = false };
        var mainMergedOpenPr = CleanFacts with { HasTrackedPrs = true, AllPrsClosed = false, MainPrMerged = true, BranchMerged = false };
        var plain = CleanFacts with { BranchMerged = false };

        Assert.Equal(Lang.T("PR abandoned, never merged", "PR bị abandon, chưa merge"), Assert.Single(WorktreeCleanup.Classify(abandoned).Reasons));
        Assert.Equal(Lang.T("merged to main, a PR is still open", "đã merge main, còn PR mở"), Assert.Single(WorktreeCleanup.Classify(mainMergedOpenPr).Reasons));
        Assert.Equal(Lang.T("not merged to main yet", "chưa merge vào main"), Assert.Single(WorktreeCleanup.Classify(plain).Reasons));
    }

    [Fact]
    public void Classify_WithoutTrackedPrs_NeverSaysAbandoned()
    {
        var noPrsUnmerged = CleanFacts with { HasTrackedPrs = false, AllPrsClosed = true, AnyPrCompleted = false, BranchMerged = false };

        Assert.Equal(Lang.T("not merged to main yet", "chưa merge vào main"), Assert.Single(WorktreeCleanup.Classify(noPrsUnmerged).Reasons));
    }

    [Fact]
    public void SlimTargets_SelectsScratchAndExtraData_KeepsCoreFiles()
    {
        using var temp = new TempDirectory();
        var docs = temp.Path;
        Directory.CreateDirectory(Path.Combine(docs, "attachments"));
        Directory.CreateDirectory(Path.Combine(docs, ".pr-scratch"));
        Directory.CreateDirectory(Path.Combine(docs, "sql"));
        Directory.CreateDirectory(Path.Combine(docs, "bugs", "BUG-1", "attachments"));
        File.WriteAllText(Path.Combine(docs, "attachments", "a.sql"), "");
        File.WriteAllText(Path.Combine(docs, "plan.md"), "");
        File.WriteAllText(Path.Combine(docs, "Plan-Review.md"), "");
        File.WriteAllText(Path.Combine(docs, "migrate.sql"), "");
        File.WriteAllText(Path.Combine(docs, "revert.sql"), "");
        File.WriteAllText(Path.Combine(docs, "sql", "prod-check.sql"), "");
        File.WriteAllText(Path.Combine(docs, "sql", "Data.CSV"), "");
        File.WriteAllText(Path.Combine(docs, "bugs", "BUG-1", "test-plan.md"), "");
        File.WriteAllText(Path.Combine(docs, "bugs", "BUG-1", "Translation.sql"), "");
        File.WriteAllText(Path.Combine(docs, "bugs", "BUG-1", "extra.sql"), "");

        var targets = WorktreeCleanup.SlimTargets(docs)
            .Select(t => Path.GetRelativePath(docs, t))
            .OrderBy(t => t)
            .ToList();

        var expected = new[]
        {
            ".pr-scratch",
            "attachments",
            "Plan-Review.md",
            Path.Combine("bugs", "BUG-1", "attachments"),
            Path.Combine("bugs", "BUG-1", "extra.sql"),
            Path.Combine("bugs", "BUG-1", "test-plan.md"),
            Path.Combine("sql", "Data.CSV"),
            Path.Combine("sql", "prod-check.sql")
        }.OrderBy(t => t).ToList();
        Assert.Equal(expected, targets, StringComparer.OrdinalIgnoreCase);
    }
}
