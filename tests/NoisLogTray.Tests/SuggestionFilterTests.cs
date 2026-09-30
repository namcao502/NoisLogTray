using NoisLogTray;

namespace NoisLogTray.Tests;

public class SuggestionFilterTests
{
    private static readonly JiraSuggestion[] Tickets =
    {
        new("MDP-1234", "Fix invoice export", null),
        new("MDP-5678", "Add partner FTP cancel", "2026-10-01"),
    };

    [Fact]
    public void BlankQueryKeepsEveryTicket()
    {
        Assert.Equal(2, MainForm.FilterSuggestions(Tickets, "  ").Count);
    }

    [Fact]
    public void MatchesKeyDigitsOrSummaryIgnoringCase()
    {
        Assert.Equal("MDP-5678", Assert.Single(MainForm.FilterSuggestions(Tickets, "5678")).Key);
        Assert.Equal("MDP-1234", Assert.Single(MainForm.FilterSuggestions(Tickets, "INVOICE")).Key);
        Assert.Empty(MainForm.FilterSuggestions(Tickets, "nothing"));
    }
}
