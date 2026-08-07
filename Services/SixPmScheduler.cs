namespace NoisLogTray;

// Fires a callback at a configured daily time (default 18:00) in Asia/Ho_Chi_Minh.
// Replaces the external Task Scheduler job + wscript shim: the resident tray process
// owns the timer. HRM rejects future stop times, so today's queue only succeeds from
// 18:00 on (past dates work anytime).
//
// Rather than arm one long one-shot timer to the next 18:00 (which a sleep or clock
// change can silently skip), it polls every minute and fires the first tick at or
// after the target time. A late wake therefore still fires within ~1 minute.
internal sealed class SixPmScheduler : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);

    private readonly Func<Task> _onFire;
    private readonly Action<string>? _log;
    private readonly object _gate = new(); // guards _fireTime / _nextFire across threads
    private System.Threading.Timer? _timer;
    private TimeOnly _fireTime;
    private DateTimeOffset _nextFire;
    private int _firing; // 0 = idle, 1 = a fire is running (re-entrancy guard)
    private bool _disposed;

    internal SixPmScheduler(TimeOnly fireTime, Func<Task> onFire, Action<string>? log = null)
    {
        _fireTime = fireTime;
        _onFire = onFire;
        _log = log;
    }

    // The next fire instant for a daily time: today if still ahead, else tomorrow.
    private static DateTimeOffset ComputeNextFire(TimeOnly fireTime)
    {
        var now = Hcm.Now();
        var todayFire = new DateTimeOffset(now.Year, now.Month, now.Day, fireTime.Hour, fireTime.Minute, 0, now.Offset);
        return now < todayFire ? todayFire : todayFire.AddDays(1);
    }

    // Change the daily fire time (e.g. after the user edits it) and re-arm the next fire.
    internal void SetFireTime(TimeOnly fireTime)
    {
        DateTimeOffset next;
        lock (_gate) { _fireTime = fireTime; _nextFire = ComputeNextFire(_fireTime); next = _nextFire; }
        _log?.Invoke($"[scheduler] Log time set to {fireTime:HH:mm}; next fire at {next:yyyy-MM-dd HH:mm}.");
    }

    internal void Start()
    {
        DateTimeOffset next;
        lock (_gate) { _nextFire = ComputeNextFire(_fireTime); next = _nextFire; }
        _log?.Invoke($"[scheduler] Next auto-log at {next:yyyy-MM-dd HH:mm} (checking every {CheckInterval.TotalSeconds:0}s).");
        _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.Zero, CheckInterval);
    }

    private void Tick()
    {
        if (_disposed) return;
        DateTimeOffset next;
        lock (_gate) next = _nextFire;
        if (Hcm.Now() < next) return;
        if (Interlocked.CompareExchange(ref _firing, 1, 0) != 0) return; // a fire is already running
        _ = FireAsync();
    }

    private async Task FireAsync()
    {
        try
        {
            _log?.Invoke("[scheduler] Scheduled log time reached.");
            await _onFire();
        }
        catch (Exception e)
        {
            _log?.Invoke($"[scheduler] Error: {e.Message}");
            AppLogger.Error($"Scheduler fire failed: {e}");
        }
        finally
        {
            DateTimeOffset next;
            lock (_gate) { _nextFire = ComputeNextFire(_fireTime); next = _nextFire; }
            _log?.Invoke($"[scheduler] Next auto-log at {next:yyyy-MM-dd HH:mm}.");
            Interlocked.Exchange(ref _firing, 0);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
    }
}
