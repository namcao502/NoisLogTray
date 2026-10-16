using System.Text;
using System.Text.Json.Nodes;

namespace NoisLogTray;

internal sealed class TeamsNotifier
{
    // A ticket check awaits Teams sequentially, so a dead webhook must fail fast.
    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private readonly string? _webhookUrl;
    private readonly string _jiraBaseUrl;

    internal TeamsNotifier(string? webhookUrl, string jiraBaseUrl)
    {
        _webhookUrl = webhookUrl;
        _jiraBaseUrl = jiraBaseUrl;
    }

    internal async Task PostNewTicketAsync(JiraTicket ticket)
    {
        await PostCardAsync(BuildNewTicketCard(ticket, _jiraBaseUrl), ticket.Key);
    }

    internal async Task PostPrMergedAsync(string ticketKey, string branch, string prUrl)
    {
        await PostCardAsync(BuildPrMergedCard(ticketKey, branch, prUrl), $"{ticketKey} ({branch}) merged");
    }

    internal async Task PostNewPrCommentAsync(string ticketKey, string branch, string prUrl, int newCommentCount)
    {
        await PostCardAsync(BuildNewPrCommentCard(ticketKey, branch, prUrl, newCommentCount), $"{ticketKey} ({branch}) new comment");
    }

    // Teams is a secondary channel: any failure is swallowed and logged so it never
    // breaks the primary ticket check. The URL carries a secret, so it is never logged.
    private async Task PostCardAsync(JsonObject card, string logContext)
    {
        if (string.IsNullOrEmpty(_webhookUrl))
        {
            return;
        }

        try
        {
            using var content = new StringContent(card.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await HttpClient.PostAsync(_webhookUrl, content);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            // The message can echo the webhook URL, whose signature is a secret: log only the type and status.
            var status = ex is HttpRequestException httpException && httpException.StatusCode.HasValue ? $" {(int)httpException.StatusCode.Value}" : "";
            AppLogger.Error($"Teams webhook ({logContext}): {ex.GetType().Name}{status}");
        }
    }

    internal static JsonObject BuildNewTicketCard(JiraTicket ticket, string jiraBaseUrl)
    {
        var dueDate = string.IsNullOrEmpty(ticket.DueDate) ? "-" : ticket.DueDate;
        var eta = string.IsNullOrEmpty(ticket.Eta) ? "-" : ticket.Eta;

        var body = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = $"Ticket mới: {ticket.Key}",
                ["weight"] = "Bolder",
                ["size"] = "Medium",
            },
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = ticket.Summary,
                ["wrap"] = true,
            },
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = $"Hạn: {dueDate} | ETA: {eta}",
                ["isSubtle"] = true,
                ["size"] = "Small",
            },
        };

        var actions = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "Action.OpenUrl",
                ["title"] = "Xem trên Jira",
                ["url"] = $"{jiraBaseUrl}/browse/{ticket.Key}",
            },
        };

        return WrapCard(body, actions);
    }

    internal static JsonObject BuildPrMergedCard(string ticketKey, string branch, string prUrl)
    {
        var body = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = $"PR đã merge: {ticketKey} ({branch})",
                ["weight"] = "Bolder",
                ["size"] = "Medium",
            },
        };

        return WrapCard(body, BuildViewPrAction(prUrl));
    }

    internal static JsonObject BuildNewPrCommentCard(string ticketKey, string branch, string prUrl, int newCommentCount)
    {
        var commentLabel = newCommentCount == 1 ? "1 comment mới" : $"{newCommentCount} comment mới";
        var body = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = $"{commentLabel} trên PR: {ticketKey} ({branch})",
                ["weight"] = "Bolder",
                ["size"] = "Medium",
            },
        };

        return WrapCard(body, BuildViewPrAction(prUrl));
    }

    private static JsonArray BuildViewPrAction(string prUrl)
    {
        return new JsonArray
        {
            new JsonObject
            {
                ["type"] = "Action.OpenUrl",
                ["title"] = "Xem PR",
                ["url"] = prUrl,
            },
        };
    }

    // The flow's "Post card in a chat or channel" action forwards the whole body into
    // AdaptiveCard.FromJson, so it must be a complete Adaptive Card, not {"text": ...}.
    private static JsonObject WrapCard(JsonArray body, JsonArray actions)
    {
        return new JsonObject
        {
            ["type"] = "AdaptiveCard",
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["version"] = "1.4",
            ["body"] = body,
            ["actions"] = actions,
        };
    }
}
