namespace NoisLogTray.Tests;

// Synchronous IProgress: Progress<T> posts to a SynchronizationContext, so its
// reports could arrive after the assertion runs.
public sealed class RecordingProgress<T> : IProgress<T>
{
    public List<T> Reports { get; } = new();

    public void Report(T value)
    {
        Reports.Add(value);
    }
}
