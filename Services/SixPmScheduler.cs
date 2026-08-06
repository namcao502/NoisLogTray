namespace NoisLogTray;

// Fires a callback at 18:00 Asia/Ho_Chi_Minh each day. Replaces the external Task
// Scheduler job + wscript shim: the resident tray process owns the timer. HRM
// rejects future stop times, so today's queue only succeeds from 18:00 on (past
// dates work anytime).
//
// Rather than arm one long one-shot timer to the next 18:00 (which a sleep or clock
// change can silently skip), it polls every minute and fires the first tick at or
// after the target time. A late wake therefore still fires within ~1 minute.
internal sealed class SixPmScheduler : IDisposable
{
    private const int FireHour = 18;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);

    private readonly Func<Task> _onFire;
    private readonly Action<string>? _log;
    private System.Threading.Timer? _timer;
    private DateTimeOffset _nextFire;
    private int _firing; // 0 = idle, 1 = a fire is running (re-entrancy guard)
    private bool _disposed;

    internal SixPmScheduler(Func<Task> onFire, Action<string>? log = null)
    {
        _onFire = onFire;
        _log = log;
    }

    internal DateTimeOffset NextFireTime()
    {
        var now = Hcm.Now();
        var todayFire = new DateTimeOffset(now.Year, now.Month, now.Day, FireHour, 0, 0, now.Offset);
        return now < todayFire ? todayFire : todayFire.AddDays(1);
    }

    internal void Start()
    {
        _nextFire = NextFireTime();
        _log?.Invoke($"[scheduler] Next auto-log at {_nextFire:yyyy-MM-dd HH:mm} (checking every {CheckInterval.TotalSeconds:0}s).");
        _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.Zero, CheckInterval);
    }

    private void Tick()
    {
        if (_disposed) return;
        if (Hcm.Now() < _nextFire) return;
        if (Interlocked.CompareExchange(ref _firing, 1, 0) != 0) return; // a fire is already running
        _ = FireAsync();
    }

    private async Task FireAsync()
    {
        try
        {
            _log?.Invoke("[scheduler] 18:00 reached; draining the queue.");
            await _onFire();
        }
        catch (Exception e)
        {
            _log?.Invoke($"[scheduler] Error: {e.Message}");
            AppLogger.Error($"Scheduler fire failed: {e}");
        }
        finally
        {
            _nextFire = NextFireTime();
            _log?.Invoke($"[scheduler] Next auto-log at {_nextFire:yyyy-MM-dd HH:mm}.");
            Interlocked.Exchange(ref _firing, 0);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
    }
}
