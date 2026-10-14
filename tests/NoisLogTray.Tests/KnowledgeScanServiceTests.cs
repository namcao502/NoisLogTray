namespace NoisLogTray.Tests;

public class KnowledgeScanServiceTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact]
    public void ReadLogTail_ReturnsTheWholeLogFromLineOne_WhenItFits()
    {
        var logPath = Path.Combine(_directory.Path, "scan.log");
        File.WriteAllText(logPath, "line 01\nline 02\n");

        var (text, firstLineNumber) = KnowledgeScanService.ReadLogTail(logPath, 1024);

        Assert.Equal("line 01\nline 02\n", text);
        Assert.Equal(1, firstLineNumber);
    }

    [Fact]
    public void ReadLogTail_DropsTheCutFirstLine_AndNumbersFromTheWholeFile()
    {
        var logPath = Path.Combine(_directory.Path, "scan.log");
        File.WriteAllText(logPath, string.Concat(Enumerable.Range(1, 10).Select(n => $"line {n:00}\n"))); // 8 bytes a line

        var (text, firstLineNumber) = KnowledgeScanService.ReadLogTail(logPath, 28);

        Assert.StartsWith("line 08\n", text);
        Assert.Equal(8, firstLineNumber);
    }

    [Fact]
    public void Read_SaysTheLogIsMissing_AndStillReturns()
    {
        var snapshot = KnowledgeScanService.Read(_directory.Path);

        Assert.True(snapshot.IsLogMissing);
        Assert.Null(snapshot.LogError);
        Assert.Empty(snapshot.Runs);
        Assert.Equal(KnowledgeMiner.ScanLogPath(_directory.Path), snapshot.LogPath);
    }

    [Fact]
    public void Read_ParsesTheRunsOfTheLog()
    {
        var logPath = KnowledgeMiner.ScanLogPath(_directory.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, "2026-10-12 09:00:02 No tickets reached Done in the last 1440 minute(s).\n"
            + "2026-10-12 09:00:03 Daily knowledge scan complete.\n");

        var snapshot = KnowledgeScanService.Read(_directory.Path);

        Assert.False(snapshot.IsLogMissing);
        Assert.True(Assert.Single(snapshot.Runs).IsComplete);
    }
}
