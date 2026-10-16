using System.Net;
using System.Text;
using System.Text.Json;

namespace NoisLogTray.Tests;

public class TeamsChatClientTests
{
    private const string ChatBase = "https://teams.test/api/chatsvc/apac/v1/users/ME/";
    private const string AggregatorBase = "https://teams.test/api/csa/apac/";

    private static string Jwt(object claims)
    {
        var header = Base64Url("{\"alg\":\"none\"}");
        var payload = Base64Url(JsonSerializer.Serialize(claims));
        return $"{header}.{payload}.sig";
    }

    private static string Base64Url(string text)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static TeamsSession Session(DateTimeOffset? expiresAt = null)
    {
        return new TeamsSession("chat-token", "aggregator-token", ChatBase, AggregatorBase, "8:orgid:abc", "Test User",
            expiresAt ?? DateTimeOffset.UtcNow.AddHours(1), "Mozilla/5.0 Chrome/130", "1415/26091712222");
    }

    private static TeamsMessageBody Body(string html)
    {
        return new TeamsMessageBody(html, null, Array.Empty<string>());
    }

    private static TeamsSniffResult Ok(TeamsSession session)
    {
        return new TeamsSniffResult(session, false, null);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
    {
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed record RecordedRequest(HttpMethod Method, string Url, string? Authorization, string Body,
        Dictionary<string, string> Headers, string? ContentType);

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] _bytes;

        internal UnknownLengthContent(byte[] bytes, string contentType)
        {
            _bytes = bytes;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return stream.WriteAsync(_bytes, 0, _bytes.Length);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private const string ImageUrl = "https://as-api.asm.skype.com/v1/objects/0-jhb-d4-abc/views/imgo";

    private static HttpResponseMessage ImageResponse(byte[] bytes, string contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return response;
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _respond;

        internal FakeHandler(Func<int, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        internal List<RecordedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(" ", h.Value), StringComparer.OrdinalIgnoreCase);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.OriginalString, request.Headers.Authorization?.ToString(), body,
                headers, request.Content?.Headers.ContentType?.MediaType));
            return _respond(Requests.Count);
        }
    }

    private sealed class SniffCounter
    {
        internal int Calls { get; private set; }

        internal Task<TeamsSniffResult> Sniff(TeamsSniffResult result)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    [Fact]
    public void SessionFrom_ReturnsSession_WithMyIdAndEarliestExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        var chatExpiry = now.AddHours(1).ToUnixTimeSeconds();
        var aggregatorExpiry = now.AddHours(2).ToUnixTimeSeconds();
        var chatToken = Jwt(new { oid = "abc-123", name = "Nam Nguyen", exp = chatExpiry });
        var aggregatorToken = Jwt(new { exp = aggregatorExpiry });

        var session = TeamsWebSniffer.SessionFrom(chatToken, ChatBase + "conversations/x", aggregatorToken, AggregatorBase + "api/v1/x", now);

        Assert.NotNull(session);
        Assert.Equal("8:orgid:abc-123", session.MyId);
        Assert.Equal("Nam Nguyen", session.MyName);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(chatExpiry), session.ExpiresAt);
    }

    [Fact]
    public void SessionFrom_ReturnsNull_WhenAnyPartIsMissing()
    {
        var now = DateTimeOffset.UtcNow;
        var chatToken = Jwt(new { oid = "abc" });
        var tokenWithoutOid = Jwt(new { name = "x" });
        var chatUrl = ChatBase + "conversations/x";
        var aggregatorUrl = AggregatorBase + "api/v1/x";

        Assert.Null(TeamsWebSniffer.SessionFrom(null, chatUrl, "agg", aggregatorUrl, now));
        Assert.Null(TeamsWebSniffer.SessionFrom(chatToken, null, "agg", aggregatorUrl, now));
        Assert.Null(TeamsWebSniffer.SessionFrom(chatToken, chatUrl, null, aggregatorUrl, now));
        Assert.Null(TeamsWebSniffer.SessionFrom(chatToken, chatUrl, "agg", null, now));
        Assert.Null(TeamsWebSniffer.SessionFrom(chatToken, "https://teams.test/other", "agg", aggregatorUrl, now));
        Assert.Null(TeamsWebSniffer.SessionFrom(chatToken, chatUrl, "agg", "https://teams.test/other", now));
        Assert.Null(TeamsWebSniffer.SessionFrom(tokenWithoutOid, chatUrl, "agg", aggregatorUrl, now));
    }

    [Fact]
    public void SessionFrom_ExpiresIn30Minutes_WhenTokenHasNoExp()
    {
        var now = DateTimeOffset.UtcNow;
        var chatToken = Jwt(new { oid = "abc" });

        var session = TeamsWebSniffer.SessionFrom(chatToken, ChatBase + "conversations/x", "opaque-token", AggregatorBase + "api/v1/x", now);

        Assert.NotNull(session);
        Assert.Equal(now.AddMinutes(30), session.ExpiresAt);
        Assert.Equal("", session.MyName);
    }

    [Fact]
    public void SessionFrom_CapturesBaseUrls_WithTrailingSlash()
    {
        var chatToken = Jwt(new { oid = "abc" });

        var session = TeamsWebSniffer.SessionFrom(
            chatToken, "https://teams.cloud.microsoft/api/chatsvc/apac/v1/users/ME/conversations/48%3Anotifications",
            "agg", "https://teams.cloud.microsoft/api/csa/apac/api/v1/teams/users/me/pinnedChannels", DateTimeOffset.UtcNow);

        Assert.NotNull(session);
        Assert.Equal("https://teams.cloud.microsoft/api/chatsvc/apac/v1/users/ME/", session.ChatBaseUrl);
        Assert.Equal("https://teams.cloud.microsoft/api/csa/apac/", session.AggregatorBaseUrl);
    }

    [Fact]
    public void SessionFrom_LowercasesMyId()
    {
        var chatToken = Jwt(new { oid = "5765AA42-84C5-43D9-9BF3-08CEE75DE0A4" });

        var session = TeamsWebSniffer.SessionFrom(chatToken, ChatBase + "x", "agg", AggregatorBase + "x", DateTimeOffset.UtcNow);

        Assert.NotNull(session);
        Assert.Equal("8:orgid:5765aa42-84c5-43d9-9bf3-08cee75de0a4", session.MyId);
    }

    [Fact]
    public async Task SniffAsync_ReturnsBusy_WhileProfileIsHeld()
    {
        Assert.True(TeamsWebSniffer.TryAcquireProfile());
        try
        {
            var result = await TeamsWebSniffer.SniffAsync(CancellationToken.None);

            Assert.Null(result.Session);
            Assert.False(result.SignedOut);
            Assert.Equal("Teams sign-in or read already running", result.Error);
        }
        finally
        {
            TeamsWebSniffer.ReleaseProfile();
        }
    }

    [Fact]
    public async Task SignInAsync_ReturnsBusy_WhileProfileIsHeld()
    {
        Assert.True(TeamsWebSniffer.TryAcquireProfile());
        try
        {
            var result = await TeamsWebSniffer.SignInAsync();

            Assert.False(result.Success);
            Assert.Equal("Teams sign-in or read already running", result.Error);
        }
        finally
        {
            TeamsWebSniffer.ReleaseProfile();
        }
    }

    [Fact]
    public async Task ListChannelsAsync_SendsAggregatorToken()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, "{\"teams\":[]}"));
        var client = new TeamsChatClient(_ => Task.FromResult(Ok(Session())), handler);

        var channels = await client.ListChannelsAsync(CancellationToken.None);

        Assert.Empty(channels);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(AggregatorBase + "api/v3/teams/users/me?isPrefetch=false&enableMembershipSummary=true", request.Url);
        Assert.Equal("Bearer aggregator-token", request.Authorization);
    }

    [Fact]
    public async Task GetThreadAsync_EscapesChannelAndMessageId_AndSendsChatToken()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, "{\"messages\":[]}"));
        var client = new TeamsChatClient(_ => Task.FromResult(Ok(Session())), handler);

        var thread = await client.GetThreadAsync("19:abc@thread.tacv2", "1792048362201", CancellationToken.None);

        Assert.Equal("19:abc@thread.tacv2", thread.ChannelId);
        Assert.Equal("1792048362201", thread.RootId);
        var request = handler.Requests[0];
        Assert.Equal(
            ChatBase + "conversations/19%3Aabc%40thread.tacv2%3Bmessageid%3D1792048362201/messages?view=msnp24Equivalent&pageSize=200",
            request.Url);
        Assert.Equal("Bearer chat-token", request.Authorization);
    }

    [Fact]
    public async Task GetThreadAsync_AddsRepliesFromTheChannelPage_WhenTheThreadReadGivesOnlyTheRoot()
    {
        var rootOnly = """
            {"messages":[{"id":"100","rootMessageId":"100","messagetype":"Text","content":"post","from":"x/8:orgid:me","composetime":"2026-10-16T04:00:00Z"}]}
            """;
        var channelPage = """
            {"messages":[
              {"id":"103","rootMessageId":"999","messagetype":"Text","content":"other post","from":"x/8:orgid:b","composetime":"2026-10-16T06:00:00Z"},
              {"id":"102","rootMessageId":"100","messagetype":"Text","content":"second","from":"x/8:orgid:b","composetime":"2026-10-16T05:30:00Z"},
              {"id":"101","rootMessageId":"100","messagetype":"Text","content":"first","from":"x/8:orgid:b","composetime":"2026-10-16T05:00:00Z"},
              {"id":"100","rootMessageId":"100","messagetype":"Text","content":"post","from":"x/8:orgid:me","composetime":"2026-10-16T04:00:00Z"}
            ]}
            """;
        var handler = new FakeHandler(call => Json(HttpStatusCode.OK, call == 1 ? rootOnly : channelPage));
        var client = new TeamsChatClient(_ => Task.FromResult(Ok(Session())), handler);

        var thread = await client.GetThreadAsync("19:abc@thread.tacv2", "100", CancellationToken.None);

        Assert.Equal(new[] { "100", "101", "102" }, thread.Messages.Select(tm => tm.Id).ToArray());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(ChatBase + "conversations/19%3Aabc%40thread.tacv2/messages?view=msnp24Equivalent&pageSize=200", handler.Requests[1].Url);
    }

    [Fact]
    public async Task GetThreadAsync_ReadsOnlyTheThread_WhenItHasReplies()
    {
        var withReply = """
            {"messages":[
              {"id":"100","rootMessageId":"100","messagetype":"Text","content":"post","from":"x/8:orgid:me","composetime":"2026-10-16T04:00:00Z"},
              {"id":"101","rootMessageId":"100","messagetype":"Text","content":"reply","from":"x/8:orgid:b","composetime":"2026-10-16T05:00:00Z"}
            ]}
            """;
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, withReply));
        var client = new TeamsChatClient(_ => Task.FromResult(Ok(Session())), handler);

        var thread = await client.GetThreadAsync("19:abc@thread.tacv2", "100", CancellationToken.None);

        Assert.Equal(2, thread.Messages.Count);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PostReplyAsync_SendsSpikeBody_AndReturnsArrivalTimeAsId()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.Created, "{\"OriginalArrivalTime\": 1792048362201}"));
        var client = new TeamsChatClient(_ => Task.FromResult(Ok(Session())), handler);

        var replyId = await client.PostReplyAsync("19:abc@thread.tacv2", "100", Body("<p>hi</p>"), CancellationToken.None);

        Assert.Equal("1792048362201", replyId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(ChatBase + "conversations/19%3Aabc%40thread.tacv2%3Bmessageid%3D100/messages", request.Url);
        Assert.Equal("Bearer chat-token", request.Authorization);

        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal("<p>hi</p>", root.GetProperty("content").GetString());
        Assert.Equal("RichText/Html", root.GetProperty("messagetype").GetString());
        Assert.Equal("text", root.GetProperty("contenttype").GetString());
        Assert.Equal("Test User", root.GetProperty("imdisplayname").GetString());
        Assert.Equal(0, root.GetProperty("amsreferences").GetArrayLength());
        Assert.Matches(@"^\d{19}$", root.GetProperty("clientmessageid").GetString());
        Assert.Equal("", root.GetProperty("properties").GetProperty("importance").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("properties").GetProperty("subject").ValueKind);
    }

    [Fact]
    public async Task PostMessageAsync_SendsSubject_ToChannelConversation_AndReturnsArrivalTimeAsId()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.Created, "{\"OriginalArrivalTime\": 1792051394963}"));
        var client = new TeamsChatClient(_ => Task.FromResult(Ok(Session())), handler);

        var postId = await client.PostMessageAsync("19:abc@thread.tacv2", "MDP-1 subject", Body("<p>hi</p>"), CancellationToken.None);

        Assert.Equal("1792051394963", postId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(ChatBase + "conversations/19%3Aabc%40thread.tacv2/messages", request.Url);
        Assert.Equal("Bearer chat-token", request.Authorization);

        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("<p>hi</p>", body.RootElement.GetProperty("content").GetString());
        Assert.Equal("MDP-1 subject", body.RootElement.GetProperty("properties").GetProperty("subject").GetString());
        Assert.Equal("TEAMS", body.RootElement.GetProperty("properties").GetProperty("formatVariant").GetString());
    }

    [Fact]
    public async Task PostMessageAsync_ThrowsUncertain_On5xx_WithoutRetry()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.BadGateway, "{}"));
        var client = new TeamsChatClient(_ => Task.FromResult(Ok(Session())), handler);

        await Assert.ThrowsAsync<TeamsReplyUncertainException>(() => client.PostMessageAsync("c", "s", Body("x"), CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PostReplyAsync_RetriesOnceOn401()
    {
        var handler = new FakeHandler(call => call == 1
            ? Json(HttpStatusCode.Unauthorized, "{}")
            : Json(HttpStatusCode.Created, "{\"OriginalArrivalTime\": 5}"));
        var counter = new SniffCounter();
        var client = new TeamsChatClient(_ => counter.Sniff(Ok(Session())), handler);

        var replyId = await client.PostReplyAsync("c", "r", Body("<p>x</p>"), CancellationToken.None);

        Assert.Equal("5", replyId);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, counter.Calls);
    }

    [Fact]
    public async Task PostReplyAsync_ThrowsUncertain_On5xxOrTimeout_WithoutRetry()
    {
        var serverError = new FakeHandler(_ => Json(HttpStatusCode.BadGateway, "{}"));
        await Assert.ThrowsAsync<TeamsReplyUncertainException>(() =>
            new TeamsChatClient(_ => Task.FromResult(Ok(Session())), serverError).PostReplyAsync("c", "r", Body("x"), CancellationToken.None));
        Assert.Single(serverError.Requests);

        var timeout = new FakeHandler(_ => throw new TaskCanceledException("timeout", new TimeoutException()));
        await Assert.ThrowsAsync<TeamsReplyUncertainException>(() =>
            new TeamsChatClient(_ => Task.FromResult(Ok(Session())), timeout).PostReplyAsync("c", "r", Body("x"), CancellationToken.None));
        Assert.Single(timeout.Requests);

        var connectionLost = new FakeHandler(_ => throw new HttpRequestException("connection reset"));
        await Assert.ThrowsAsync<TeamsReplyUncertainException>(() =>
            new TeamsChatClient(_ => Task.FromResult(Ok(Session())), connectionLost).PostReplyAsync("c", "r", Body("x"), CancellationToken.None));
        Assert.Single(connectionLost.Requests);
    }

    [Fact]
    public async Task PostReplyAsync_ThrowsUncertain_WhenBodyHasNoArrivalTime()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.Created, "{}"));
        var client = new TeamsChatClient(_ => Task.FromResult(Ok(Session())), handler);

        await Assert.ThrowsAsync<TeamsReplyUncertainException>(() => client.PostReplyAsync("c", "r", Body("x"), CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetChannelPageAsync_FollowsBackwardLink_OnlyOnChatHost()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, "{\"messages\":[]}"));
        var client = new TeamsChatClient(_ => Task.FromResult(Ok(Session())), handler);
        var link = ChatBase + "conversations/19%3Aabc/messages?startTime=1&pageSize=200";

        await client.GetChannelPageAsync("19:abc", link, CancellationToken.None);

        Assert.Equal(link, Assert.Single(handler.Requests).Url);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetChannelPageAsync("19:abc", "https://evil.test/steal", CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Client_ReusesSession_UntilNearExpiry()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, "{\"teams\":[]}"));
        var counter = new SniffCounter();
        var client = new TeamsChatClient(_ => counter.Sniff(Ok(Session())), handler);

        await client.ListChannelsAsync(CancellationToken.None);
        await client.ListChannelsAsync(CancellationToken.None);

        Assert.Equal(1, counter.Calls);

        var nearExpiry = new SniffCounter();
        var expiringClient = new TeamsChatClient(_ => nearExpiry.Sniff(Ok(Session(DateTimeOffset.UtcNow.AddMinutes(1)))), handler);

        await expiringClient.ListChannelsAsync(CancellationToken.None);
        await expiringClient.ListChannelsAsync(CancellationToken.None);

        Assert.Equal(2, nearExpiry.Calls);
    }

    [Fact]
    public async Task Client_ResniffsOnce_On401()
    {
        var handler = new FakeHandler(call => call == 1 ? Json(HttpStatusCode.Unauthorized, "{}") : Json(HttpStatusCode.OK, "{\"teams\":[]}"));
        var counter = new SniffCounter();
        var client = new TeamsChatClient(_ => counter.Sniff(Ok(Session())), handler);

        await client.ListChannelsAsync(CancellationToken.None);

        Assert.Equal(2, counter.Calls);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Client_ThrowsSignedOut_WhenSecond401()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.Unauthorized, "{}"));
        var counter = new SniffCounter();
        var client = new TeamsChatClient(_ => counter.Sniff(Ok(Session())), handler);

        await Assert.ThrowsAsync<TeamsSignedOutException>(() => client.ListChannelsAsync(CancellationToken.None));

        Assert.Equal(2, counter.Calls);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Client_ThrowsSignedOut_WhenSniffSaysSignedOut()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var client = new TeamsChatClient(_ => Task.FromResult(new TeamsSniffResult(null, true, null)), handler);

        await Assert.ThrowsAsync<TeamsSignedOutException>(() => client.GetMyIdAsync(CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void SessionFrom_KeepsUserAgent_ReplacingHeadlessChrome()
    {
        var chatToken = Jwt(new { oid = "abc" });

        var session = TeamsWebSniffer.SessionFrom(chatToken, ChatBase + "x", "agg", AggregatorBase + "x", DateTimeOffset.UtcNow,
            "Mozilla/5.0 HeadlessChrome/130.0.0.0 Safari/537.36", "1415/26091712222");

        Assert.NotNull(session);
        Assert.Equal("Mozilla/5.0 Chrome/130.0.0.0 Safari/537.36", session.UserAgent);
        Assert.Equal("1415/26091712222", session.ClientVersion);
    }

    [Fact]
    public void SessionFrom_UsesDefaultUserAgent_WhenMissing()
    {
        var chatToken = Jwt(new { oid = "abc" });

        var session = TeamsWebSniffer.SessionFrom(chatToken, ChatBase + "x", "agg", AggregatorBase + "x", DateTimeOffset.UtcNow);

        Assert.NotNull(session);
        Assert.Equal(TeamsWebSniffer.DefaultUserAgent, session.UserAgent);
        Assert.Null(session.ClientVersion);
    }

    [Fact]
    public async Task PostReplyAsync_SendsMentionsAsStringAndAmsReferences()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.Created, "{\"OriginalArrivalTime\": 7}"));
        var client = new TeamsChatClient(_ => Task.FromResult(Ok(Session())), handler);
        var mentionsJson = "[{\"itemid\":\"0\"}]";

        await client.PostReplyAsync("c", "r", new TeamsMessageBody("<p>x</p>", mentionsJson, new[] { "0-jhb-d4-abc" }), CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var mentions = body.RootElement.GetProperty("properties").GetProperty("mentions");
        Assert.Equal(JsonValueKind.String, mentions.ValueKind);
        Assert.Equal(mentionsJson, mentions.GetString());
        Assert.Equal("0-jhb-d4-abc", Assert.Single(body.RootElement.GetProperty("amsreferences").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task UploadImageAsync_CreatesObjectThenPutsBytes_WithTeamsHeaders()
    {
        var handler = new FakeHandler(call => call == 1
            ? Json(HttpStatusCode.Created, "{\"id\":\"0-jhb-d4-abc\"}")
            : new HttpResponseMessage(HttpStatusCode.Created));
        var client = new TeamsChatClient(_ => Task.FromResult(Ok(Session())), handler);
        var image = new TeamsImage(new byte[] { 1, 2, 3 }, "x.png", 10, 20);

        var objectId = await client.UploadImageAsync("19:abc@thread.tacv2", image, CancellationToken.None);

        Assert.Equal("0-jhb-d4-abc", objectId);
        Assert.Equal(2, handler.Requests.Count);
        var create = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, create.Method);
        Assert.Equal("https://as-api.asm.skype.com/v1/objects", create.Url);
        using var createBody = JsonDocument.Parse(create.Body);
        Assert.Equal("pish/image", createBody.RootElement.GetProperty("type").GetString());
        Assert.Equal("x.png", createBody.RootElement.GetProperty("filename").GetString());
        Assert.Equal("read", createBody.RootElement.GetProperty("permissions").GetProperty("19:abc@thread.tacv2")[0].GetString());

        var put = handler.Requests[1];
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.Equal("https://as-api.asm.skype.com/v1/objects/0-jhb-d4-abc/content/imgpsh", put.Url);
        Assert.Equal("application/octet-stream", put.ContentType);

        foreach (var request in handler.Requests)
        {
            Assert.Equal("Bearer chat-token", request.Authorization);
            Assert.Equal("Mozilla/5.0 Chrome/130", request.Headers["User-Agent"]);
            Assert.Equal("web", request.Headers["x-ms-client-type"]);
            Assert.Equal("1415/26091712222", request.Headers["x-ms-client-version"]);
            Assert.Equal("https://teams.cloud.microsoft", request.Headers["Origin"]);
            Assert.Equal("https://teams.cloud.microsoft/", request.Headers["Referer"]);
        }
    }

    [Fact]
    public async Task UploadImageAsync_RetriesOnceOn401()
    {
        var handler = new FakeHandler(call => call == 1
            ? Json(HttpStatusCode.Unauthorized, "{}")
            : call == 2 ? Json(HttpStatusCode.Created, "{\"id\":\"obj\"}") : new HttpResponseMessage(HttpStatusCode.Created));
        var counter = new SniffCounter();
        var client = new TeamsChatClient(_ => counter.Sniff(Ok(Session())), handler);

        var objectId = await client.UploadImageAsync("c", new TeamsImage(new byte[] { 1 }, "x.png", 1, 1), CancellationToken.None);

        Assert.Equal("obj", objectId);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(2, counter.Calls);
    }

    private const string PictureBase = "https://teams.cloud.microsoft/api/mt/apac/beta/users/abc/profilepicturev2/";
    private const string PersonMri = "8:orgid:11111111-2222-3333-4444-555555555555";

    private static TeamsSession ProfileSession(DateTimeOffset? profileExpiresAt = null, string? profileToken = "spaces-token")
    {
        return Session() with { ProfileToken = profileToken, ProfilePictureBaseUrl = PictureBase, ProfileTokenExpiresAt = profileExpiresAt };
    }

    [Fact]
    public void SessionFrom_KeepsProfileTokenUrlAndExpiry_FromSpacesToken()
    {
        var now = DateTimeOffset.UtcNow;
        var profileExpiry = now.AddHours(3).ToUnixTimeSeconds();
        var chatToken = Jwt(new { oid = "abc-123", exp = now.AddHours(1).ToUnixTimeSeconds() });
        var spacesToken = Jwt(new { aud = "https://api.spaces.skype.com", exp = profileExpiry });

        var session = TeamsWebSniffer.SessionFrom(chatToken, ChatBase + "x", "agg", AggregatorBase + "x", now, profileToken: spacesToken);

        Assert.NotNull(session);
        Assert.Equal(spacesToken, session.ProfileToken);
        Assert.Equal("https://teams.cloud.microsoft/api/mt/apac/beta/users/abc-123/profilepicturev2/", session.ProfilePictureBaseUrl);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(profileExpiry), session.ProfileTokenExpiresAt);
        Assert.Equal(now.AddMinutes(30), session.ExpiresAt);
    }

    [Fact]
    public void SessionFrom_LeavesProfileNull_WhenNoTokenOrUnknownRegion()
    {
        var now = DateTimeOffset.UtcNow;
        var chatToken = Jwt(new { oid = "abc-123", exp = now.AddHours(1).ToUnixTimeSeconds() });

        var withoutToken = TeamsWebSniffer.SessionFrom(chatToken, ChatBase + "x", "agg", AggregatorBase + "x", now);
        var unknownRegion = TeamsWebSniffer.SessionFrom(chatToken, "https://teams.test/api/chatsvc/UPPER/v1/users/ME/x", "agg",
            AggregatorBase + "x", now, profileToken: "spaces");

        Assert.NotNull(withoutToken);
        Assert.Null(withoutToken.ProfileToken);
        Assert.Null(withoutToken.ProfilePictureBaseUrl);
        Assert.Null(withoutToken.ProfileTokenExpiresAt);
        Assert.NotNull(unknownRegion);
        Assert.Null(unknownRegion.ProfileToken);
        Assert.Null(unknownRegion.ProfilePictureBaseUrl);
    }

    [Fact]
    public async Task GetProfilePictureAsync_RequestsPictureUrlWithSpacesToken()
    {
        var handler = new FakeHandler(_ => ImageResponse(new byte[] { 1, 2 }, "image/jpeg"));
        var client = new TeamsChatClient(_ => Task.FromResult(Ok(ProfileSession())), handler);

        var picture = await client.GetProfilePictureAsync(PersonMri, "Nam Nguyen", CancellationToken.None);

        Assert.Equal(new byte[] { 1, 2 }, picture!.Value.Bytes);
        Assert.Equal("image/jpeg", picture.Value.ContentType);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"{PictureBase}{PersonMri}?displayname=Nam%20Nguyen&size=HR64x64", request.Url);
        Assert.Equal("Bearer spaces-token", request.Authorization);
    }

    [Fact]
    public async Task GetProfilePictureAsync_ReturnsNull_WithoutCallingNetwork_WhenNoOrExpiredProfileToken()
    {
        var handler = new FakeHandler(_ => ImageResponse(new byte[] { 1 }, "image/jpeg"));
        var noToken = new TeamsChatClient(_ => Task.FromResult(Ok(Session())), handler);
        var expired = new TeamsChatClient(_ => Task.FromResult(Ok(ProfileSession(DateTimeOffset.UtcNow.AddMinutes(-1)))), handler);

        Assert.Null(await noToken.GetProfilePictureAsync(PersonMri, "Name", CancellationToken.None));
        Assert.Null(await expired.GetProfilePictureAsync(PersonMri, "Name", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetProfilePictureAsync_RejectsNonPersonMri()
    {
        var handler = new FakeHandler(_ => ImageResponse(new byte[] { 1 }, "image/jpeg"));
        var client = new TeamsChatClient(_ => Task.FromResult(Ok(ProfileSession())), handler);

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetProfilePictureAsync("19:abc@thread.tacv2", "Name", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetProfilePictureAsync_ReturnsNull_On401_WithoutInvalidatingOrSniffing()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var counter = new SniffCounter();
        var client = new TeamsChatClient(_ => counter.Sniff(Ok(ProfileSession())), handler);

        Assert.Null(await client.GetProfilePictureAsync(PersonMri, "Name", CancellationToken.None));
        Assert.Null(await client.GetProfilePictureAsync(PersonMri, "Name", CancellationToken.None));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public async Task GetProfilePictureAsync_RejectsNonImageOrOver1Mb()
    {
        var oversize = new byte[TeamsChatClient.MaxAvatarBytes + 1];
        var knownLength = new TeamsChatClient(_ => Task.FromResult(Ok(ProfileSession())), new FakeHandler(_ => ImageResponse(oversize, "image/png")));
        var unknownLength = new TeamsChatClient(_ => Task.FromResult(Ok(ProfileSession())), new FakeHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(oversize, "image/png") }));
        var notImage = new TeamsChatClient(_ => Task.FromResult(Ok(ProfileSession())), new FakeHandler(_ => ImageResponse(new byte[] { 1 }, "text/html")));
        var svg = new TeamsChatClient(_ => Task.FromResult(Ok(ProfileSession())), new FakeHandler(_ => ImageResponse(new byte[] { 1 }, "image/svg+xml")));

        Assert.Null(await knownLength.GetProfilePictureAsync(PersonMri, "Name", CancellationToken.None));
        Assert.Null(await unknownLength.GetProfilePictureAsync(PersonMri, "Name", CancellationToken.None));
        Assert.Null(await notImage.GetProfilePictureAsync(PersonMri, "Name", CancellationToken.None));
        Assert.Null(await svg.GetProfilePictureAsync(PersonMri, "Name", CancellationToken.None));
    }

    [Fact]
    public async Task GetImageAsync_RefusesNonAmsUrl_WithoutRequest()
    {
        var handler = new FakeHandler(_ => ImageResponse(new byte[] { 1 }, "image/png"));
        var client = new TeamsChatClient(_ => Task.FromResult(Ok(Session())), handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetImageAsync("https://evil.test/v1/objects/a/views/imgo", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetImageAsync_ReturnsBytesAndContentType()
    {
        var handler = new FakeHandler(_ => ImageResponse(new byte[] { 1, 2 }, "image/jpeg"));
        var client = new TeamsChatClient(_ => Task.FromResult(Ok(Session())), handler);

        var (bytes, contentType) = await client.GetImageAsync(ImageUrl, CancellationToken.None);

        Assert.Equal(new byte[] { 1, 2 }, bytes);
        Assert.Equal("image/jpeg", contentType);
        Assert.Equal("Bearer chat-token", Assert.Single(handler.Requests).Authorization);
    }

    [Fact]
    public async Task GetImageAsync_RefusesOversizeOrNonImage()
    {
        var oversize = new byte[TeamsCompose.MaxImageBytes + 1];
        var knownLength = new TeamsChatClient(_ => Task.FromResult(Ok(Session())), new FakeHandler(_ => ImageResponse(oversize, "image/png")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => knownLength.GetImageAsync(ImageUrl, CancellationToken.None));

        var unknownLength = new TeamsChatClient(_ => Task.FromResult(Ok(Session())), new FakeHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(oversize, "image/png") }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => unknownLength.GetImageAsync(ImageUrl, CancellationToken.None));

        var notImage = new TeamsChatClient(_ => Task.FromResult(Ok(Session())), new FakeHandler(_ => ImageResponse(new byte[] { 1 }, "text/html")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => notImage.GetImageAsync(ImageUrl, CancellationToken.None));
    }

    [Fact]
    public async Task GetImageAsync_TreatsRedirectAsError_WithOneRequest()
    {
        var handler = new FakeHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://evil.test/steal");
            return response;
        });
        var client = new TeamsChatClient(_ => Task.FromResult(Ok(Session())), handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetImageAsync(ImageUrl, CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Client_DefaultHandler_DoesNotFollowRedirects()
    {
        var probe = System.Net.Sockets.TcpListener.Create(0);
        probe.Start();
        var freePort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{freePort}/");
        listener.Start();
        var hits = 0;
        var serve = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                Interlocked.Increment(ref hits);
                context.Response.StatusCode = 302;
                context.Response.RedirectLocation = $"http://localhost:{freePort}/elsewhere";
                context.Response.Close();
            }
        });
        var localBase = $"http://localhost:{freePort}/api/chatsvc/apac/v1/users/ME/";
        var session = new TeamsSession("chat-token", "aggregator-token", localBase, AggregatorBase, "8:orgid:abc", "Test User",
            DateTimeOffset.UtcNow.AddHours(1), "Chrome", null);
        var client = new TeamsChatClient(_ => Task.FromResult(Ok(session)));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetThreadAsync("c", "r", CancellationToken.None));

        listener.Stop();
        await serve;
        Assert.Equal(1, hits);
    }

    [Fact]
    public async Task UploadImageAsync_DoesNotLogTokenOrBytes()
    {
        var secretToken = "secret-token-" + Guid.NewGuid().ToString("N");
        var session = new TeamsSession(secretToken, "aggregator-token", ChatBase, AggregatorBase, "8:orgid:abc", "Test User",
            DateTimeOffset.UtcNow.AddHours(1), "Chrome", null);
        var handler = new FakeHandler(call => call == 1
            ? Json(HttpStatusCode.Created, "{\"id\":\"obj\"}")
            : new HttpResponseMessage(HttpStatusCode.BadRequest));
        var client = new TeamsChatClient(_ => Task.FromResult(Ok(session)), handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.UploadImageAsync("c", new TeamsImage(Encoding.ASCII.GetBytes("IMAGEBYTES-MARKER"), "x.png", 1, 1), CancellationToken.None));

        var logFile = Path.Combine(AppLogger.DirectoryOverride!, $"app-{DateTime.Now:yyyy-MM-dd}.log");
        var log = File.ReadAllText(logFile);
        Assert.DoesNotContain(secretToken, log);
        Assert.DoesNotContain("IMAGEBYTES-MARKER", log);
        Assert.Contains("[teams] image upload failed (400)", log);
    }

    [Fact]
    public async Task EditMessageAsync_PutsToGivenConversation_WithSubject()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, ""));
        var client = new TeamsChatClient(_ => Task.FromResult(Ok(Session())), handler);
        var body = new TeamsMessageBody("<p>new</p>", "[]", new[] { "obj-1" });

        await client.EditMessageAsync("19:abc@thread.tacv2;messageid=100", "200", "MDP-1 subject", body, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal(ChatBase + "conversations/19%3Aabc%40thread.tacv2%3Bmessageid%3D100/messages/200", request.Url);
        Assert.Equal("Bearer chat-token", request.Authorization);
        using var json = JsonDocument.Parse(request.Body);
        var root = json.RootElement;
        Assert.Equal("<p>new</p>", root.GetProperty("content").GetString());
        Assert.Equal("RichText/Html", root.GetProperty("messagetype").GetString());
        Assert.Equal("text", root.GetProperty("contenttype").GetString());
        Assert.Equal("obj-1", root.GetProperty("amsreferences")[0].GetString());
        Assert.False(root.TryGetProperty("clientmessageid", out _));
        Assert.Equal("MDP-1 subject", root.GetProperty("properties").GetProperty("subject").GetString());
        Assert.Equal("[]", root.GetProperty("properties").GetProperty("mentions").GetString());
        Assert.Equal("TEAMS", root.GetProperty("properties").GetProperty("formatVariant").GetString());
    }

    [Fact]
    public async Task DeleteMessageAsync_SendsSoftDelete()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, "null"));
        var client = new TeamsChatClient(_ => Task.FromResult(Ok(Session())), handler);

        await client.DeleteMessageAsync("19:abc@thread.tacv2", "200", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal(ChatBase + "conversations/19%3Aabc%40thread.tacv2/messages/200?behavior=softDelete", request.Url);
        Assert.Equal("Bearer chat-token", request.Authorization);
    }

    [Fact]
    public async Task EditMessageAsync_ThrowsUncertain_On500()
    {
        var serverError = new FakeHandler(_ => Json(HttpStatusCode.InternalServerError, "{}"));
        await Assert.ThrowsAsync<TeamsReplyUncertainException>(() =>
            new TeamsChatClient(_ => Task.FromResult(Ok(Session())), serverError).EditMessageAsync("c", "m", null, Body("x"), CancellationToken.None));
        Assert.Single(serverError.Requests);

        var timeout = new FakeHandler(_ => throw new TaskCanceledException("timeout", new TimeoutException()));
        await Assert.ThrowsAsync<TeamsReplyUncertainException>(() =>
            new TeamsChatClient(_ => Task.FromResult(Ok(Session())), timeout).DeleteMessageAsync("c", "m", CancellationToken.None));

        var forbidden = new FakeHandler(_ => Json(HttpStatusCode.Forbidden, "{}"));
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new TeamsChatClient(_ => Task.FromResult(Ok(Session())), forbidden).DeleteMessageAsync("c", "m", CancellationToken.None));
        Assert.Equal(HttpStatusCode.Forbidden, failure.StatusCode);
    }

    private static string ChatListPage(string id, string? backwardLink)
    {
        var metadata = backwardLink == null ? "" : $",\"_metadata\":{{\"backwardLink\":\"{backwardLink}\"}}";
        return $"{{\"conversations\":[{{\"id\":\"{id}\",\"threadProperties\":{{\"topic\":\"MDP-1 {id}\"}}}}]{metadata}}}";
    }

    [Fact]
    public async Task ListChatsAsync_FollowsBackwardLink_UpToThreePages()
    {
        var handler = new FakeHandler(call => Json(HttpStatusCode.OK, ChatListPage($"19:p{call}@thread.v2", ChatBase + $"conversations?syncState={call}")));
        var client = new TeamsChatClient(_ => Task.FromResult(Ok(Session())), handler);

        var chats = await client.ListChatsAsync(CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(3, chats.Count);
        Assert.StartsWith(ChatBase + "conversations?view=msnp24Equivalent&pageSize=100&targetType=", handler.Requests[0].Url);
        Assert.EndsWith("&startTime=0", handler.Requests[0].Url);
        Assert.Equal(ChatBase + "conversations?syncState=1", handler.Requests[1].Url);
        Assert.Equal("Bearer chat-token", handler.Requests[0].Authorization);
    }

    [Fact]
    public async Task ListChatsAsync_KeepsFirstPage_AndStops_WhenBackwardLinkHasOtherHost()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, ChatListPage("19:p@thread.v2", "https://evil.test/conversations?syncState=1")));
        var client = new TeamsChatClient(_ => Task.FromResult(Ok(Session())), handler);

        var chats = await client.ListChatsAsync(CancellationToken.None);

        Assert.Equal("19:p@thread.v2", Assert.Single(chats).Id);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ListChatsAsync_FollowsBackwardLink_ThatNamesTheUserInsteadOfMe()
    {
        // Teams writes the next page's link with the user's own id where the first request said ME.
        var userLink = "https://teams.test/api/chatsvc/apac/v1/users/8:orgid:abc/conversations?syncState=1";
        var handler = new FakeHandler(call => Json(HttpStatusCode.OK, ChatListPage($"19:p{call}@thread.v2", call == 1 ? userLink : "")));
        var client = new TeamsChatClient(_ => Task.FromResult(Ok(Session())), handler);

        var chats = await client.ListChatsAsync(CancellationToken.None);

        Assert.Equal(2, chats.Count);
        Assert.Equal(userLink, handler.Requests[1].Url);
    }

    [Fact]
    public async Task GetChatAsync_ReturnsChatThread_OldestFirst()
    {
        var body = """
            {"messages":[
              {"id":"2","messagetype":"Text","content":"later","from":"x/8:orgid:abc","composetime":"2026-10-14T09:00:00Z"},
              {"id":"1","messagetype":"Text","content":"earlier","from":"x/8:orgid:abc","composetime":"2026-10-14T08:00:00Z"}
            ]}
            """;
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, body));
        var client = new TeamsChatClient(_ => Task.FromResult(Ok(Session())), handler);

        var thread = await client.GetChatAsync("19:aaa@thread.v2", CancellationToken.None);

        Assert.True(thread.IsChat);
        Assert.Equal("19:aaa@thread.v2", thread.ChannelId);
        Assert.Equal("", thread.RootId);
        Assert.Equal(new[] { "1", "2" }, thread.Messages.Select(tm => tm.Id).ToArray());
        Assert.Equal(ChatBase + "conversations/19%3Aaaa%40thread.v2/messages?view=msnp24Equivalent&pageSize=200", Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task PostChatMessageAsync_PostsToChatConversation()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.Created, "{\"OriginalArrivalTime\": 42}"));
        var client = new TeamsChatClient(_ => Task.FromResult(Ok(Session())), handler);

        var messageId = await client.PostChatMessageAsync("19:aaa@thread.v2", Body("<p>hi</p>"), CancellationToken.None);

        Assert.Equal("42", messageId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(ChatBase + "conversations/19%3Aaaa%40thread.v2/messages", request.Url);
        Assert.DoesNotContain("messageid", request.Url);
    }
}
