using System.Globalization;
using System.Xml;

namespace NoisLogTray;

// One Windows Task Scheduler task through the Task Scheduler COM API (Schedule.Service), late-bound with dynamic: the
// signed-in user's own tasks need no admin rights, and nothing is parsed from schtasks output in the machine's language.
internal static class WindowsScheduledTask
{
    private const int TaskTriggerDaily = 2;   // TASK_TRIGGER_DAILY
    private const int TaskCreationUpdate = 4; // TASK_UPDATE
    // "Never" (no last run, no next run) comes back as the OLE zero date, 1899-12-30.
    private const int FirstRealYear = 2000;

    // Null when there is no such task; any other failure throws.
    internal static ScheduledTask? Read(string taskName)
    {
        var task = FindTask(ConnectRootFolder(), taskName);
        if (task == null)
        {
            return null;
        }

        dynamic definition = task.Definition;
        TimeOnly? dailyStartTime = null;
        var dailyIntervalDays = 0;
        dynamic triggers = definition.Triggers;
        int triggerCount = triggers.Count;
        if (triggerCount != 0)
        {
            dynamic firstTrigger = triggers[1]; // the collection is 1-based
            int triggerType = firstTrigger.Type;
            if (triggerType == TaskTriggerDaily)
            {
                // The clock time as written in StartBoundary ("2026-09-16T09:00:00+07:00"), whatever its offset.
                var startBoundary = DateTimeOffset.Parse((string)firstTrigger.StartBoundary, CultureInfo.InvariantCulture);
                dailyStartTime = TimeOnly.FromTimeSpan(startBoundary.DateTime.TimeOfDay);
                dailyIntervalDays = firstTrigger.DaysInterval;
            }
        }

        DateTime lastRunTime = task.LastRunTime;
        DateTime nextRunTime = task.NextRunTime;
        int state = task.State;
        int lastTaskResult = task.LastTaskResult;
        string? executionTimeLimit = definition.Settings.ExecutionTimeLimit;
        return new ScheduledTask(
            (ScheduledTaskState)state,
            lastRunTime.Year < FirstRealYear ? null : lastRunTime,
            lastTaskResult,
            nextRunTime.Year < FirstRealYear ? null : nextRunTime,
            ParseTimeLimit(executionTimeLimit),
            dailyStartTime,
            dailyIntervalDays);
    }

    // Starts it now, like Run in Task Scheduler; the task's own policy decides about a run already going.
    internal static void Run(string taskName)
    {
        var task = FindTask(ConnectRootFolder(), taskName);
        if (task == null)
        {
            throw new InvalidOperationException($"There is no task named {taskName}.");
        }

        task.Run(null);
    }

    // TASK_UPDATE re-registers the same definition with only the limit changed, for the task's own user and logon type.
    internal static void SetExecutionTimeLimit(string taskName, TimeSpan limit)
    {
        var rootFolder = ConnectRootFolder();
        var task = FindTask(rootFolder, taskName);
        if (task == null)
        {
            throw new InvalidOperationException($"There is no task named {taskName}.");
        }

        dynamic definition = task.Definition;
        definition.Settings.ExecutionTimeLimit = XmlConvert.ToString(limit);
        rootFolder.RegisterTaskDefinition(task.Path, definition, TaskCreationUpdate, null, null, definition.Principal.LogonType);
    }

    // "PT3H" -> 3 hours; empty or "PT0S" means no limit.
    internal static TimeSpan? ParseTimeLimit(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var limit = XmlConvert.ToTimeSpan(text);
        return limit == TimeSpan.Zero ? null : limit;
    }

    private static dynamic ConnectRootFolder()
    {
        var serviceType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("The Task Scheduler service is not available.");
        dynamic service = Activator.CreateInstance(serviceType)
            ?? throw new InvalidOperationException("The Task Scheduler service could not be created.");
        service.Connect();
        return service.GetFolder(@"\");
    }

    // COM reports a missing task as FileNotFoundException (HRESULT 0x80070002).
    private static dynamic? FindTask(dynamic rootFolder, string taskName)
    {
        try
        {
            return rootFolder.GetTask(taskName);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
