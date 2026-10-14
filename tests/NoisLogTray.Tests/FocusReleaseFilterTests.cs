using NoisLogTray;

namespace NoisLogTray.Tests;

public class FocusReleaseFilterTests
{
    [Fact]
    public void ReleasesTheBox_WhenTheClickLandsOnPlainSurface()
    {
        using var form = new Form();
        using var box = new TextBox();
        using var card = new Panel();
        using var label = new Label();
        form.Controls.AddRange(new Control[] { box, card, label });

        Assert.True(FocusReleaseFilter.ReleasesFocus(card, box));
        Assert.True(FocusReleaseFilter.ReleasesFocus(label, box));
    }

    [Fact]
    public void KeepsTheBox_WhenTheClickIsOnItOrItsFrame()
    {
        using var form = new Form();
        using var host = new RoundedHost();
        using var box = new TextBox();
        host.Controls.Add(box);
        form.Controls.Add(host);

        Assert.False(FocusReleaseFilter.ReleasesFocus(box, box));
        Assert.False(FocusReleaseFilter.ReleasesFocus(host, box));
        Assert.False(FocusReleaseFilter.ReleasesFocus(null, box)); // a WebView2 or another app's window
    }

    [Fact]
    public void KeepsTheBox_WhenTheClickIsInAnotherWindow()
    {
        using var form = new Form();
        using var popup = new Form();
        using var box = new TextBox();
        using var popupSurface = new Panel();
        form.Controls.Add(box);
        popup.Controls.Add(popupSurface);

        Assert.False(FocusReleaseFilter.ReleasesFocus(popupSurface, box));
    }
}
