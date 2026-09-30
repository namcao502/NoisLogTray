using NoisLogTray;

namespace NoisLogTray.Tests;

public class JiraClientTests
{
    [Fact]
    public void ParseTicketsPageReturnsNextTokenUntilLastPage()
    {
        var list = new List<JiraSuggestion>();

        var next = JiraClient.ParseTicketsPage(
            """{"issues":[{"key":"MDP-1","fields":{"summary":"One","duedate":"2026-10-01"}}],"nextPageToken":"abc","isLast":false}""",
            list);
        Assert.Equal("abc", next);

        next = JiraClient.ParseTicketsPage(
            """{"issues":[{"key":"MDP-2","fields":{"summary":"Two","duedate":null}}],"isLast":true}""",
            list);
        Assert.Null(next);

        Assert.Equal(new[] { "MDP-1", "MDP-2" }, list.Select(t => t.Key));
        Assert.Equal("2026-10-01", list[0].DueDate);
        Assert.Null(list[1].DueDate);
    }
}
