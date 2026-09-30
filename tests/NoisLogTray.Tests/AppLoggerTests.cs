using NoisLogTray;

namespace NoisLogTray.Tests;

public class AppLoggerTests
{
    [Fact]
    public void PurgeOldDeletesOnlyLogsPastRetention()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NoisLogTrayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var now = new DateTime(2026, 9, 30, 12, 0, 0);
            string Make(string name, int ageDays)
            {
                var path = Path.Combine(dir, name);
                File.WriteAllText(path, "x");
                File.SetLastWriteTime(path, now.AddDays(-ageDays));
                return path;
            }

            var old = Make("app-2026-08-01.log", 31);
            var legacy = Make("app.log.1", 40);
            var recent = Make("app-2026-09-29.log", 1);
            var other = Make("queue.json", 90);

            AppLogger.PurgeOld(dir, now);

            Assert.False(File.Exists(old));
            Assert.False(File.Exists(legacy));
            Assert.True(File.Exists(recent));
            Assert.True(File.Exists(other));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
