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

    [Theory]
    [InlineData("MDP-1234", "MDP-1234")]
    [InlineData("  mdp-1234 ", "MDP-1234")]
    [InlineData("1234", "MDP-1234")]
    [InlineData("https://newoceaninfosys.atlassian.net/browse/MDP-1234?focusedCommentId=1", "MDP-1234")]
    public void TryParseKey_ReturnsUppercaseKey_WhenInputContainsKey(string input, string expected)
    {
        Assert.True(TicketParser.TryParseKey(input, out var ticketKey));
        Assert.Equal(expected, ticketKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("MDP-")]
    [InlineData("hello")]
    public void TryParseKey_ReturnsFalse_WhenInputHasNoKey(string input)
        => Assert.False(TicketParser.TryParseKey(input, out _));
}
