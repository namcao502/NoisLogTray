using NoisLogTray;

namespace NoisLogTray.Tests;

public class WebViewSetupTests
{
    [Fact]
    public void MakeActive_MovesTheFormsActiveControl_DownToTheClickedView()
    {
        using var form = new Form();
        using var page = new UserControl();
        using var button = new Button();
        using var view = new Control(); // selectable, like the WebView2 control
        page.Controls.Add(button);
        page.Controls.Add(view);
        form.Controls.Add(page);
        form.Show();
        button.Focus();
        Assert.Same(button, WebViewSetup.ActiveLeaf(form));

        WebViewSetup.MakeActive(view);

        Assert.Same(view, WebViewSetup.ActiveLeaf(form));
        Assert.False(button.Focused);
    }

    [Fact]
    public void MakeActive_LeavesTheForm_WhenTheViewIsAlreadyActive()
    {
        using var form = new Form();
        using var page = new UserControl();
        using var view = new Control(); // selectable, like the WebView2 control
        page.Controls.Add(view);
        form.Controls.Add(page);
        form.Show();
        WebViewSetup.MakeActive(view);
        var activeChanges = 0;
        view.Enter += (_, _) => activeChanges++;
        view.Leave += (_, _) => activeChanges++;

        WebViewSetup.MakeActive(view);

        Assert.Equal(0, activeChanges);
        Assert.Same(view, WebViewSetup.ActiveLeaf(form));
    }
}
