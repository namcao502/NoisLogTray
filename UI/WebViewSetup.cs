using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NoisLogTray;

// One WebView2 environment for the app (one user-data folder); every view serves Assets/ on one virtual host.
internal static class WebViewSetup
{
    private const string HostName = "noislogtray.assets";
    private static Task<CoreWebView2Environment>? _environment;

    internal static string PageUrl(string relativePath)
    {
        return $"https://{HostName}/{relativePath}";
    }

    // UI thread. Throws what CreateAsync/EnsureCoreWebView2Async throw; a failed environment is not cached.
    // Returns early for a view disposed mid-init, so callers still check IsDisposed after the await.
    internal static async Task InitializeAsync(WebView2 view)
    {
        view.DefaultBackgroundColor = Theme.WindowBg; // no white flash before the page paints in dark mode
        _environment ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.DataDirectory, "WebView2"));
        CoreWebView2Environment environment;
        try
        {
            environment = await _environment;
        }
        catch
        {
            _environment = null;
            throw;
        }
        await view.EnsureCoreWebView2Async(environment);
        if (view.IsDisposed) return;
        // A click in the page raises GotFocus but leaves the form's ActiveControl on the control focused before (a tab, a
        // button), which keeps its focus look and takes focus back whenever the window is activated again.
        view.GotFocus += (_, _) => MakeActive(view);
        var core = view.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false; // no F5 reload / Ctrl+F find
        core.Settings.IsStatusBarEnabled = false;
        core.SetVirtualHostNameToFolderMapping(HostName,
            Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Deny);
        // A Ctrl+click on a URL in a diff or the terminal must not open a popup or leave the app's pages.
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.NavigationStarting += (_, e) =>
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || uri.Host != HostName) e.Cancel = true;
        };
    }

    // Compared with the innermost active control: the form's own ActiveControl is the page holding the view, and setting
    // it again would focus the view again, which raises GotFocus again.
    internal static void MakeActive(Control control)
    {
        var form = control.FindForm();
        if (form == null || ActiveLeaf(form) == control) return;
        form.ActiveControl = control;
    }

    // The control that has focus as far as WinForms knows: ActiveControl followed down through nested containers.
    internal static Control? ActiveLeaf(ContainerControl container)
    {
        var active = container.ActiveControl;
        while (active is ContainerControl nested && nested.ActiveControl != null) active = nested.ActiveControl;
        return active;
    }
}
