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

    [Theory]
    [InlineData(null, AppConfig.DefaultDevRepoPath)]
    [InlineData("", AppConfig.DefaultDevRepoPath)]
    [InlineData("  ", AppConfig.DefaultDevRepoPath)]
    [InlineData("  D:\\Repo  ", "D:\\Repo")]
    public void ParseDevRepoPathTrimsOrFallsBack(string? raw, string expected)
    {
        Assert.Equal(expected, AppConfig.ParseDevRepoPath(raw));
    }

    private static string WriteTokens(TempDirectory dir, params string[] lines)
    {
        var path = Path.Combine(dir.Path, "tokens.env");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void ImportDevTokens_ImportsMissingKeysEncrypted()
    {
        using var dir = new TempDirectory();
        var path = WriteTokens(dir, "AZURE_DEVOPS_PAT=fake-pat", "TEAMS_WEBHOOK_URL=https://example.test/hook");
        var settings = new AppSettings();

        var changed = AppConfig.ImportDevTokens(settings, path);

        Assert.True(changed);
        Assert.StartsWith("enc:", settings.Config["AZURE_DEVOPS_PAT"]);
        Assert.Equal("fake-pat", Secrets.Unprotect(settings.Config["AZURE_DEVOPS_PAT"]));
        Assert.Equal("https://example.test/hook", Secrets.Unprotect(settings.Config["TEAMS_WEBHOOK_URL"]));
    }

    [Fact]
    public void ImportDevTokens_KeepsExistingKey_EvenWhenEmpty()
    {
        using var dir = new TempDirectory();
        var path = WriteTokens(dir, "AZURE_DEVOPS_PAT=fake-pat", "TEAMS_WEBHOOK_URL=https://example.test/hook");
        var settings = new AppSettings();
        settings.Config["AZURE_DEVOPS_PAT"] = "";

        AppConfig.ImportDevTokens(settings, path);

        Assert.Equal("", settings.Config["AZURE_DEVOPS_PAT"]);
        Assert.True(settings.Config.ContainsKey("TEAMS_WEBHOOK_URL"));
    }

    [Fact]
    public void ImportDevTokens_ReturnsFalse_WhenFileMissing()
    {
        using var dir = new TempDirectory();
        var settings = new AppSettings();

        var changed = AppConfig.ImportDevTokens(settings, Path.Combine(dir.Path, "missing.env"));

        Assert.False(changed);
        Assert.Empty(settings.Config);
    }
}
