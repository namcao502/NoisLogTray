using Xunit;

namespace NoisLogTray.Tests;

public class TeamsNotifierTests
{
    private const string BaseUrl = "https://example.atlassian.net";

    [Fact]
    public void BuildNewTicketCard_IsAdaptiveCardWithJiraLink()
    {
        var ticket = new JiraTicket("MDP-1", "Summary", null, null);

        var card = TeamsNotifier.BuildNewTicketCard(ticket, BaseUrl);

        Assert.Equal("AdaptiveCard", card["type"]!.GetValue<string>());
        Assert.Contains($"{BaseUrl}/browse/MDP-1", card.ToJsonString());
    }

    [Fact]
    public void BuildPrMergedCard_LinksThePr()
    {
        var card = TeamsNotifier.BuildPrMergedCard("MDP-1", "feature/x", "https://dev.azure.com/pr/5");

        Assert.Equal("AdaptiveCard", card["type"]!.GetValue<string>());
        Assert.Contains("https://dev.azure.com/pr/5", card.ToJsonString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task PostNewTicketAsync_DoesNothing_WhenWebhookMissing(string? webhookUrl)
    {
        var notifier = new TeamsNotifier(webhookUrl, BaseUrl);

        await notifier.PostNewTicketAsync(new JiraTicket("MDP-1", "Summary", null, null));
    }

    [Fact]
    public async Task PostNewTicketAsync_DoesNotThrow_WhenWebhookUrlIsInvalid()
    {
        var notifier = new TeamsNotifier("not a url", BaseUrl);

        await notifier.PostNewTicketAsync(new JiraTicket("MDP-1", "Summary", null, null));
    }
}
