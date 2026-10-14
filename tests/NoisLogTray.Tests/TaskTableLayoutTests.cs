namespace NoisLogTray.Tests;

public class TaskTableLayoutTests
{
    private const int Gap = 10;
    private const int Pad = 8;
    private const int MinSummary = 160;

    private static readonly Dictionary<TaskTableColumn, int> Widths = new()
    {
        [TaskTableColumn.Key] = 70,
        [TaskTableColumn.Status] = 120,
        [TaskTableColumn.Due] = 50,
        [TaskTableColumn.Eta] = 50,
        [TaskTableColumn.Priority] = 20,
        [TaskTableColumn.Logged] = 50,
        [TaskTableColumn.PrMain] = 70,
        [TaskTableColumn.PrDev] = 70,
    };

    private static TaskTableLayout For(int rowWidth) => TaskTableLayout.For(rowWidth, Widths, Gap, Pad, MinSummary);

    // The Work board's columns: every one.
    private static readonly TaskTableColumn[] BoardColumns = Enum.GetValues<TaskTableColumn>();

    [Fact]
    public void For_ShowsEveryColumn_WhenWide()
    {
        var layout = For(800);

        Assert.Equal(BoardColumns, layout.Columns.Keys.OrderBy(ttc => ttc));
        Assert.Equal(204, layout.Columns[TaskTableColumn.Summary].Width);
    }

    [Theory]
    [InlineData(720, "Eta")]
    [InlineData(670, "Eta,Priority")]
    [InlineData(620, "Eta,Priority,Logged")]
    [InlineData(560, "Eta,Priority,Logged,PrMain")]
    [InlineData(500, "Eta,Priority,Logged,PrMain,PrDev")]
    public void For_DropsEtaThenPriorityThenLoggedThenMainThenDev_AsTheRowNarrows(int rowWidth, string droppedNames)
    {
        var dropped = droppedNames.Split(',').Select(Enum.Parse<TaskTableColumn>);
        var layout = For(rowWidth);

        Assert.Equal(BoardColumns.Except(dropped), layout.Columns.Keys.OrderBy(ttc => ttc));
        Assert.True(layout.Columns[TaskTableColumn.Summary].Width >= MinSummary);
    }

    [Theory]
    [InlineData(300, 14)]
    [InlineData(200, 0)]
    public void For_KeepsKeySummaryStatusDue_EvenWhenTooNarrow(int rowWidth, int summaryWidth)
    {
        var layout = For(rowWidth);

        Assert.Equal(new[] { TaskTableColumn.Key, TaskTableColumn.Summary, TaskTableColumn.Status, TaskTableColumn.Due },
            layout.Columns.Keys.OrderBy(ttc => ttc));
        Assert.Equal(summaryWidth, layout.Columns[TaskTableColumn.Summary].Width);
    }

    [Fact]
    public void For_PlacesColumnsLeftToRight_WithoutOverlap()
    {
        var columns = For(800).Columns.OrderBy(entry => entry.Key).Select(entry => entry.Value).ToList();

        Assert.Equal(Pad, columns[0].X);
        for (var columnIndex = 1; columnIndex < columns.Count; columnIndex++)
            Assert.Equal(columns[columnIndex - 1].X + columns[columnIndex - 1].Width + Gap, columns[columnIndex].X);
        Assert.Equal(800 - Pad, columns[^1].X + columns[^1].Width);
    }

    [Fact]
    public void For_UsesOnlyTheGivenColumns()
    {
        var widths = new Dictionary<TaskTableColumn, int>
        {
            [TaskTableColumn.Key] = 70,
            [TaskTableColumn.Status] = 120,
            [TaskTableColumn.Due] = 50,
        };

        var layout = TaskTableLayout.For(600, widths, Gap, Pad, MinSummary);

        Assert.Equal(new[] { TaskTableColumn.Key, TaskTableColumn.Summary, TaskTableColumn.Status, TaskTableColumn.Due },
            layout.Columns.OrderBy(entry => entry.Value.X).Select(entry => entry.Key));
    }
}

