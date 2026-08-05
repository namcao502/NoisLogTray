using Microsoft.Win32;

namespace NoisLogTray;

internal sealed record StartupOperationResult(bool Success, string? ErrorMessage);

// Registers the app to launch at user logon via the per-user Run key
// (HKCU\Software\Microsoft\Windows\CurrentVersion\Run). This needs no elevation,
// unlike a scheduled task, which managed/Enterprise machines often block with
// "Access is denied".
internal static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NoisLogTray";

    internal static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (key == null) return false;
            var value = key.GetValue(ValueName) as string;
            return value != null && value.Length != 0;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"StartupService.IsEnabled failed: {ex.Message}");
            return false;
        }
    }

    internal static StartupOperationResult TrySet(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null)
                return Failure(enable, "Could not open the Run registry key.");

            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                    return Failure(enable, "Could not resolve the executable path.");
                // Quote so a path containing spaces still launches correctly.
                key.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
                AppLogger.Info("Start with Windows enabled (Run key).");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                AppLogger.Info("Start with Windows disabled (Run key).");
            }

            return new StartupOperationResult(true, null);
        }
        catch (Exception ex)
        {
            return Failure(enable, ex.Message);
        }
    }

    private static StartupOperationResult Failure(bool enable, string message)
    {
        var verb = enable ? "enable" : "disable";
        AppLogger.Error($"StartupService.TrySet({enable}) failed: {message}");
        return new StartupOperationResult(false, $"Could not {verb} Start with Windows: {message}");
    }
}
