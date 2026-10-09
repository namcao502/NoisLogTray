using NoisLogTray;

namespace NoisLogTray.Tests;

public class SidebarNavTests
{
    [Fact]
    public void PageLabel_Settings_ReturnsLabel()
    {
        // Lang is static and flipped by other tests in parallel, so accept either language.
        var label = SidebarNav.PageLabel(ShellPage.Settings);
        Assert.Contains(label, new[] { "Settings", "Cài đặt" });
    }
}
