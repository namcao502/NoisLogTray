using NoisLogTray;

namespace NoisLogTray.Tests;

public class UiScaleTests
{
    [Fact]
    public void ToFactor_KnownChoices()
    {
        Assert.Equal(1f, UiScale.ToFactor(100));
        Assert.Equal(1.25f, UiScale.ToFactor(125));
        Assert.Equal(1.5f, UiScale.ToFactor(150));
    }

    [Fact]
    public void ToFactor_UnknownFallsBackToDefault()
    {
        Assert.Equal(UiScale.DefaultZoomPercent / 100f, UiScale.ToFactor(137));
        Assert.Equal(UiScale.DefaultZoomPercent / 100f, UiScale.ToFactor(0));
    }
}
