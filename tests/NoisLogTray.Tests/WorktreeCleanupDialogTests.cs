using NoisLogTray;

namespace NoisLogTray.Tests;

public class WorktreeCleanupDialogTests
{
    private static WorktreeCleanupRow Row(string key, CleanupVerdict verdict, bool isMined = false, long slimBytes = 0, bool isMerged = true)
    {
        return new WorktreeCleanupRow(key, [], verdict, [], 0, isMined, isMerged, slimBytes);
    }

    private static WorktreeCleanupDialog NewDialog()
    {
        return new WorktreeCleanupDialog(
            () => Task.FromResult(new List<WorktreeCleanupRow>()),
            key => Task.FromResult(Row(key, CleanupVerdict.Keep)),
            row => Task.FromResult<string?>(null),
            key => Task.FromResult<string?>(null),
            key => Task.FromResult<string?>(null));
    }

    [Fact]
    public void VerdictCell_ColorsReadyGreen_CheckAmber_ElseGray()
    {
        Assert.Equal(WeekCard.Green, WorktreeCleanupDialog.VerdictCell(CleanupVerdict.Ready).Color);
        Assert.Equal(WeekCard.Amber, WorktreeCleanupDialog.VerdictCell(CleanupVerdict.Check).Color);
        Assert.Equal(TaskRow.Gray, WorktreeCleanupDialog.VerdictCell(CleanupVerdict.Keep).Color);
        Assert.Equal(TaskRow.Gray, WorktreeCleanupDialog.VerdictCell(CleanupVerdict.NoWorktree).Color);
        Assert.All(Enum.GetValues<CleanupVerdict>(), cv => Assert.False(string.IsNullOrEmpty(WorktreeCleanupDialog.VerdictCell(cv).Text)));
    }

    [Fact]
    public void DocsText_SaysMinedMergedOrNot_AndTheFreeableSize()
    {
        var mined = WorktreeCleanupDialog.DocsText(Row("MDP-1", CleanupVerdict.Ready, isMined: true));
        var merged = WorktreeCleanupDialog.DocsText(Row("MDP-2", CleanupVerdict.Ready, isMined: false, isMerged: true));
        var notMerged = WorktreeCleanupDialog.DocsText(Row("MDP-3", CleanupVerdict.Ready, isMined: false, isMerged: false));

        Assert.Equal(3, new[] { mined, merged, notMerged }.Distinct().Count());
        Assert.Contains("4.8 MB", WorktreeCleanupDialog.DocsText(Row("MDP-4", CleanupVerdict.NoWorktree, slimBytes: 5_000_000)));
    }

    [Fact]
    public void Columns_PlaceTheTextColumnsLeftToRight_BeforeTheButtons()
    {
        var columns = WorktreeCleanupDialog.Columns(800);

        Assert.True(columns.KeyX < columns.VerdictX);
        Assert.True(columns.VerdictX < columns.DocsX);
        Assert.True(columns.DocsX < columns.ReasonsX);
        Assert.True(columns.ReasonsX < columns.ReasonsRight);
        Assert.True(columns.ReasonsRight < 800);
    }

    [Fact]
    public void ShowRows_KeepRowHasNoRemoveButton()
    {
        using var dialog = NewDialog();

        dialog.ShowRows([Row("MDP-1", CleanupVerdict.Keep), Row("MDP-2", CleanupVerdict.Ready), Row("MDP-3", CleanupVerdict.NoWorktree)]);

        Assert.Null(dialog.RemoveButtonFor("MDP-1"));
        Assert.NotNull(dialog.RemoveButtonFor("MDP-2"));
    }

    [Fact]
    public void ShowRows_NotMinedNoWorktreeRowHasSlimButtonButIsNotCountedInSlimAll()
    {
        using var dialog = NewDialog();

        dialog.ShowRows([Row("MDP-1", CleanupVerdict.Keep), Row("MDP-2", CleanupVerdict.Ready), Row("MDP-3", CleanupVerdict.NoWorktree, isMined: false, slimBytes: 5_000_000)]);

        Assert.NotNull(dialog.SlimButtonFor("MDP-3"));
        Assert.Contains("(0)", dialog.SlimAllButton.Text);
        Assert.False(dialog.SlimAllButton.Enabled);
    }

    [Fact]
    public void ShowRows_MineButtonOnlyOnMergedRowsNotMinedYet()
    {
        using var dialog = NewDialog();

        dialog.ShowRows([
            Row("MDP-1", CleanupVerdict.Ready, isMined: false),
            Row("MDP-2", CleanupVerdict.Ready, isMined: true),
            Row("MDP-3", CleanupVerdict.Check, isMined: false, isMerged: false),
            Row("MDP-4", CleanupVerdict.NoWorktree, isMined: false, slimBytes: 5_000),
            Row("MDP-5", CleanupVerdict.NoWorktree, isMined: false, slimBytes: 5_000, isMerged: false)]);

        Assert.NotNull(dialog.MineButtonFor("MDP-1"));
        Assert.Null(dialog.MineButtonFor("MDP-2"));
        Assert.Null(dialog.MineButtonFor("MDP-3"));
        Assert.NotNull(dialog.MineButtonFor("MDP-4"));
        Assert.Null(dialog.MineButtonFor("MDP-5"));
        Assert.NotNull(dialog.SlimButtonFor("MDP-5"));
    }

    [Fact]
    public void ShowRows_RemoveAllCountsReadyRows()
    {
        using var dialog = NewDialog();

        dialog.ShowRows([Row("MDP-1", CleanupVerdict.Keep), Row("MDP-2", CleanupVerdict.Ready), Row("MDP-3", CleanupVerdict.NoWorktree)]);

        Assert.Contains("(1)", dialog.RemoveAllButton.Text);
        Assert.True(dialog.RemoveAllButton.Enabled);
    }
}
