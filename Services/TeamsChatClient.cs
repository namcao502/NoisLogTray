using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NoisLogTray;

// Talks to Teams' internal chat API with the sniffed Bearer tokens only. The session is cached until shortly before it expires.
internal sealed class TeamsChatClient : ITeamsChatClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan UploadRequestTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(2);

    internal const int MaxAvatarBytes = 1024 * 1024;
    internal const int MaxChatListPages = 3;

    private const string ChatTargetTypes =
        "Passport|Skype|Lync|Thread|Agent|ShortCircuit|PSTN|Flxt|NotificationStream|ModernBots|secureConversations|InteropBroadcast";

    private static readonly Regex AvatarContentType = new(@"^image/(jpeg|png|gif|webp)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Func<CancellationToken, Task<TeamsSniffResult>> _sniff;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private volatile TeamsSession? _session;

    internal TeamsChatClient(Func<CancellationToken, Task<TeamsSniffResult>> sniff, HttpMessageHandler? handler = null)
    {
        _sniff = sniff;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<string> GetMyIdAsync(CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(cancellationToken);
        return session.MyId;
    }

    public async Task<IReadOnlyList<TeamsChannel>> ListChannelsAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            ts => $"{ts.AggregatorBaseUrl}api/v3/teams/users/me?isPrefetch=false&enableMembershipSummary=true", true, cancellationToken);
        var channels = TeamsChat.ParseChannels(document.RootElement);
        AppLogger.Info($"[teams] listed {channels.Count} channel(s)");
        return channels;
    }

    public async Task<TeamsMessagePage> GetChannelPageAsync(string channelId, string? backwardLink, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(ts =>
        {
            if (backwardLink == null)
            {
                return $"{ts.ChatBaseUrl}conversations/{Uri.EscapeDataString(channelId)}/messages?view=msnp24Equivalent&pageSize=200";
            }

            if (!backwardLink.StartsWith(ts.ChatBaseUrl, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Teams page link points to an unexpected host");
            }

            return backwardLink;
        }, false, cancellationToken);

        var page = TeamsChat.ParseMessages(document.RootElement);
        AppLogger.Info($"[teams] read a channel page of {page.Messages.Count} message(s)");
        return page;
    }

    public async Task<TeamsThread> GetThreadAsync(string channelId, string rootId, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            ts => $"{ts.ChatBaseUrl}conversations/{Uri.EscapeDataString(channelId + ";messageid=" + rootId)}/messages?view=msnp24Equivalent&pageSize=200",
            false, cancellationToken);

        var messages = TeamsChat.ParseMessages(document.RootElement).Messages;
        if (messages.Any(tm => tm.Id != rootId))
        {
            return new TeamsThread(channelId, rootId, messages);
        }

        // Teams sometimes returns only the root here (seen after a reaction on it); the channel's newest page still holds the replies.
        using var channelDocument = await GetJsonAsync(
            ts => $"{ts.ChatBaseUrl}conversations/{Uri.EscapeDataString(channelId)}/messages?view=msnp24Equivalent&pageSize=200",
            false, cancellationToken);
        var replies = TeamsChat.ParseMessages(channelDocument.RootElement).Messages
            .Where(tm => tm.RootMessageId == rootId && tm.Id != rootId);
        return new TeamsThread(channelId, rootId, messages.Concat(replies).OrderBy(tm => tm.SentAt).ToList());
    }

    public async Task<IReadOnlyList<TeamsGroupChat>> ListChatsAsync(CancellationToken cancellationToken)
    {
        var chats = new List<TeamsGroupChat>();
        string? backwardLink = null;
        for (var pageIndex = 0; pageIndex < MaxChatListPages; pageIndex++)
        {
            var link = backwardLink;
            if (link != null)
            {
                var session = await GetSessionAsync(cancellationToken);
                if (!IsChatListLink(link, session.ChatBaseUrl))
                {
                    AppLogger.Info("[teams] the chat list's next page points elsewhere; kept the pages read so far");
                    break;
                }
            }

            using var document = await GetJsonAsync(ts => link ?? $"{ts.ChatBaseUrl}conversations?view=msnp24Equivalent&pageSize=100&targetType={ChatTargetTypes}&startTime=0",
                false, cancellationToken);

            var page = TeamsChat.ParseChats(document.RootElement);
            chats.AddRange(page.Chats);
            backwardLink = page.BackwardLink;
            if (string.IsNullOrEmpty(backwardLink))
            {
                break;
            }
        }

        AppLogger.Info($"[teams] listed {chats.Count} group chat(s)");
        return chats;
    }

    // Teams writes the next page's link with the user's id where the first request said ME (".../users/8:orgid:<id>/conversations"),
    // so only the origin and the path up to "users/" must match the session's chat base.
    internal static bool IsChatListLink(string link, string chatBaseUrl)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var linkUri) || !Uri.TryCreate(chatBaseUrl, UriKind.Absolute, out var baseUri))
        {
            return false;
        }

        var basePath = baseUri.AbsolutePath;
        var usersIndex = basePath.IndexOf("/users/", StringComparison.Ordinal);
        var usersPath = usersIndex < 0 ? basePath : basePath[..(usersIndex + "/users/".Length)];
        return linkUri.Scheme == Uri.UriSchemeHttps && linkUri.Scheme == baseUri.Scheme
            && string.Equals(linkUri.Authority, baseUri.Authority, StringComparison.OrdinalIgnoreCase)
            && linkUri.AbsolutePath.StartsWith(usersPath, StringComparison.Ordinal);
    }

    public async Task<TeamsThread> GetChatAsync(string chatId, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            ts => $"{ts.ChatBaseUrl}conversations/{Uri.EscapeDataString(chatId)}/messages?view=msnp24Equivalent&pageSize=200", false, cancellationToken);

        var messages = TeamsChat.ParseMessages(document.RootElement).Messages;
        return new TeamsThread(chatId, "", messages, IsChat: true);
    }

    public Task<string> PostChatMessageAsync(string chatId, TeamsMessageBody body, CancellationToken cancellationToken)
    {
        return PostAsync(chatId, null, body, cancellationToken);
    }

    public Task<string> PostReplyAsync(string channelId, string rootId, TeamsMessageBody body, CancellationToken cancellationToken)
    {
        return PostAsync(channelId + ";messageid=" + rootId, null, body, cancellationToken);
    }

    public Task<string> PostMessageAsync(string channelId, string subject, TeamsMessageBody body, CancellationToken cancellationToken)
    {
        return PostAsync(channelId, subject, body, cancellationToken);
    }

    public Task EditMessageAsync(string conversationId, string messageId, string? subject, TeamsMessageBody body, CancellationToken cancellationToken)
    {
        return SendChangeAsync("edit", ts =>
        {
            var json = JsonSerializer.Serialize(new
            {
                content = body.Html,
                messagetype = "RichText/Html",
                contenttype = "text",
                amsreferences = body.AmsReferences,
                properties = new { importance = "", subject, mentions = body.MentionsJson, formatVariant = "TEAMS" },
            });

            var request = CreateRequest(HttpMethod.Put, MessageUrl(ts, conversationId, messageId), ts.ChatToken);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return request;
        }, cancellationToken);
    }

    public Task DeleteMessageAsync(string conversationId, string messageId, CancellationToken cancellationToken)
    {
        return SendChangeAsync("delete", ts => CreateRequest(HttpMethod.Delete, MessageUrl(ts, conversationId, messageId) + "?behavior=softDelete", ts.ChatToken),
            cancellationToken);
    }

    public async Task<string> UploadImageAsync(string channelId, TeamsImage image, CancellationToken cancellationToken)
    {
        string objectId;
        using (var createResponse = await SendAsync(ts =>
        {
            var request = CreateAmsRequest(HttpMethod.Post, TeamsCompose.AmsObjectsUrl, ts);
            var createBody = JsonSerializer.Serialize(new
            {
                type = "pish/image",
                permissions = new Dictionary<string, string[]> { [channelId] = new[] { "read" } },
                filename = image.FileName,
            });
            request.Content = new StringContent(createBody, Encoding.UTF8, "application/json");
            return request;
        }, cancellationToken, UploadRequestTimeout))
        {
            EnsureUploadSuccess(createResponse);

            using var document = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("id", out var idElement)
                || idElement.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(idElement.GetString()))
            {
                throw new InvalidOperationException("Teams did not return an id for the uploaded image");
            }

            objectId = idElement.GetString()!;
        }

        using var putResponse = await SendAsync(ts =>
        {
            var request = CreateAmsRequest(HttpMethod.Put,
                $"{TeamsCompose.AmsObjectsUrl}/{Uri.EscapeDataString(objectId)}/content/imgpsh", ts);
            var content = new ByteArrayContent(image.Bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content = content;
            return request;
        }, cancellationToken, UploadRequestTimeout);
        EnsureUploadSuccess(putResponse);

        AppLogger.Info($"[teams] image uploaded ({image.Bytes.Length} bytes)");
        return objectId;
    }

    public async Task<(byte[] Bytes, string ContentType)> GetImageAsync(string url, CancellationToken cancellationToken)
    {
        if (!TeamsChat.IsAmsImageUrl(url))
        {
            throw new InvalidOperationException("Teams image link points to an unexpected host");
        }

        using var response = await SendAsync(ts => CreateAmsRequest(HttpMethod.Get, url, ts), cancellationToken, RequestTimeout,
            HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Teams answered the image request with something that is not an image");
        }

        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength != null && declaredLength.Value > TeamsCompose.MaxImageBytes)
        {
            throw new InvalidOperationException("The Teams image is too large");
        }

        return (await ReadLimitedAsync(response, TeamsCompose.MaxImageBytes, cancellationToken), contentType);
    }

    public async Task<(byte[] Bytes, string ContentType)?> GetProfilePictureAsync(string mri, string displayName, CancellationToken cancellationToken)
    {
        if (!TeamsCompose.IsPersonMri(mri))
        {
            throw new ArgumentException("Not a Teams person id", nameof(mri));
        }

        var session = await GetSessionAsync(cancellationToken);
        var tokenExpired = session.ProfileTokenExpiresAt != null && session.ProfileTokenExpiresAt.Value <= DateTimeOffset.UtcNow;
        if (string.IsNullOrEmpty(session.ProfileToken) || string.IsNullOrEmpty(session.ProfilePictureBaseUrl) || tokenExpired)
        {
            return null;
        }

        // A refusal here only means no picture: it must not sign the chat session out or start a sniff.
        try
        {
            var url = $"{session.ProfilePictureBaseUrl}{mri}?displayname={Uri.EscapeDataString(displayName)}&size=HR64x64";
            using var request = CreateAmsRequest(HttpMethod.Get, url, session, session.ProfileToken);
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(RequestTimeout);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                AppLogger.Info($"[teams] avatar not loaded ({(int)response.StatusCode})");
                return null;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            var declaredLength = response.Content.Headers.ContentLength;
            if (!AvatarContentType.IsMatch(contentType) || (declaredLength != null && declaredLength.Value > MaxAvatarBytes))
            {
                AppLogger.Info("[teams] avatar not loaded (not a usable image)");
                return null;
            }

            return (await ReadLimitedAsync(response, MaxAvatarBytes, cancellationToken), contentType.ToLowerInvariant());
        }
        catch (InvalidOperationException)
        {
            AppLogger.Info("[teams] avatar not loaded (too large)");
            return null;
        }
        catch (HttpRequestException ex)
        {
            AppLogger.Info($"[teams] avatar not loaded ({(ex.StatusCode == null ? "no status" : ((int)ex.StatusCode.Value).ToString())})");
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppLogger.Info("[teams] avatar not loaded (timeout)");
            return null;
        }
    }

    // Reads the body up to maxBytes within the request timeout; throws InvalidOperationException past the limit.
    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readTimeout.CancelAfter(RequestTimeout);
        await using var stream = await response.Content.ReadAsStreamAsync(readTimeout.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int readCount;
        while ((readCount = await stream.ReadAsync(chunk, readTimeout.Token)) != 0)
        {
            if (buffer.Length + readCount > maxBytes)
            {
                throw new InvalidOperationException("The Teams image is too large");
            }

            buffer.Write(chunk, 0, readCount);
        }

        return buffer.ToArray();
    }

    private static void EnsureUploadSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            AppLogger.Error($"[teams] image upload failed ({(int)response.StatusCode})");
        }

        response.EnsureSuccessStatusCode();
    }

    private static string MessageUrl(TeamsSession session, string conversationId, string messageId)
    {
        return $"{session.ChatBaseUrl}conversations/{Uri.EscapeDataString(conversationId)}/messages/{Uri.EscapeDataString(messageId)}";
    }

    // An edit or delete answers with an empty body, so success is the status alone; a lost answer may still have been applied.
    private async Task SendChangeAsync(string action, Func<TeamsSession, HttpRequestMessage> buildRequest, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(buildRequest, cancellationToken);
            if ((int)response.StatusCode >= 500)
            {
                AppLogger.Error($"[teams] {action} failed ({(int)response.StatusCode})");
                throw new TeamsReplyUncertainException($"Teams answered {(int)response.StatusCode}; the {action} may have been applied");
            }

            if (!response.IsSuccessStatusCode)
            {
                AppLogger.Error($"[teams] {action} failed ({(int)response.StatusCode})");
            }

            response.EnsureSuccessStatusCode();
            AppLogger.Info($"[teams] {action} sent");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == null)
        {
            AppLogger.Error($"[teams] {action} request failed without a status: {ex.Message}");
            throw new TeamsReplyUncertainException($"The {action} request failed; it may have been applied", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            AppLogger.Error($"[teams] {action} request timed out");
            throw new TeamsReplyUncertainException($"The {action} request timed out; it may have been applied", ex);
        }
    }

    private async Task<string> PostAsync(string conversationId, string? subject, TeamsMessageBody messageBody, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(ts =>
            {
                var body = JsonSerializer.Serialize(new
                {
                    content = messageBody.Html,
                    messagetype = "RichText/Html",
                    contenttype = "text",
                    amsreferences = messageBody.AmsReferences,
                    clientmessageid = Random.Shared.NextInt64(1_000_000_000_000_000_000, long.MaxValue).ToString(CultureInfo.InvariantCulture),
                    imdisplayname = ts.MyName,
                    properties = new { importance = "", subject, mentions = messageBody.MentionsJson, formatVariant = "TEAMS" },
                });

                var request = CreateRequest(HttpMethod.Post,
                    $"{ts.ChatBaseUrl}conversations/{Uri.EscapeDataString(conversationId)}/messages", ts.ChatToken);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                return request;
            }, cancellationToken);

            if ((int)response.StatusCode >= 500)
            {
                throw new TeamsReplyUncertainException($"Teams answered {(int)response.StatusCode}; the message may have been posted");
            }

            response.EnsureSuccessStatusCode();

            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(responseText);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("OriginalArrivalTime", out var arrivalTime))
            {
                if (arrivalTime.ValueKind == JsonValueKind.Number)
                {
                    AppLogger.Info("[teams] message posted");
                    return arrivalTime.GetInt64().ToString(CultureInfo.InvariantCulture);
                }

                if (arrivalTime.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(arrivalTime.GetString()))
                {
                    AppLogger.Info("[teams] message posted");
                    return arrivalTime.GetString()!;
                }
            }

            throw new TeamsReplyUncertainException("Teams accepted the message but did not return its id");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == null)
        {
            AppLogger.Error($"[teams] post request failed without a status: {ex.Message}");
            throw new TeamsReplyUncertainException("The post request failed; it may have been posted", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            AppLogger.Error("[teams] post request timed out");
            throw new TeamsReplyUncertainException("The post request timed out; it may have been posted", ex);
        }
        catch (JsonException ex)
        {
            throw new TeamsReplyUncertainException("Teams answered the post with an unreadable body", ex);
        }
    }

    public void InvalidateSession()
    {
        _session = null;
    }

    private async Task<TeamsSession> GetSessionAsync(CancellationToken cancellationToken)
    {
        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            var cached = _session;
            if (cached != null && DateTimeOffset.UtcNow < cached.ExpiresAt - ExpiryMargin)
            {
                return cached;
            }

            var result = await _sniff(cancellationToken);
            if (result.Session != null)
            {
                _session = result.Session;
                return result.Session;
            }

            if (result.SignedOut)
            {
                throw new TeamsSignedOutException();
            }

            throw new InvalidOperationException(result.Error ?? "Teams sign-in check failed");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    // One re-sniff and retry on a 401. A 401 means Teams did not act on the request, so retrying a POST cannot duplicate it.
    private async Task<HttpResponseMessage> SendAsync(Func<TeamsSession, HttpRequestMessage> buildRequest, CancellationToken cancellationToken,
        TimeSpan? timeout = null, HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var session = await GetSessionAsync(cancellationToken);
            using var request = buildRequest(session);
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(timeout ?? RequestTimeout);
            var response = await _http.SendAsync(request, completionOption, requestTimeout.Token);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return response;
            }

            response.Dispose();
            InvalidateSession();
            AppLogger.Info("[teams] token rejected (401)");
        }

        throw new TeamsSignedOutException();
    }

    private async Task<JsonDocument> GetJsonAsync(Func<TeamsSession, string> urlFor, bool useAggregatorToken, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            ts => CreateRequest(HttpMethod.Get, urlFor(ts), useAggregatorToken ? ts.AggregatorToken : ts.ChatToken), cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static HttpRequestMessage CreateAmsRequest(HttpMethod method, string url, TeamsSession session, string? token = null)
    {
        var request = CreateRequest(method, url, token ?? session.ChatToken);
        request.Headers.TryAddWithoutValidation("User-Agent", session.UserAgent);
        request.Headers.TryAddWithoutValidation("x-ms-client-type", "web");
        if (session.ClientVersion != null)
        {
            request.Headers.TryAddWithoutValidation("x-ms-client-version", session.ClientVersion);
        }

        request.Headers.TryAddWithoutValidation("Origin", "https://teams.cloud.microsoft");
        request.Headers.TryAddWithoutValidation("Referer", "https://teams.cloud.microsoft/");
        return request;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
