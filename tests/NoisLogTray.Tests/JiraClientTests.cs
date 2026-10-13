using NoisLogTray;

namespace NoisLogTray.Tests;

public class JiraClientTests
{
    [Fact]
    public void ParseSearchResponseReadsStatusAndTimeSpentWhenPresent()
    {
        var json = """
            {"issues":[{"key":"MDP-1","fields":{
                "summary":"Summary","duedate":"2026-09-30","customfield_10735":"2026-09-29",
                "customfield_11111":3,"status":{"name":"PR & Code Review"},"timespent":5400}}]}
            """;

        var ticket = JiraClient.ParseSearchResponse(json)["MDP-1"];

        Assert.Equal("Summary", ticket.Summary);
        Assert.Equal("2026-09-30", ticket.DueDate);
        Assert.Equal("2026-09-29", ticket.Eta);
        Assert.Equal(3, ticket.DevPriority);
        Assert.Equal("PR & Code Review", ticket.Status);
        Assert.Equal(5400, ticket.TimeSpentSeconds);
    }

    [Fact]
    public void ParseSearchResponseReturnsNullsWhenOptionalFieldsAreNull()
    {
        var json = """
            {"issues":[{"key":"MDP-1","fields":{
                "summary":"Summary","duedate":null,"customfield_10735":null,
                "customfield_11111":null,"status":null,"timespent":null}}]}
            """;

        var ticket = JiraClient.ParseSearchResponse(json)["MDP-1"];

        Assert.Null(ticket.DueDate);
        Assert.Null(ticket.Eta);
        Assert.Null(ticket.DevPriority);
        Assert.Null(ticket.Status);
        Assert.Null(ticket.StatusCategory);
        Assert.Null(ticket.TimeSpentSeconds);
    }

    [Fact]
    public void ParseIssueResponseReadsSingleIssue()
    {
        var json = """
            {"key":"MDP-2","fields":{"summary":"Single","duedate":null,"status":{"name":"Backlog"}}}
            """;

        var ticket = JiraClient.ParseIssueResponse(json);

        Assert.Equal("MDP-2", ticket.Key);
        Assert.Equal("Single", ticket.Summary);
        Assert.Equal("Backlog", ticket.Status);
    }

    [Fact]
    public void ParseSearchResponseSkipsIssueWithoutUsableKey()
    {
        var json = """
            {"issues":[{"key":"","fields":{"summary":"A"}},{"fields":{"summary":"B"}},{"key":"MDP-1","fields":{"summary":"C"}}]}
            """;

        var tickets = JiraClient.ParseSearchResponse(json);

        Assert.Equal(new[] { "MDP-1" }, tickets.Keys);
    }

    [Fact]
    public void ParseSearchResponseReturnsEmptyWhenIssuesMissingOrNotArray()
    {
        Assert.Empty(JiraClient.ParseSearchResponse("{}"));
        Assert.Empty(JiraClient.ParseSearchResponse("""{"issues":null}"""));
    }

    [Fact]
    public void ParseSearchResponseParsesIssueWithMissingFields()
    {
        var ticket = JiraClient.ParseSearchResponse("""{"issues":[{"key":"MDP-9"},{"key":"MDP-8","fields":null}]}""")["MDP-9"];

        Assert.Equal("", ticket.Summary);
        Assert.Null(ticket.DueDate);
        Assert.Null(ticket.Eta);
        Assert.Null(ticket.DevPriority);
        Assert.Null(ticket.Status);
        Assert.Null(ticket.StatusCategory);
        Assert.Null(ticket.TimeSpentSeconds);
    }

    [Fact]
    public void ParseIssueResponseIgnoresNonObjectStatusAndCategory()
    {
        var plain = JiraClient.ParseIssueResponse("""{"key":"MDP-4","fields":{"summary":"S","status":"Backlog"}}""");
        var noCategory = JiraClient.ParseIssueResponse("""{"key":"MDP-5","fields":{"summary":"S","status":{"name":"Backlog","statusCategory":"new"}}}""");

        Assert.Null(plain.Status);
        Assert.Null(plain.StatusCategory);
        Assert.Equal("Backlog", noCategory.Status);
        Assert.Null(noCategory.StatusCategory);
    }

    [Fact]
    public void ParseIssueResponseReadsStatusCategory()
    {
        var json = """
            {"key":"MDP-3","fields":{"summary":"Shipped","status":{"name":"Deployed to Production","statusCategory":{"key":"done"}}}}
            """;

        var ticket = JiraClient.ParseIssueResponse(json);

        Assert.Equal("done", ticket.StatusCategory);
        Assert.True(ticket.IsStatusDone);
    }

    [Fact]
    public void FindTransitionIdMatchesTargetStatusIgnoringCase()
    {
        var json = """
            {"transitions":[{"id":"9","to":{"name":"PR & Code Review"}},{"id":"10","to":{"name":"Deployed to Dev"}},{"id":"11","to":{"name":"Ready To Test"}}]}
            """;

        Assert.Equal("11", JiraClient.FindTransitionId(json, "Ready to Test"));
    }

    [Fact]
    public void FindTransitionIdReturnsNullWhenNoTransitionLeadsToTheStatus()
    {
        var json = """{"transitions":[{"id":"9","to":{"name":"PR & Code Review"}}]}""";

        Assert.Null(JiraClient.FindTransitionId(json, "Deployed to Dev"));
    }

    [Fact]
    public void FindTransitionIdReturnsNullWhenTransitionsPropertyIsMissing()
    {
        Assert.Null(JiraClient.FindTransitionId("""{"errorMessages":[]}""", "Ready to Test"));
    }
}
