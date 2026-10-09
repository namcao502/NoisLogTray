using NoisLogTray;

namespace NoisLogTray.Tests;

public class TaskRowTests
{
    [Fact]
    public void ButtonsOnOwnLine_WhenButtonsOverHalf()
    {
        Assert.True(TaskRow.ButtonsOnOwnLine(400, 250));
        Assert.False(TaskRow.ButtonsOnOwnLine(600, 250));
    }

    [Fact]
    public void ButtonLines_WrapsWhenTooWide()
    {
        var lines = TaskRow.ButtonLines(381, new[] { 240, 70, 70, 70 }, 12, 6);

        Assert.Equal(new[] { 0, 0, 1, 1 }, lines);
    }

    [Fact]
    public void ButtonLines_OversizedButtonGetsOwnLine()
    {
        var lines = TaskRow.ButtonLines(200, new[] { 300, 50 }, 12, 6);

        Assert.Equal(new[] { 0, 1 }, lines);
    }

    [Fact]
    public void ButtonLines_SingleLineWhenFits()
    {
        var lines = TaskRow.ButtonLines(600, new[] { 70, 70, 70 }, 12, 6);

        Assert.Equal(new[] { 0, 0, 0 }, lines);
    }
}
