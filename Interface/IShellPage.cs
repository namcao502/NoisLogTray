namespace NoisLogTray;

// Lifecycle hooks a page hosted in ShellForm receives; implementing it is optional.
internal interface IShellPage
{
    void OnPageShown();         // page became visible, or the window was shown while it is current
    void OnActivatedRefresh();  // window activated while this page is current
    void OnHidden();            // window hidden to tray (sent to every page)
}
