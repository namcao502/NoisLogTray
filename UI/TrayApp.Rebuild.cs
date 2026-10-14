namespace NoisLogTray;

internal sealed partial class TrayApp
{
    private bool _rebuilding;

    // Settings > "Rebuild and restart": the app keeps running through the build, then quits and the new build opens.
    private async void RebuildAndRestart()
    {
        if (_rebuilding)
        {
            Notify(Lang.T("The app is already rebuilding.", "App đang build lại rồi."), ToolTipIcon.Info);
            return;
        }

        var repoPath = AppRebuild.FindSourceRepo(AppContext.BaseDirectory);
        if (repoPath == null)
        {
            return;
        }

        var confirmed = ConfirmDialog.Ask(_shell, Lang.T("Rebuild and restart?", "Build lại và khởi động lại?"),
            Lang.T($"Builds the app from {repoPath} (a minute or two), then restarts it. Running terminals stop; ticket tabs and the Terminal page resume their Claude session.",
                $"Build app từ {repoPath} (khoảng một hai phút) rồi khởi động lại. Terminal đang chạy sẽ dừng; tab ticket và trang Terminal sẽ resume session Claude."),
            Lang.T("Rebuild", "Build lại"), destructive: true);
        if (!confirmed)
        {
            return;
        }

        _rebuilding = true;
        Notify(Lang.T("Rebuilding the app; it restarts when the build is done.", "Đang build lại app; build xong app sẽ tự khởi động lại."),
            ToolTipIcon.Info);
        AppLogger.Info($"[rebuild] publishing from {repoPath}");
        string? error;
        using (BusyWork.Shared.Begin(Lang.T("Rebuilding the app...", "Đang build lại app...")))
        {
            error = await Task.Run(() => AppRebuild.Publish(repoPath));
        }

        if (error == null && !AppRebuild.StartNewBuild(repoPath))
        {
            error = Lang.T("the new build did not start", "bản mới không khởi động được");
        }
        if (error != null)
        {
            _rebuilding = false;
            AppLogger.Error($"[rebuild] failed: {error}");
            Notify(Lang.T($"Rebuild failed: {error} (see log).", $"Build lại thất bại: {error} (xem log)."), ToolTipIcon.Error);
            return;
        }

        AppLogger.Info("[rebuild] handing over to the new build");
        ShutDown();
    }
}
