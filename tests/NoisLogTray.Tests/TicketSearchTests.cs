namespace NoisLogTray.Tests;

public class TicketSearchTests
{
    private readonly JiraTicket _ticket = new("MDP-8961", "Change how Patch bins are configured in MDP", null, null);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Matches_ReturnsTrue_WhenQueryIsBlank(string query)
        => Assert.True(TicketSearch.Matches(_ticket, query));

    [Theory]
    [InlineData("8961")]
    [InlineData("mdp-8961")]
    [InlineData("patch BINS")]
    [InlineData("bins patch")]
    public void Matches_ReturnsTrue_WhenEveryWordIsInKeyOrSummary(string query)
        => Assert.True(TicketSearch.Matches(_ticket, query));

    [Theory]
    [InlineData("8962")]
    [InlineData("patch order")]
    public void Matches_ReturnsFalse_WhenAnyWordIsMissing(string query)
        => Assert.False(TicketSearch.Matches(_ticket, query));
}
