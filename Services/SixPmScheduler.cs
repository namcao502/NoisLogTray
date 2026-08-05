namespace NoisLogTray;

// Fires a callback at 18:00 Asia/Ho_Chi_Minh each day, then reschedules for the
// next day. Replaces the external Task Scheduler job + wscript shim: the resident
// tray process owns the timer. HRM rejects future stop times, so today's queue
// only succeeds from 18:00 on (past dates work anytime).
internal sealed class SixPmScheduler : IDisposable
{
    private const int FireHour = 18;

    private readonly Func<Task> _onFire;
    private readonly Action<string>? _log;
    private System.Threading.Timer? _timer;
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

    internal void Start() => ScheduleNext();

    private void ScheduleNext()
    {
        if (_disposed) return;
        var next = NextFireTime();
        var delay = next - Hcm.Now();
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

        _log?.Invoke($"[scheduler] Next auto-log at {next:yyyy-MM-dd HH:mm} (in {delay:hh\\:mm}).");
        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ => _ = FireAsync(), null, delay, Timeout.InfiniteTimeSpan);
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
            ScheduleNext();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
    }
}
