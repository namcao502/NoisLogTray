namespace NoisLogTray.Tests;

public class WindowsScheduledTaskTests
{
    [Fact]
    public void Read_ReturnsNull_ForATaskThatDoesNotExist()
    {
        Assert.Null(WindowsScheduledTask.Read($"NoisLogTray-Missing-{Guid.NewGuid():N}"));
    }

    [Theory]
    [InlineData("PT3H", 180)]
    [InlineData("PT1H30M", 90)]
    [InlineData("PT0S", null)]
    [InlineData("", null)]
    public void ParseTimeLimit_ReadsTheTaskDuration(string text, int? expectedMinutes)
    {
        var expected = expectedMinutes == null ? (TimeSpan?)null : TimeSpan.FromMinutes(expectedMinutes.Value);

        Assert.Equal(expected, WindowsScheduledTask.ParseTimeLimit(text));
    }
}
