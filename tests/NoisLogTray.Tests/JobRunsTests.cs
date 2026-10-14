namespace NoisLogTray.Tests;

public class JobRunsTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly string _filePath;

    public JobRunsTests()
    {
        _filePath = Path.Combine(_directory.Path, "job-runs.json");
    }

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact]
    public void Record_KeepsTheNewestRunsOfAJob_UpToTheLimit()
    {
        var firstStart = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.FromHours(7));
        for (var runNumber = 1; runNumber <= JobRuns.MaxRunsPerJob + 2; runNumber++)
        {
            var startedAt = firstStart.AddMinutes(runNumber);
            JobRuns.Record(AppJob.TicketCheck, new JobRun(startedAt, startedAt.AddSeconds(3), true, $"run {runNumber}"), _filePath);
        }

        var runs = JobRuns.Load(_filePath)[AppJob.TicketCheck];

        Assert.Equal(JobRuns.MaxRunsPerJob, runs.Count);
        Assert.Equal("run 3", runs[0].Summary);
        Assert.Equal($"run {JobRuns.MaxRunsPerJob + 2}", runs[^1].Summary);
    }

    [Fact]
    public void Record_LeavesTheOtherJobsAlone()
    {
        var startedAt = new DateTimeOffset(2026, 10, 14, 18, 0, 0, TimeSpan.FromHours(7));
        JobRuns.Record(AppJob.AutoLog, new JobRun(startedAt, startedAt.AddSeconds(40), false, "1 logged, 1 kept for retry"), _filePath);
        JobRuns.Record(AppJob.CatchUp, new JobRun(startedAt, startedAt, true, "Nothing due"), _filePath);

        var runsByJob = JobRuns.Load(_filePath);

        Assert.False(Assert.Single(runsByJob[AppJob.AutoLog]).Succeeded);
        Assert.Equal("Nothing due", Assert.Single(runsByJob[AppJob.CatchUp]).Summary);
    }

    [Fact]
    public void Load_ReturnsNothing_WhenTheFileIsMissing()
    {
        Assert.Empty(JobRuns.Load(_filePath));
    }

    [Fact]
    public void Load_ReturnsNothing_AndKeepsABadCopy_WhenTheFileIsCorrupt()
    {
        File.WriteAllText(_filePath, "{not json");

        Assert.Empty(JobRuns.Load(_filePath));
        Assert.True(File.Exists(_filePath + ".bad"));
    }
}
