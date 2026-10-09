namespace NoisLogTray.Tests;

// A throwaway folder under %TEMP%, deleted on Dispose.
public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "NoisLogTray.Tests", Guid.NewGuid().ToString("N"));

    public TempDirectory()
    {
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        if (!Directory.Exists(Path)) return;

        foreach (var filePath in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            File.SetAttributes(filePath, FileAttributes.Normal);

        Directory.Delete(Path, true);
    }
}
