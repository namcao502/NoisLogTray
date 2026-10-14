using NoisLogTray;

namespace NoisLogTray.Tests;

public class StatTileTests
{
    [Fact]
    public void SetValue_KeepsTheToneValueAndDetail_AndNamesThemForAScreenReader()
    {
        using var tile = new StatTile { Title = "HRM TODAY" };

        tile.SetValue(WeekCard.Amber, "4h / 8h", "half a day");

        Assert.Equal((WeekCard.Amber, "4h / 8h", "half a day"), (tile.Tone, tile.Value, tile.Detail));
        Assert.Equal("HRM TODAY: 4h / 8h. half a day", tile.AccessibleName);
    }

    [Fact]
    public void SetValue_WithoutDetail_LeavesItOutOfTheName()
    {
        using var tile = new StatTile { Title = "TSC TODAY" };

        tile.SetValue(WeekCard.Green, "MDP-1", string.Empty);

        Assert.Equal("TSC TODAY: MDP-1", tile.AccessibleName);
    }
}
