namespace NoisLogTray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            AppLogger.Error($"Unhandled UI thread exception: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLogger.Error($"Unhandled exception (terminating={e.IsTerminating}): {e.ExceptionObject}");

        AppRebuild.WaitForPreviousInstance(args);
        using var mutex = new Mutex(true, "Global\\NoisLogTray_SingleInstance", out var createdNew);
        if (!createdNew) return;

        AppLogger.Info("NoisLogTray started.");
        AssemblyPreload.Start(typeof(Program).Assembly);
        Theme.Load();
        Lang.Load();
        TicketCheckInterval.Load();
        Application.AddMessageFilter(new FocusReleaseFilter());
        Application.Run(new TrayApp());
    }
}
