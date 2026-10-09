namespace NoisLogTray.Tests;

public class PrCommentAlertsTests
{
    private static readonly DateTimeOffset Earlier = new(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(7));
    private static readonly DateTimeOffset Later = Earlier.AddHours(2);

    [Fact]
    public void Add_SumsCountsAndKeepsLatestUrl()
    {
        var first = PrCommentAlerts.Add(new Dictionary<string, PrCommentAlert>(), "MDP-1", 2, "https://pr/1", Earlier);

        var second = PrCommentAlerts.Add(first, "MDP-1", 3, "https://pr/2", Later);

        Assert.Equal(new PrCommentAlert("MDP-1", 5, "https://pr/2", Later), second["MDP-1"]);
    }

    [Fact]
    public void Add_IgnoresZero()
    {
        var alerts = new Dictionary<string, PrCommentAlert>();

        var result = PrCommentAlerts.Add(alerts, "MDP-1", 0, "https://pr/1", Earlier);

        Assert.Empty(result);
    }

    [Fact]
    public void Remove_DropsOnlyThatTicket()
    {
        var alerts = new Dictionary<string, PrCommentAlert>
        {
            ["MDP-1"] = new("MDP-1", 1, "u1", Earlier),
            ["MDP-2"] = new("MDP-2", 2, "u2", Earlier),
        };

        var result = PrCommentAlerts.Remove(alerts, "MDP-1");

        Assert.Equal(new[] { "MDP-2" }, result.Keys);
    }

    [Fact]
    public void KeepOnly_DropsTicketsOffTheBoard()
    {
        var alerts = new Dictionary<string, PrCommentAlert>
        {
            ["MDP-1"] = new("MDP-1", 1, "u1", Earlier),
            ["MDP-2"] = new("MDP-2", 2, "u2", Earlier),
        };

        var result = PrCommentAlerts.KeepOnly(alerts, new HashSet<string> { "MDP-2" });

        Assert.Equal(new[] { "MDP-2" }, result.Keys);
    }

    [Fact]
    public void Add_DoesNotMutateInput()
    {
        var alerts = new Dictionary<string, PrCommentAlert> { ["MDP-1"] = new("MDP-1", 1, "u1", Earlier) };

        PrCommentAlerts.Add(alerts, "MDP-1", 4, "u2", Later);
        PrCommentAlerts.Add(alerts, "MDP-9", 1, "u3", Later);

        Assert.Single(alerts);
        Assert.Equal(1, alerts["MDP-1"].NewCommentCount);
    }
}
