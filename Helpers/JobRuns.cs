using System.Text.Json;

namespace NoisLogTray;

// The last runs of each AppJob, for the Schedules dialog. Own file, not settings.json: the drain records from the threadpool
// and would race the settings saves. A failed write is logged and dropped; it must never fail the job it records.
internal static class JobRuns
{
    internal const int MaxRunsPerJob = 10;
    private static readonly object Gate = new();

    internal static void Record(AppJob job, JobRun run, string? path = null)
    {
        var file = path ?? AppPaths.JobRunsPath;
        lock (Gate)
        {
            try
            {
                var runsByJob = ReadUnlocked(file);
                var jobRuns = runsByJob.GetValueOrDefault(job, new List<JobRun>()).Append(run).TakeLast(MaxRunsPerJob).ToList();
                var updatedRunsByJob = new Dictionary<AppJob, List<JobRun>>(runsByJob) { [job] = jobRuns };
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(updatedRunsByJob));
                File.Move(file + ".tmp", file, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                AppLogger.Error($"[jobs] Recording a {job} run failed: {exception.Message}");
            }
        }
    }

    // Oldest first per job.
    internal static Dictionary<AppJob, List<JobRun>> Load(string? path = null)
    {
        lock (Gate)
        {
            return ReadUnlocked(path ?? AppPaths.JobRunsPath);
        }
    }

    // Missing or unreadable reads as empty; a corrupt file is kept aside as .bad.
    private static Dictionary<AppJob, List<JobRun>> ReadUnlocked(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return new Dictionary<AppJob, List<JobRun>>();
            }

            return JsonSerializer.Deserialize<Dictionary<AppJob, List<JobRun>>>(File.ReadAllText(file))
                ?? new Dictionary<AppJob, List<JobRun>>();
        }
        catch (JsonException exception)
        {
            AppLogger.Error($"[jobs] Corrupt, keeping a .bad copy: {exception.Message}");
            try
            {
                File.Move(file, file + ".bad", overwrite: true);
            }
            catch (Exception moveError) when (moveError is IOException || moveError is UnauthorizedAccessException)
            {
                AppLogger.Error($"[jobs] Could not keep .bad copy: {moveError.Message}");
            }

            return new Dictionary<AppJob, List<JobRun>>();
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            AppLogger.Error($"[jobs] Read failed: {exception.Message}");
            return new Dictionary<AppJob, List<JobRun>>();
        }
    }
}
