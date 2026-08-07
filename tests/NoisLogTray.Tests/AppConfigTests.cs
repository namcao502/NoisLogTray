using NoisLogTray;

namespace NoisLogTray.Tests;

// Covers AppConfig.ParseLogTime: 12-hour "h:mm tt" (also accepts 24-hour), trimmed,
// with a fail-safe 18:00 fallback so a bad LOG_TIME can never break startup. Expected
// is the canonical 24-hour form for comparison.
public class AppConfigTests
{
    [Theory]
    // 12-hour with AM/PM (the dialog's format)
    [InlineData("6:00 PM", "18:00")]
    [InlineData("12:00 AM", "00:00")]
    [InlineData("12:00 PM", "12:00")]
    [InlineData("9:30 AM", "09:30")]
    [InlineData("6:00 pm", "18:00")]   // lowercase designator
    [InlineData("  7:15 AM  ", "07:15")] // surrounding whitespace trimmed
    // 24-hour still accepted (backward compatible / hand-edited .env)
    [InlineData("18:00", "18:00")]
    [InlineData("9:30", "09:30")]
    [InlineData("08:05", "08:05")]
    // Invalid -> default 18:00
    [InlineData(null, "18:00")]        // missing
    [InlineData("", "18:00")]          // blank
    [InlineData("6pm", "18:00")]       // no minutes
    [InlineData("13:00 PM", "18:00")]  // 13 is not a 12-hour value; trailing PM rejects 24h
    [InlineData("25:00", "18:00")]     // hour out of range
    [InlineData("18:60", "18:00")]     // minute out of range
    [InlineData("1800", "18:00")]      // no colon
    public void ParseLogTimeNormalizesOrFallsBack(string? raw, string expected)
    {
        Assert.Equal(expected, AppConfig.ParseLogTime(raw).ToString("HH:mm"));
    }
}
