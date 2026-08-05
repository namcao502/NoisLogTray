namespace NoisLogTray;

// In-process single-lock mutex for the one Chrome profile (port of
// lib/browser-lock.ts). LaunchPersistentContext locks the profile on disk, so two
// operations against it collide. Reject-fast: a second caller is turned away
// immediately rather than queued.
internal static class BrowserLock
{
    internal const string BusyMessage =
        "An operation is already in progress for this session. Wait for it to finish, then try again.";

    private static int _held; // 0 = free, 1 = held

    internal static bool TryAcquire() => Interlocked.CompareExchange(ref _held, 1, 0) == 0;

    internal static void Release() => Interlocked.Exchange(ref _held, 0);
}
