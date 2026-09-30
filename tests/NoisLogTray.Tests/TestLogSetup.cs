using System.Runtime.CompilerServices;
using NoisLogTray;

namespace NoisLogTray.Tests;

internal static class TestLogSetup
{
    // Runs before any test: code under test that logs (e.g. a corrupt settings.json backup)
    // must not write into the user's real %AppData% log.
    [ModuleInitializer]
    internal static void RedirectAppLog() =>
        AppLogger.DirectoryOverride = Path.Combine(Path.GetTempPath(), "NoisLogTray.Tests", "logs");
}
