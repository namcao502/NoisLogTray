namespace NoisLogTray;

// How often TrayApp checks Jira and the PRs, picked in Settings; loaded and persisted like Theme and Lang.
internal static class TicketCheckInterval
{
    internal const int DefaultMinutes = 5;
    internal static readonly int[] ChoicesMinutes = [5, 10, 15, 30, 60];

    internal static event Action? Changed;

    internal static int Minutes { get; private set; } = DefaultMinutes;

    // A saved value that is not one of the choices (a hand-edited settings.json) falls back to the default.
    internal static void Load()
    {
        var savedMinutes = AppSettings.Load().TicketCheckMinutes;
        Minutes = savedMinutes != null && ChoicesMinutes.Contains(savedMinutes.Value) ? savedMinutes.Value : DefaultMinutes;
    }

    internal static void Set(int minutes)
    {
        if (minutes == Minutes || !ChoicesMinutes.Contains(minutes))
        {
            return;
        }

        Minutes = minutes;
        var settings = AppSettings.Load(); // read-modify-write to keep other keys
        settings.TicketCheckMinutes = minutes;
        AppSettings.Save(settings);
        Changed?.Invoke();
    }
}
