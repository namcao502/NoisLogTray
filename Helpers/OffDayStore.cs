namespace NoisLogTray;

// Dates already marked OFF in TSC. Without this record every app start would sniff a
// Graph token - launching headless Chrome - only to find the workbook already correct.
internal static class OffDayStore
{
    private const string Format = "yyyy-MM-dd";
    private static readonly object Gate = new();

    internal static IReadOnlySet<DateOnly> Read(string? path = null)
    {
        var dates = new HashSet<DateOnly>();
        foreach (var raw in AppSettings.Load(path).MarkedOffDates)
            if (DateOnly.TryParseExact(raw, Format, out var date)) dates.Add(date);
        return dates;
    }

    // Read-modify-write like Theme.Toggle, so the theme, window position and Config map
    // survive. Past dates are pruned here: the scan window never looks back at them.
    internal static void Add(IReadOnlyList<DateOnly> dates, string? path = null)
    {
        if (dates.Count == 0) return;

        lock (Gate)
        {
            var settings = AppSettings.Load(path);
            var today = Hcm.Today();
            var kept = new SortedSet<DateOnly>();

            foreach (var raw in settings.MarkedOffDates)
                if (DateOnly.TryParseExact(raw, Format, out var date) && date >= today) kept.Add(date);
            foreach (var date in dates)
                if (date >= today) kept.Add(date);

            settings.MarkedOffDates = kept.Select(d => d.ToString(Format)).ToList();
            AppSettings.Save(settings, path);
        }
    }
}
