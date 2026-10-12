namespace NoisLogTray.Tests;

// Text is checked in either language: Lang is static UI state another test class may flip.
public class WeekCardTests
{
    private static readonly DateOnly Day = new(2026, 10, 6);

    private static void AssertText(string actual, string english, string vietnamese)
        => Assert.True(actual == english || actual == vietnamese, $"Unexpected text: {actual}");

    [Fact]
    public void TscStatus_Failed_IsNotRead()
    {
        var c = new DayCoverage(Day, 8, null, TscState: TscReadState.Failed);

        var (color, text) = WeekCard.TscStatus(c, false);

        Assert.Equal(WeekCard.Gray, color);
        AssertText(text, "not read", "không đọc được");
    }

    [Fact]
    public void TscStatus_PastOffNotRead_BeatsOff()
    {
        var c = new DayCoverage(Day, null, null, IsOff: true, TscState: TscReadState.NoToken);

        var (color, text) = WeekCard.TscStatus(c, false);

        Assert.Equal(WeekCard.Gray, color);
        AssertText(text, "not read", "không đọc được");
    }

    [Fact]
    public void TscStatus_FutureOff_StillAmber()
    {
        var c = new DayCoverage(Day, null, null, IsOff: true, TscState: TscReadState.NotDue);

        var (color, text) = WeekCard.TscStatus(c, true);

        Assert.Equal(WeekCard.Amber, color);
        AssertText(text, "off - not marked", "nghỉ - chưa đánh dấu");
    }

    [Fact]
    public void TscStatus_Future_StillPending()
    {
        var c = new DayCoverage(Day, null, null, TscState: TscReadState.NotDue);

        var (color, text) = WeekCard.TscStatus(c, true);

        Assert.Equal(WeekCard.Gray, color);
        AssertText(text, "pending", "chưa tới");
    }

    [Fact]
    public void IsActionable_FailedWeekday_NotActionableForTsc()
    {
        // HRM is fully logged, so only an unread TSC cell could make the row clickable.
        var c = new DayCoverage(Day, 8, null, TscState: TscReadState.Failed);

        Assert.False(WeekCard.IsActionable(c, false));
    }

    [Fact]
    public void IsActionable_PastOffFailed_NotActionable()
    {
        var c = new DayCoverage(Day, null, null, IsOff: true, TscState: TscReadState.Failed);

        Assert.False(WeekCard.IsActionable(c, false));
    }

    [Fact]
    public void IsActionable_FutureOffUnmarked_Actionable()
    {
        var c = new DayCoverage(Day, null, null, IsOff: true, TscState: TscReadState.NotDue);

        Assert.True(WeekCard.IsActionable(c, true));
    }
}
