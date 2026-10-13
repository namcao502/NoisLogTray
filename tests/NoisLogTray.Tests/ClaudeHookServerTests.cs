using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace NoisLogTray.Tests;

public class ClaudeHookServerTests
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void BuildSettingsJson_PostsEveryWatchedEvent_ToTheSessionUrl()
    {
        const string url = "http://localhost:1234/token/MDP-1";

        var settings = JsonNode.Parse(ClaudeHookServer.BuildSettingsJson(url))!.AsObject();

        var hooks = settings["hooks"]!.AsObject();
        Assert.Equal(
            new[] { "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SessionEnd" },
            hooks.Select(kvp => kvp.Key).ToArray());
        foreach (var eventEntry in hooks)
        {
            var matchers = eventEntry.Value!.AsArray();
            Assert.Single(matchers);
            var matcher = matchers[0]!.AsObject();
            Assert.False(matcher.ContainsKey("matcher"));
            var hook = Assert.Single(matcher["hooks"]!.AsArray())!.AsObject();
            Assert.Equal("http", (string?)hook["type"]);
            Assert.Equal(url, (string?)hook["url"]);
            Assert.Equal(5, (int?)hook["timeout"]);
        }
    }

    [Fact]
    public void HookUrl_EndsWithTheSessionName()
    {
        using var server = ClaudeHookServer.TryStart();

        Assert.NotNull(server);
        Assert.EndsWith("/MDP-1", server.HookUrl("MDP-1"));
    }

    [Fact]
    public async Task Post_RaisesHookReceived_AndRepliesEmptyJson_WhenTheUrlIsTheSessions()
    {
        using var server = ClaudeHookServer.TryStart();
        Assert.NotNull(server);
        var received = new TaskCompletionSource<(string SessionName, ClaudeHookEvent HookEvent)>();
        server.HookReceived += (sessionName, hookEvent) => received.TrySetResult((sessionName, hookEvent));
        using var client = NewClient();

        var response = await client.PostAsync(server.HookUrl("MDP-1"), Json("""{"hook_event_name":"Stop"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{}", await response.Content.ReadAsStringAsync());
        var hookReceived = await received.Task.WaitAsync(ReceiveTimeout);
        Assert.Equal("MDP-1", hookReceived.SessionName);
        Assert.Equal(new ClaudeHookEvent("Stop", null, null, null), hookReceived.HookEvent);
    }

    [Fact]
    public async Task Post_Returns404_AndRaisesNothing_WhenTheTokenIsWrong()
    {
        using var server = ClaudeHookServer.TryStart();
        Assert.NotNull(server);
        var raised = false;
        server.HookReceived += (_, _) => raised = true;
        var sessionUri = new Uri(server.HookUrl("MDP-1"));
        var wrongTokenUrl = $"{sessionUri.GetLeftPart(UriPartial.Authority)}/{Guid.NewGuid():N}/MDP-1";
        using var client = NewClient();

        var response = await client.PostAsync(wrongTokenUrl, Json("""{"hook_event_name":"Stop"}"""));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(raised);
    }

    [Fact]
    public async Task Post_RepliesEmptyJson_AndRaisesNothing_WhenTheBodyIsNotAHook()
    {
        using var server = ClaudeHookServer.TryStart();
        Assert.NotNull(server);
        var raised = false;
        server.HookReceived += (_, _) => raised = true;
        using var client = NewClient();

        var response = await client.PostAsync(server.HookUrl("MDP-1"), Json("not json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{}", await response.Content.ReadAsStringAsync());
        Assert.False(raised);
    }

    [Fact]
    public async Task Post_KeepsAnswering_WhenAHookReceivedSubscriberThrows()
    {
        using var server = ClaudeHookServer.TryStart();
        Assert.NotNull(server);
        var calls = 0;
        server.HookReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("subscriber failed");
        };
        using var client = NewClient();

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.PostAsync(server.HookUrl("MDP-1"), Json("""{"hook_event_name":"Stop"}""")));
        var response = await client.PostAsync(server.HookUrl("MDP-1"), Json("""{"hook_event_name":"Stop"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, calls);
    }

    // Bounded, so a dead listener fails the test quickly instead of after HttpClient's 100s default.
    private static HttpClient NewClient() => new() { Timeout = ReceiveTimeout };

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}
