using NoisLogTray;

namespace NoisLogTray.Tests;

public class TicketParserTests
{
    [Fact]
    public void PrefixesShorthandDigits()
    {
        var (tickets, invalid) = TicketParser.Parse("1234");
        Assert.Equal(new[] { "MDP-1234" }, tickets);
        Assert.Empty(invalid);
    }

    [Fact]
    public void AcceptsFullKeysAnyCaseAndSplitsOnCommasAndSpaces()
    {
        var (tickets, invalid) = TicketParser.Parse("1234, mdp-5678 MDP-9999");
        Assert.Equal(new[] { "MDP-1234", "MDP-5678", "MDP-9999" }, tickets);
        Assert.Empty(invalid);
    }

    [Fact]
    public void DedupesPreservingOrder()
    {
        var (tickets, _) = TicketParser.Parse("1234, MDP-1234, 1234");
        Assert.Equal(new[] { "MDP-1234" }, tickets);
    }

    [Fact]
    public void CollectsInvalidTokens()
    {
        var (tickets, invalid) = TicketParser.Parse("1234, nope, ABC-1");
        Assert.Equal(new[] { "MDP-1234" }, tickets);
        Assert.Equal(new[] { "nope", "ABC-1" }, invalid);
    }

    [Fact]
    public void EmptyInputYieldsNothing()
    {
        var (tickets, invalid) = TicketParser.Parse("   ");
        Assert.Empty(tickets);
        Assert.Empty(invalid);
    }
}
