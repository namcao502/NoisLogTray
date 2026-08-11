namespace NoisLogTray;

// Small retry wrapper for transient network failures. Retries only transport-level
// faults - a dropped/refused connection (HttpRequestException) or an HttpClient timeout
// (a TaskCanceledException that is NOT the caller's own cancellation) - with exponential
// backoff plus jitter. HTTP status codes (4xx/5xx) are the caller's to interpret and are
// never retried here, so a non-idempotent request is only ever re-sent when we are
// confident the previous attempt did not reach the server.
internal static class Retry
{
    internal static async Task<T> OnTransientAsync<T>(
        Func<CancellationToken, Task<T>> op,
        Action<string>? onLog = null,
        int attempts = 3,
        CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await op(ct);
            }
            catch (Exception ex) when (attempt < attempts && IsTransient(ex, ct))
            {
                // 200ms, 600ms, ... plus up to 150ms jitter so parallel callers desync.
                var backoff = 200 * (int)Math.Pow(3, attempt - 1) + Random.Shared.Next(0, 150);
                onLog?.Invoke($"[retry] attempt {attempt}/{attempts} failed ({ex.GetType().Name}); retrying in {backoff}ms.");
                await Task.Delay(backoff, ct);
            }
        }
    }

    // Transient = a transport fault we did not ask for. A cancellation the caller
    // requested is never transient (rethrow it). An internal timeout surfaces as an
    // OperationCanceledException with no caller cancellation. A dropped/refused
    // connection is an HttpRequestException, which the MCP layer may wrap - walk the
    // inner-exception chain so a wrapped transport fault is still caught.
    private static bool IsTransient(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        if (ex is OperationCanceledException) return true;
        for (var e = ex; e != null; e = e.InnerException)
            if (e is HttpRequestException) return true;
        return false;
    }
}
