namespace NoisLogTray.Tests;

public class TscWrittenDatesTests
{
    [Fact]
    public void Record_ThenContains_ReturnsTrue()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "tsc-written.json");

        TscWrittenDates.Record(new[] { new DateOnly(2026, 10, 9) }, path);

        Assert.True(TscWrittenDates.Contains(new DateOnly(2026, 10, 9), path));
    }

    [Fact]
    public void Contains_ReturnsFalse_ForUnrecordedDate()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "tsc-written.json");

        TscWrittenDates.Record(new[] { new DateOnly(2026, 10, 9) }, path);

        Assert.False(TscWrittenDates.Contains(new DateOnly(2026, 10, 8), path));
    }

    [Fact]
    public void Record_KeepsOnly31MostRecentDates()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "tsc-written.json");
        var start = new DateOnly(2026, 1, 1);

        TscWrittenDates.Record(Enumerable.Range(0, 40).Select(offset => start.AddDays(offset)), path);

        Assert.False(TscWrittenDates.Contains(start.AddDays(8), path));
        Assert.True(TscWrittenDates.Contains(start.AddDays(9), path));
        Assert.True(TscWrittenDates.Contains(start.AddDays(39), path));
    }

    [Fact]
    public void Contains_ReturnsFalse_AndKeepsBadCopy_WhenFileCorrupt()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "tsc-written.json");
        File.WriteAllText(path, "{not json");

        Assert.False(TscWrittenDates.Contains(new DateOnly(2026, 10, 9), path));
        Assert.True(File.Exists(path + ".bad"));
    }
}
