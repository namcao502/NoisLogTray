using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
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

    [Fact]
    public async Task PostAsUserAsync_PostsTheSubjectAndHtmlMessage_AndReturnsTrue_On2xx()
    {
        var prefix = FreeLocalhostPrefix();
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        var notifier = new TeamsNotifier(null, BaseUrl, prefix);
        var answer = AnswerOnceAsync(listener, 202);

        var posted = await notifier.PostAsUserAsync("MDP-1 Tiêu đề\r\n\r\nmô tả <x>", CancellationToken.None);

        Assert.True(posted);
        var payload = JsonNode.Parse(await answer)!.AsObject();
        Assert.Equal(2, payload.Count);
        Assert.Equal("MDP-1 Tiêu đề", payload["subject"]!.GetValue<string>());
        Assert.Equal("mô tả &lt;x&gt;", payload["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task PostAsUserAsync_ReturnsFalse_AndKeepsTheUrlOutOfTheLog_OnServerError()
    {
        var prefix = FreeLocalhostPrefix();
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        var signature = Guid.NewGuid().ToString("N");
        var notifier = new TeamsNotifier(null, BaseUrl, $"{prefix}?sig={signature}");
        var logPath = TodayLogPath();
        var logLengthBefore = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;
        var answer = AnswerOnceAsync(listener, 500);

        var posted = await notifier.PostAsUserAsync("MDP-1\r\nx", CancellationToken.None);
        await answer;

        Assert.False(posted);
        var newLog = ReadLogSince(logPath, logLengthBefore);
        Assert.Contains("Teams post as user", newLog);
        Assert.DoesNotContain(signature, newLog);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task PostAsUserAsync_ReturnsFalse_WhenNoUrl(string? postWorkflowUrl)
    {
        var notifier = new TeamsNotifier(null, BaseUrl, postWorkflowUrl);

        Assert.False(notifier.CanPostAsUser);
        Assert.False(await notifier.PostAsUserAsync("MDP-1\r\nx", CancellationToken.None));
    }

    [Fact]
    public async Task PostAsUserAsync_ReturnsFalse_AndLogsNothing_WhenCancelled()
    {
        var notifier = new TeamsNotifier(null, BaseUrl, FreeLocalhostPrefix());
        var logPath = TodayLogPath();
        var logLengthBefore = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;

        var posted = await notifier.PostAsUserAsync("MDP-1\r\nx", new CancellationToken(canceled: true));

        Assert.False(posted);
        Assert.DoesNotContain("Teams post as user", ReadLogSince(logPath, logLengthBefore));
    }

    private static string FreeLocalhostPrefix()
    {
        var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        var port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
        tcpListener.Stop();
        return $"http://localhost:{port}/";
    }

    // Answers one request with the status and returns the request body.
    private static async Task<string> AnswerOnceAsync(HttpListener listener, int statusCode)
    {
        var context = await listener.GetContextAsync();
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        context.Response.StatusCode = statusCode;
        context.Response.Close();
        return body;
    }

    private static string TodayLogPath()
    {
        var logDirectory = AppLogger.DirectoryOverride;
        Assert.NotNull(logDirectory);
        return Path.Combine(logDirectory, $"app-{DateTime.Now:yyyy-MM-dd}.log");
    }

    // Only what this test appended: other tests write to the same file.
    private static string ReadLogSince(string logPath, long logLengthBefore)
    {
        if (!File.Exists(logPath))
        {
            return "";
        }
        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(logLengthBefore, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
