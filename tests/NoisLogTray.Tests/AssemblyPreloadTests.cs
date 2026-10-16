using NoisLogTray;

namespace NoisLogTray.Tests;

public class AssemblyPreloadTests
{
    [Fact]
    public void LoadClosure_LoadsWhatTheAppUsesOnlyLater()
    {
        var loaded = AssemblyPreload.LoadClosure(typeof(TrayApp).Assembly);

        // The worktree cleanup's PLINQ is the first use of System.Linq.Parallel, long after startup.
        Assert.Contains("System.Linq.Parallel", loaded);
        Assert.Contains("Microsoft.Web.WebView2.WinForms", loaded);
        Assert.Contains(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.GetName().Name == "System.Linq.Parallel");
    }
}
