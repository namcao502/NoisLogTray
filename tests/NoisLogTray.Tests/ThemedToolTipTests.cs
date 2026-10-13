using System.Drawing;
using NoisLogTray;

namespace NoisLogTray.Tests;

public class ThemedToolTipTests
{
    [Fact]
    public void IsCut_OnlyWhenTheTextDoesNotFitTheLabel()
    {
        using var label = new Label { Font = new Font("Segoe UI", 9F), Text = "A ticket summary that is far too long to fit", Size = new Size(60, 18) };

        Assert.True(ThemedToolTip.IsCut(label));
        label.Size = new Size(600, 18);
        Assert.False(ThemedToolTip.IsCut(label));
    }

    [Fact]
    public void Measure_PadsTheText_AndWrapsALongOne()
    {
        var shortTip = ThemedToolTip.Measure("Dismiss");
        var longTip = ThemedToolTip.Measure(string.Join(" ", Enumerable.Repeat("word", 200)));

        Assert.True(shortTip.Width > TextRenderer.MeasureText("Dismiss", new Font("Segoe UI", 9F)).Width);
        Assert.True(longTip.Height > shortTip.Height * 3);
        Assert.True(longTip.Width < 1000);
    }

    [Fact]
    public void Track_KeepsTheTipOnTheLabelsText_AndDropsItOnDispose()
    {
        var label = new Label { Text = "first" };
        ThemedToolTip.Track(label);

        label.Text = "second";
        Assert.Equal("second", ThemedToolTip.FullText.GetToolTip(label));
        Assert.True(label.AutoEllipsis);

        label.Dispose();
        Assert.Equal("", ThemedToolTip.FullText.GetToolTip(label));
    }
}
