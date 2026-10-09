namespace NoisLogTray.Tests;

public class CoverageTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);

    [Fact]
    public void TscStateFor_FutureIsNotDue()
    {
        var map = new Dictionary<DateOnly, string?> { [Today] = "MDP-1" };

        Assert.Equal(TscReadState.NotDue, LoggingService.TscStateFor(Today.AddDays(1), Today, true, true, map));
    }

    [Fact]
    public void TscStateFor_NoTokenNoSniffIsNoToken()
    {
        Assert.Equal(TscReadState.NoToken, LoggingService.TscStateFor(Today, Today, false, false, null));
    }

    [Fact]
    public void TscStateFor_SniffFailedIsFailed()
    {
        Assert.Equal(TscReadState.Failed, LoggingService.TscStateFor(Today, Today, false, true, null));
    }

    [Fact]
    public void TscStateFor_NullMapIsFailed()
    {
        Assert.Equal(TscReadState.Failed, LoggingService.TscStateFor(Today, Today, true, false, null));
    }

    [Fact]
    public void TscStateFor_MissingDateIsFailed()
    {
        var map = new Dictionary<DateOnly, string?> { [Today.AddDays(-1)] = "MDP-1" };

        Assert.Equal(TscReadState.Failed, LoggingService.TscStateFor(Today, Today, true, true, map));
    }

    [Fact]
    public void TscStateFor_PresentIsRead()
    {
        // An empty cell is still a successful read: the key is present with a null value.
        var map = new Dictionary<DateOnly, string?> { [Today] = null };

        Assert.Equal(TscReadState.Read, LoggingService.TscStateFor(Today, Today, true, true, map));
    }
}
