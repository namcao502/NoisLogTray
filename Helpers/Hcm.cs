namespace NoisLogTray;

// Everything the old app computed in the Asia/Ho_Chi_Minh timezone (worksheet
// year, day-of-year row, HRM workDate) goes through here. HCM has no DST, so a
// fixed UTC+7 is a correct fallback if the tz database lookup fails.
internal static class Hcm
{
    private static readonly TimeZoneInfo Zone = ResolveZone();

    private static TimeZoneInfo ResolveZone()
    {
        foreach (var id in new[] { "Asia/Ho_Chi_Minh", "SE Asia Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("HCM+7", TimeSpan.FromHours(7), "HCM+7", "HCM+7");
    }

    internal static DateTimeOffset Now() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone);

    internal static DateOnly Today() => DateOnly.FromDateTime(Now().DateTime);

    // HRM workDate (YYYY-MM-DD) for a chosen calendar date.
    internal static string ApiDate(DateOnly date) => date.ToString("yyyy-MM-dd");
}
