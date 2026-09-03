namespace NoisLogTray;

// Marks approved full-day leave OFF in the shared TSC workbook. Runs on a poll rather
// than at LOG_TIME: leave is approved days ahead, and the marker's value is being early.
internal sealed class OffDaySync : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(2);

    // Only forward: a past day is never touched automatically (use the Log OFF button).
    private const int LookAheadDays = 60;

    private readonly LoggingService _service;
    private readonly Action<string> _log;
    private readonly Action<string> _notify;
    private System.Threading.Timer? _timer;
    private int _syncing; // 0 = idle, 1 = running; guards the timer against the scheduler
    private bool _disposed;

    // Returned on contention, so a collision with the timer tick cannot answer "no off
    // days" and pop the reminder on a day off.
    private volatile IReadOnlyList<DateOnly> _lastKnown = Array.Empty<DateOnly>();

    // Skipped dates never reach OffDayStore, so without this they stay pending and every
    // poll reopens a Graph session to skip them again. Session-scoped, so a restart re-checks.
    private readonly HashSet<DateOnly> _skippedThisSession = new();

    internal OffDaySync(LoggingService service, Action<string> log, Action<string> notify)
    {
        _service = service;
        _log = log;
        _notify = notify;
    }

    internal void Start()
    {
        _log($"[off-sync] Checking approved leave now, then every {PollInterval.TotalHours:0} hours.");
        _timer = new System.Threading.Timer(_ => _ = TickAsync(), null, TimeSpan.Zero, PollInterval);
    }

    private async Task TickAsync()
    {
        if (_disposed) return;
        await SyncAsync();
    }

    // Returns every off date, not just the newly marked ones, so the scheduler can test
    // today. Never throws - a bad read must not block the daily reminder.
    internal async Task<IReadOnlyList<DateOnly>> SyncAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _syncing, 1, 0) != 0)
        {
            _log("[off-sync] A check is already running; using the last known result.");
            return _lastKnown;
        }

        try
        {
            var today = Hcm.Today();
            var offDates = await _service.GetOffDatesAsync(today, today.AddDays(LookAheadDays), _log, ct);
            _lastKnown = offDates;
            if (offDates.Count == 0) return offDates;

            var alreadyMarked = OffDayStore.Read();
            var pending = offDates
                .Where(d => !alreadyMarked.Contains(d) && !_skippedThisSession.Contains(d))
                .ToList();
            if (pending.Count == 0)
            {
                _log("[off-sync] Nothing to mark; all approved leave is already handled.");
                return offDates;
            }

            _log($"[off-sync] Marking {string.Join(", ", pending)} as {TscCells.OffMarker} in TSC ...");
            var result = await _service.LogOffAsync(pending, overwrite: false, _log, ct: ct);

            if (result.Marked.Count != 0)
            {
                OffDayStore.Add(result.Marked);
                _notify($"Marked {string.Join(", ", result.Marked)} as {TscCells.OffMarker} in TSC.");
            }
            if (result.Skipped.Count != 0)
            {
                foreach (var date in result.Skipped) _skippedThisSession.Add(date);
                _log($"[off-sync] Left alone (already hold real work): {string.Join(", ", result.Skipped)}."
                    + " Not retrying until restart.");
            }
            if (!result.Success)
                _log($"[off-sync] {result.Error} Will retry on the next check.");

            return offDates;
        }
        catch (Exception e)
        {
            _log($"[off-sync] Error: {e.Message}");
            AppLogger.Error($"Off-day sync failed: {e}");
            return Array.Empty<DateOnly>();
        }
        finally
        {
            Interlocked.Exchange(ref _syncing, 0);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
    }
}
