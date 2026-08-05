namespace NoisLogTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            AppLogger.Error($"Unhandled UI thread exception: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLogger.Error($"Unhandled exception (terminating={e.IsTerminating}): {e.ExceptionObject}");

        using var mutex = new Mutex(true, "Global\\NoisLogTray_SingleInstance", out var createdNew);
        if (!createdNew) return;

        AppLogger.Info("NoisLogTray started.");
        Theme.Load();
        Application.Run(new TrayApp());
    }
}
