namespace NoisLogTray;

// Work the user may be waiting on (a Jira check, a log, a git listing). While any runs, the header's BusySpinner turns,
// so a slow call never reads as a frozen app. Thread-safe; Changed is raised on the thread that began or ended the work.
internal sealed class BusyWork
{
    internal static BusyWork Shared { get; } = new();

    private readonly object _lock = new();
    private readonly List<Handle> _running = new();

    internal event Action? Changed;

    // The work ends when the returned handle is disposed; a second Dispose does nothing.
    internal IDisposable Begin(string label)
    {
        var handle = new Handle(this, label);
        lock (_lock) _running.Add(handle);
        Changed?.Invoke();
        return handle;
    }

    internal bool IsBusy
    {
        get { lock (_lock) return _running.Count != 0; }
    }

    // Oldest first; overlapping runs of the same work are named once.
    internal IReadOnlyList<string> Labels
    {
        get { lock (_lock) return _running.Select(h => h.Label).Distinct().ToList(); }
    }

    private void End(Handle handle)
    {
        bool removed;
        lock (_lock) removed = _running.Remove(handle);
        if (removed) Changed?.Invoke();
    }

    private sealed class Handle : IDisposable
    {
        private readonly BusyWork _owner;

        internal Handle(BusyWork owner, string label)
        {
            _owner = owner;
            Label = label;
        }

        internal string Label { get; }

        public void Dispose()
        {
            _owner.End(this);
        }
    }
}
