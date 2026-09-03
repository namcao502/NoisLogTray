using NoisLogTray;

namespace NoisLogTray.Tests;

// Covers OffDayStore against a temp settings.json, like AppSettingsTests.
public class OffDayStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"noislog-offday-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { /* ignore */ }
        try { File.Delete(_path + ".bad"); } catch { /* ignore */ }
    }

    [Fact]
    public void MissingFileYieldsNoMarkedDates()
    {
        Assert.Empty(OffDayStore.Read(_path));
    }

    [Fact]
    public void RoundTripsAddedDates()
    {
        var today = Hcm.Today();
        var next = today.AddDays(3);
        OffDayStore.Add(new[] { today, next }, _path);

        var read = OffDayStore.Read(_path);
        Assert.Equal(2, read.Count);
        Assert.Contains(today, read);
        Assert.Contains(next, read);
    }

    [Fact]
    public void MergesWithWhatIsAlreadyStored()
    {
        var today = Hcm.Today();
        OffDayStore.Add(new[] { today.AddDays(1) }, _path);
        OffDayStore.Add(new[] { today.AddDays(2) }, _path);

        var read = OffDayStore.Read(_path);
        Assert.Contains(today.AddDays(1), read);
        Assert.Contains(today.AddDays(2), read);
    }

    [Fact]
    public void DropsPastDatesSinceTheScanWindowNeverLooksBack()
    {
        var today = Hcm.Today();
        OffDayStore.Add(new[] { today.AddDays(-5), today.AddDays(1) }, _path);

        var read = OffDayStore.Read(_path);
        Assert.Equal(new[] { today.AddDays(1) }, read.ToArray());
    }

    [Fact]
    public void PrunesStaleEntriesOnTheNextWrite()
    {
        var today = Hcm.Today();
        var settings = new AppSettings();
        settings.MarkedOffDates.Add(today.AddDays(-10).ToString("yyyy-MM-dd"));
        AppSettings.Save(settings, _path);

        OffDayStore.Add(new[] { today.AddDays(2) }, _path);

        Assert.Equal(new[] { today.AddDays(2) }, OffDayStore.Read(_path).ToArray());
    }

    [Fact]
    public void KeepsTheRestOfSettingsIntact()
    {
        var settings = new AppSettings { Dark = false, WindowX = 42, WindowY = 99 };
        settings.Config["JIRA_EMAIL"] = "you@company.com";
        AppSettings.Save(settings, _path);

        OffDayStore.Add(new[] { Hcm.Today().AddDays(1) }, _path);

        var read = AppSettings.Load(_path);
        Assert.False(read.Dark);
        Assert.Equal(42, read.WindowX);
        Assert.Equal(99, read.WindowY);
        Assert.Equal("you@company.com", read.Config["JIRA_EMAIL"]);
        Assert.Single(read.MarkedOffDates);
    }

    [Fact]
    public void AddingNothingLeavesTheFileAlone()
    {
        OffDayStore.Add(Array.Empty<DateOnly>(), _path);
        Assert.False(File.Exists(_path));
    }
}
