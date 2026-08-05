using System.Drawing;

namespace NoisLogTray;

// Loads the embedded application icon (app.ico) at a requested pixel size. The tray
// uses the 16px frame; windows use 32px. Falls back to the system icon if the
// embedded resource is somehow missing.
internal static class AppIcon
{
    internal static Icon Load(int size)
    {
        var asm = typeof(AppIcon).Assembly;
        var name = Array.Find(asm.GetManifestResourceNames(),
            n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
        if (name == null) return SystemIcons.Application;

        using var stream = asm.GetManifestResourceStream(name);
        return stream == null ? SystemIcons.Application : new Icon(stream, new Size(size, size));
    }
}
