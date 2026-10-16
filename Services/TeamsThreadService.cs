using System.Net;
using System.Text.Json;

namespace NoisLogTray;

// Finds a ticket's Teams thread through the post index, reads it and sends replies into it. One instance serves every ticket tab.
internal sealed partial class TeamsThreadService
{
    internal const int MaxPagesPerRead = 15;
    internal const int MaxRecentChannels = 3;
    internal const int MaxPeople = 300;
    internal const int MaxCachedImageBytes = 32 * 1024 * 1024;
    internal const int MaxParallelImageLoads = 3;
    internal const int MaxCachedAvatars = 300;

    private const int MaxParallelProbes = 4;
    private const string NotYourMessageText = "You can only change your own messages.";
    private static readonly IReadOnlySet<string> NoKeptAmsIds = new HashSet<string>(StringComparer.Ordinal);
    private const int MinNewPagesPerSync = 3;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ChannelCacheLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SyncOverlap = TimeSpan.FromDays(1);
    private static readonly TimeSpan AvatarMissLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AvatarDiskLifetime = TimeSpan.FromDays(7);
    private static readonly Dictionary<string, string> AvatarExtensions = new()
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
    };

    private readonly ITeamsChatClient _client;
    private readonly TeamsPostIndex _postIndex;
    private readonly Func<AppSettings> _loadSettings;
    private readonly Action<AppSettings> _saveSettings;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _channelListLock = new(1, 1);
    private readonly SemaphoreSlim _channelDetectionLock = new(1, 1);
    private readonly Dictionary<string, SemaphoreSlim> _channelSyncLocks = new();
    private readonly object _peopleLock = new();
    private readonly SemaphoreSlim _imageLoadLimit = new(MaxParallelImageLoads);
    private readonly object _imageCacheLock = new();
    private readonly LinkedList<CachedImage> _imageCacheOrder = new();
    private readonly Dictionary<string, LinkedListNode<CachedImage>> _imageCacheNodes = new();
    private long _imageCacheBytes;
    private readonly string _avatarsDirectory;
    private readonly object _avatarLock = new();
    private readonly Dictionary<string, CachedAvatar> _avatarCache = new();
    private readonly Dictionary<string, Task<string?>> _avatarLoads = new();
    private ChannelListSnapshot? _channelList;

    internal TeamsThreadService(
        ITeamsChatClient client,
        TeamsPostIndex postIndex,
        Func<AppSettings> loadSettings,
        Action<AppSettings> saveSettings,
        Func<DateTimeOffset>? now = null,
        string? avatarsDirectory = null)
    {
        _client = client;
        _postIndex = postIndex;
        _loadSettings = loadSettings;
        _saveSettings = saveSettings;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _avatarsDirectory = Path.Combine(avatarsDirectory ?? AppPaths.TeamsAvatarsDir, "HR64");
    }

    internal async Task<TeamsThreadResult> ReadAsync(string ticketKey, string? chatId, CancellationToken cancellationToken, bool quiet = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);

        TeamsThreadResult result;
        try
        {
            if (chatId == null)
            {
                result = await ReadCoreAsync(ticketKey, timeout.Token);
                if (result.State != TeamsThreadState.Failed)
                {
                    result = result with { Chats = await GetTicketChatsAsync(ticketKey, timeout.Token) };
                }
            }
            else
            {
                result = await ReadChatCoreAsync(ticketKey, chatId, timeout.Token);
            }
        }
        catch (TeamsSignedOutException)
        {
            result = new TeamsThreadResult(TeamsThreadState.SignedOut, null, "", Array.Empty<TeamsChannel>(), null, null);
        }
        catch (Exception ex) when (IsExpectedFailure(ex, cancellationToken))
        {
            result = new TeamsThreadResult(TeamsThreadState.Failed, null, "", Array.Empty<TeamsChannel>(), null, ShortError(ex));
        }

        // Read after the search, which may have detected a channel and put it first.
        if (result.Channels.Count != 0)
        {
            result = result with { RecentChannelIds = _loadSettings().TeamsRecentChannelIds, People = PeopleFor(result.Thread, result.MyId) };
        }

        if (quiet && result.State == TeamsThreadState.Found)
        {
            return result;
        }
        AppLogger.Info($"[teams] {ticketKey}: {result.State}{(result.StillSearching ? " (still searching)" : "")}{(chatId != null ? " (chat)" : "")}");
        return result;
    }

    // Saves the channel and puts it first in the recent list; the post index keeps every channel's posts.
    internal void SelectChannel(string channelId)
    {
        var settings = _loadSettings();
        settings.TeamsChannelId = channelId;
        settings.TeamsRecentChannelIds = WithRecentChannel(settings.TeamsRecentChannelIds, channelId);
        _saveSettings(settings);
        Volatile.Write(ref _channelList, null);
    }

    internal async Task<TeamsReplyResult> SendReplyAsync(string ticketKey, TeamsThread thread, TeamsDraft draft, CancellationToken cancellationToken)
    {
        if (TeamsCompose.IsEmptyHtml(draft.Html) && draft.Images.Count == 0)
        {
            AppLogger.Info($"[teams] {ticketKey}: reply failed");
            return new TeamsReplyResult(TeamsReplyState.Failed, null, "The reply is empty.");
        }

        var (uploadedImages, imageError) = await UploadImagesAsync(thread.ChannelId, draft.Images, cancellationToken);
        if (imageError != null)
        {
            AppLogger.Info($"[teams] {ticketKey}: reply failed");
            return new TeamsReplyResult(TeamsReplyState.Failed, null, imageError);
        }

        try
        {
            var replyBody = TeamsCompose.BuildBody(draft.Html, draft.Mentions, uploadedImages, NoKeptAmsIds);
            if (thread.IsChat)
            {
                await _client.PostChatMessageAsync(thread.ChannelId, replyBody, cancellationToken);
            }
            else
            {
                await _client.PostReplyAsync(thread.ChannelId, thread.RootId, replyBody, cancellationToken);
            }
        }
        catch (TeamsReplyUncertainException ex)
        {
            AppLogger.Info($"[teams] {ticketKey}: reply uncertain");
            return new TeamsReplyResult(TeamsReplyState.Uncertain, null, ex.Message);
        }
        catch (TeamsSignedOutException ex)
        {
            AppLogger.Info($"[teams] {ticketKey}: reply failed");
            return new TeamsReplyResult(TeamsReplyState.Failed, null, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            AppLogger.Info($"[teams] {ticketKey}: reply failed");
            return new TeamsReplyResult(TeamsReplyState.Failed, null, ShortError(ex));
        }

        AppLogger.Info($"[teams] {ticketKey}: reply sent");

        // The reply is already posted, so a failed re-read leaves After null and the view reads again on its own.
        var after = await RereadThreadAsync(ticketKey, thread, cancellationToken);
        return new TeamsReplyResult(TeamsReplyState.Sent, after, null);
    }

    // Posts the ticket announcement as a new post in the selected channel.
    internal async Task<TeamsReplyResult> PostAnnouncementAsync(string ticketKey, string subject, TeamsDraft draft, CancellationToken cancellationToken)
    {
        var channelId = _loadSettings().TeamsChannelId;
        if (string.IsNullOrEmpty(channelId))
        {
            AppLogger.Info($"[teams] {ticketKey}: post failed");
            return new TeamsReplyResult(TeamsReplyState.Failed, null, "Pick a channel first");
        }

        var trimmedSubject = subject.Trim();
        if (trimmedSubject.Length == 0 || (TeamsCompose.IsEmptyHtml(draft.Html) && draft.Images.Count == 0))
        {
            AppLogger.Info($"[teams] {ticketKey}: post failed");
            return new TeamsReplyResult(TeamsReplyState.Failed, null, "The post needs a subject line and a description.");
        }

        var (uploadedImages, imageError) = await UploadImagesAsync(channelId, draft.Images, cancellationToken);
        if (imageError != null)
        {
            AppLogger.Info($"[teams] {ticketKey}: post failed");
            return new TeamsReplyResult(TeamsReplyState.Failed, null, imageError);
        }

        string postId;
        try
        {
            postId = await _client.PostMessageAsync(channelId, trimmedSubject, TeamsCompose.BuildBody(draft.Html, draft.Mentions, uploadedImages, NoKeptAmsIds), cancellationToken);
        }
        catch (TeamsReplyUncertainException ex)
        {
            AppLogger.Info($"[teams] {ticketKey}: post uncertain");
            return new TeamsReplyResult(TeamsReplyState.Uncertain, null, ex.Message);
        }
        catch (TeamsSignedOutException ex)
        {
            AppLogger.Info($"[teams] {ticketKey}: post failed");
            return new TeamsReplyResult(TeamsReplyState.Failed, null, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            AppLogger.Info($"[teams] {ticketKey}: post failed");
            return new TeamsReplyResult(TeamsReplyState.Failed, null, ShortError(ex));
        }

        AppLogger.Info($"[teams] {ticketKey}: post sent");
        var postKey = ticketKey.ToUpperInvariant();
        var sentPost = new TeamsIndexedPost(postId, _now(), true);
        _postIndex.Update(channelId, tci => tci with
        {
            Posts = new Dictionary<string, TeamsIndexedPost>(tci.Posts) { [postKey] = sentPost },
        });

        // The post is already out, so a failed read leaves After null and the view reads again on its own.
        var after = await ReadAsync(ticketKey, null, cancellationToken);
        return new TeamsReplyResult(TeamsReplyState.Sent, after.State == TeamsThreadState.Found ? after : null, null);
    }

    // Edits one of the user's own messages; a root post keeps its ticket key in the subject. After is the re-read thread.
    internal async Task<TeamsReplyResult> EditAsync(string ticketKey, TeamsThread thread, string messageId, string? subject, TeamsDraft draft,
        CancellationToken cancellationToken)
    {
        var message = thread.Messages.FirstOrDefault(tm => tm.Id == messageId);
        var isRoot = !thread.IsChat && messageId == thread.RootId;
        var sentSubject = isRoot ? subject?.Trim() : null;

        string myId;
        try
        {
            myId = await _client.GetMyIdAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is TeamsSignedOutException || IsExpectedFailure(ex, cancellationToken))
        {
            AppLogger.Info($"[teams] {ticketKey}: edit failed");
            return new TeamsReplyResult(TeamsReplyState.Failed, null, ShortError(ex));
        }

        var refusal = EditRefusal(ticketKey, message, myId, isRoot, sentSubject, draft);
        if (refusal != null)
        {
            AppLogger.Info($"[teams] {ticketKey}: edit failed");
            return refusal;
        }

        var (uploadedImages, imageError) = await UploadImagesAsync(thread.ChannelId, draft.Images, cancellationToken);
        if (imageError != null)
        {
            AppLogger.Info($"[teams] {ticketKey}: edit failed");
            return new TeamsReplyResult(TeamsReplyState.Failed, null, imageError);
        }

        var body = TeamsCompose.BuildBody(draft.Html, draft.Mentions, uploadedImages, TeamsChat.AmsObjectIds(message!.Content));
        var failure = await RunChangeAsync("edit", ticketKey,
            () => _client.EditMessageAsync(ConversationFor(thread, isRoot), messageId, sentSubject, body, cancellationToken));
        if (failure != null)
        {
            return failure;
        }

        return new TeamsReplyResult(TeamsReplyState.Sent, await RereadThreadAsync(ticketKey, thread, cancellationToken), null);
    }

    // Deletes one of the user's own messages. Deleting the root post drops it from the index; After is then a fresh read of the ticket.
    internal async Task<TeamsReplyResult> DeleteAsync(string ticketKey, TeamsThread thread, string messageId, CancellationToken cancellationToken)
    {
        var message = thread.Messages.FirstOrDefault(tm => tm.Id == messageId);
        var isRoot = !thread.IsChat && messageId == thread.RootId;

        string myId;
        try
        {
            myId = await _client.GetMyIdAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is TeamsSignedOutException || IsExpectedFailure(ex, cancellationToken))
        {
            AppLogger.Info($"[teams] {ticketKey}: delete failed");
            return new TeamsReplyResult(TeamsReplyState.Failed, null, ShortError(ex));
        }

        if (message == null || !string.Equals(message.SenderId, myId, StringComparison.Ordinal))
        {
            AppLogger.Info($"[teams] {ticketKey}: delete failed");
            return new TeamsReplyResult(TeamsReplyState.Failed, null, NotYourMessageText);
        }

        var failure = await RunChangeAsync("delete", ticketKey,
            () => _client.DeleteMessageAsync(ConversationFor(thread, isRoot), messageId, cancellationToken));
        if (failure != null)
        {
            return failure;
        }

        if (!isRoot)
        {
            return new TeamsReplyResult(TeamsReplyState.Sent, await RereadThreadAsync(ticketKey, thread, cancellationToken), null);
        }

        var postKey = ticketKey.ToUpperInvariant();
        _postIndex.Update(thread.ChannelId, tci =>
        {
            var isTheDeletedPost = tci.Posts.TryGetValue(postKey, out var indexedPost) && indexedPost.RootId == messageId;
            return isTheDeletedPost ? TeamsPostIndex.WithoutPost(tci, postKey) : tci;
        });
        return new TeamsReplyResult(TeamsReplyState.Sent, await ReadAsync(ticketKey, null, cancellationToken), null);
    }

    private static TeamsReplyResult? EditRefusal(string ticketKey, TeamsMessage? message, string myId, bool isRoot, string? sentSubject, TeamsDraft draft)
    {
        if (message == null || !string.Equals(message.SenderId, myId, StringComparison.Ordinal))
        {
            return new TeamsReplyResult(TeamsReplyState.Failed, null, NotYourMessageText);
        }

        if (!TeamsChat.IsEditable(message))
        {
            return new TeamsReplyResult(TeamsReplyState.Failed, null, "This message cannot be edited here.");
        }

        if (isRoot && (sentSubject == null || !sentSubject.StartsWith(ticketKey, StringComparison.OrdinalIgnoreCase)))
        {
            return new TeamsReplyResult(TeamsReplyState.Failed, null, $"The subject must start with {ticketKey}.");
        }

        if (TeamsCompose.IsEmptyHtml(draft.Html) && draft.Images.Count == 0)
        {
            return new TeamsReplyResult(TeamsReplyState.Failed, null, "The message is empty.");
        }

        return null;
    }

    private static string ConversationFor(TeamsThread thread, bool isRoot)
    {
        return isRoot || thread.IsChat ? thread.ChannelId : thread.ChannelId + ";messageid=" + thread.RootId;
    }

    // Runs an edit or delete; null when it went through, else the result to return.
    private static async Task<TeamsReplyResult?> RunChangeAsync(string action, string ticketKey, Func<Task> send)
    {
        try
        {
            await send();
        }
        catch (TeamsReplyUncertainException ex)
        {
            AppLogger.Info($"[teams] {ticketKey}: {action} uncertain");
            return new TeamsReplyResult(TeamsReplyState.Uncertain, null, ex.Message);
        }
        catch (TeamsSignedOutException ex)
        {
            AppLogger.Info($"[teams] {ticketKey}: {action} failed");
            return new TeamsReplyResult(TeamsReplyState.Failed, null, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            AppLogger.Info($"[teams] {ticketKey}: {action} failed");
            return new TeamsReplyResult(TeamsReplyState.Failed, null, ShortError(ex));
        }

        AppLogger.Info($"[teams] {ticketKey}: {action} sent");
        return null;
    }

    // Null when the re-read fails: the change is already in Teams, so the view reads again on its own.
    private async Task<TeamsThreadResult?> RereadThreadAsync(string ticketKey, TeamsThread thread, CancellationToken cancellationToken)
    {
        if (thread.IsChat)
        {
            return await RereadChatAsync(ticketKey, thread, cancellationToken);
        }

        try
        {
            var myId = await _client.GetMyIdAsync(cancellationToken);
            var channels = await GetChannelsAsync(cancellationToken);
            var rereadThread = await _client.GetThreadAsync(thread.ChannelId, thread.RootId, cancellationToken);
            CollectPeople(rereadThread.Messages, myId);
            return new TeamsThreadResult(TeamsThreadState.Found, rereadThread, myId, channels, thread.ChannelId, null,
                _loadSettings().TeamsRecentChannelIds, PeopleFor(rereadThread, myId));
        }
        catch (Exception ex) when (ex is TeamsSignedOutException || IsExpectedFailure(ex, cancellationToken))
        {
            return null;
        }
    }

    // Loads an AMS image through the chat token as a data URL; null when it cannot be shown. Bytes are cached.
    internal async Task<string?> GetImageDataUrlAsync(string url, CancellationToken cancellationToken)
    {
        if (!TeamsChat.IsAmsImageUrl(url))
        {
            return null;
        }

        lock (_imageCacheLock)
        {
            if (_imageCacheNodes.TryGetValue(url, out var cachedNode))
            {
                _imageCacheOrder.Remove(cachedNode);
                _imageCacheOrder.AddFirst(cachedNode);
                return ToDataUrl(cachedNode.Value.Bytes, cachedNode.Value.ContentType);
            }
        }

        await _imageLoadLimit.WaitAsync(cancellationToken);
        try
        {
            var (bytes, contentType) = await _client.GetImageAsync(url, cancellationToken);
            CacheImage(url, bytes, contentType);
            return ToDataUrl(bytes, contentType);
        }
        catch (Exception ex) when (ex is TeamsSignedOutException || IsExpectedFailure(ex, cancellationToken))
        {
            var status = ex is HttpRequestException httpException && httpException.StatusCode != null
                ? ((int)httpException.StatusCode.Value).ToString()
                : ex.GetType().Name;
            AppLogger.Info($"[teams] image not loaded ({status})");
            return null;
        }
        finally
        {
            _imageLoadLimit.Release();
        }
    }

    // A person's profile picture as a data URL, cached in memory and on disk; null when there is none. Callers share one download per person.
    internal async Task<string?> GetAvatarDataUrlAsync(string mri, string displayName, CancellationToken cancellationToken)
    {
        if (!TeamsCompose.IsPersonMri(mri))
        {
            return null;
        }

        var objectId = mri.Substring("8:orgid:".Length);
        Task<string?> load;
        lock (_avatarLock)
        {
            if (_avatarCache.TryGetValue(objectId, out var cached) && (cached.DataUrl != null || _now() - cached.At < AvatarMissLifetime))
            {
                return cached.DataUrl;
            }

            if (!_avatarLoads.TryGetValue(objectId, out load!))
            {
                // The download outlives its first caller, so it does not take the caller's token; the client's own timeout bounds it.
                load = Task.Run(() => LoadAvatarAsync(mri, objectId, displayName));
                _avatarLoads[objectId] = load;
            }
        }

        return await load.WaitAsync(cancellationToken);
    }

    private async Task<string?> LoadAvatarAsync(string mri, string objectId, string displayName)
    {
        string? dataUrl = null;
        try
        {
            dataUrl = ReadAvatarFromDisk(objectId);
            if (dataUrl == null)
            {
                dataUrl = await DownloadAvatarAsync(mri, objectId, displayName);
            }
        }
        finally
        {
            lock (_avatarLock)
            {
                _avatarCache[objectId] = new CachedAvatar(dataUrl, _now());
                while (_avatarCache.Count > MaxCachedAvatars)
                {
                    var oldestObjectId = _avatarCache.MinBy(kv => kv.Value.At).Key;
                    _avatarCache.Remove(oldestObjectId);
                }

                _avatarLoads.Remove(objectId);
            }
        }

        return dataUrl;
    }

    private string? ReadAvatarFromDisk(string objectId)
    {
        try
        {
            if (!Directory.Exists(_avatarsDirectory))
            {
                return null;
            }

            foreach (var file in Directory.EnumerateFiles(_avatarsDirectory, objectId + ".*"))
            {
                var contentType = AvatarExtensions.FirstOrDefault(kv => kv.Value == Path.GetExtension(file)).Key;
                if (contentType != null && _now().UtcDateTime - File.GetLastWriteTimeUtc(file) < AvatarDiskLifetime)
                {
                    return ToDataUrl(File.ReadAllBytes(file), contentType);
                }
            }
        }
        catch (IOException ex)
        {
            AppLogger.Info($"[teams] avatar cache not read: {ex.GetType().Name}");
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLogger.Info($"[teams] avatar cache not read: {ex.GetType().Name}");
        }

        return null;
    }

    private async Task<string?> DownloadAvatarAsync(string mri, string objectId, string displayName)
    {
        await _imageLoadLimit.WaitAsync();
        try
        {
            var picture = await _client.GetProfilePictureAsync(mri, displayName, CancellationToken.None);
            if (picture == null)
            {
                return null;
            }

            WriteAvatarToDisk(objectId, picture.Value.Bytes, picture.Value.ContentType);
            return ToDataUrl(picture.Value.Bytes, picture.Value.ContentType);
        }
        catch (Exception ex) when (ex is TeamsSignedOutException || IsExpectedFailure(ex, CancellationToken.None))
        {
            AppLogger.Info($"[teams] avatar not loaded ({ex.GetType().Name})");
            return null;
        }
        finally
        {
            _imageLoadLimit.Release();
        }
    }

    private void WriteAvatarToDisk(string objectId, byte[] bytes, string contentType)
    {
        if (!AvatarExtensions.TryGetValue(contentType, out var extension))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_avatarsDirectory);
            var path = Path.Combine(_avatarsDirectory, objectId + extension);
            var temporaryPath = path + ".tmp";
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, path, true);
            foreach (var otherFile in Directory.EnumerateFiles(_avatarsDirectory, objectId + ".*").Where(f => f != path))
            {
                File.Delete(otherFile);
            }
        }
        catch (IOException ex)
        {
            AppLogger.Info($"[teams] avatar cache not written: {ex.GetType().Name}");
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLogger.Info($"[teams] avatar cache not written: {ex.GetType().Name}");
        }
    }

    // Uploads one image after another; the first failure ends it, so nothing is posted with a picture missing.
    private async Task<(List<(string ObjectId, TeamsImage Image)> Uploaded, string? Error)> UploadImagesAsync(
        string channelId, IReadOnlyList<TeamsImage> images, CancellationToken cancellationToken)
    {
        var uploaded = new List<(string ObjectId, TeamsImage Image)>();
        if (images.Count > TeamsCompose.MaxImages)
        {
            return (uploaded, $"At most {TeamsCompose.MaxImages} images per message.");
        }

        foreach (var image in images)
        {
            var problem = TeamsCompose.ImageProblem(image);
            if (problem != null)
            {
                return (uploaded, TeamsCompose.ImageProblemText(problem.Value, image.FileName));
            }
        }

        if (images.Sum(ti => (long)ti.Bytes.Length) > TeamsCompose.MaxTotalImageBytes)
        {
            return (uploaded, $"The images are over {TeamsCompose.MaxTotalImageBytes / (1024 * 1024)} MB in all.");
        }

        foreach (var image in images)
        {
            try
            {
                var objectId = await _client.UploadImageAsync(channelId, image, cancellationToken);
                uploaded.Add((objectId, image));
            }
            catch (Exception ex) when (ex is TeamsSignedOutException || IsExpectedFailure(ex, cancellationToken))
            {
                return (uploaded, $"Could not upload {image.FileName}");
            }
        }

        return (uploaded, null);
    }

    // Looks in the selected channel, then the recent ones: an own post first, then the selected channel, then the newest.
    private async Task<TeamsThreadResult> ReadCoreAsync(string ticketKey, CancellationToken cancellationToken)
    {
        var myId = await _client.GetMyIdAsync(cancellationToken);
        var channels = await GetChannelsAsync(cancellationToken);

        var selectedChannelId = await ResolveChannelAsync(myId, channels, cancellationToken);
        if (selectedChannelId == null)
        {
            return new TeamsThreadResult(TeamsThreadState.NotPosted, null, myId, channels, null, null);
        }

        var lookupChannelIds = new[] { selectedChannelId }
            .Concat(_loadSettings().TeamsRecentChannelIds
                .Where(rc => rc != selectedChannelId && channels.Any(tc => tc.Id == rc))
                .Distinct()
                .Take(MaxRecentChannels))
            .ToList();
        var postKey = ticketKey.ToUpperInvariant();
        var unreadableChannelIds = await SyncLookupChannelsAsync(lookupChannelIds, postKey, myId, cancellationToken);

        var channelPosts = new List<ChannelPost>();
        var stillSearching = false;
        foreach (var lookupChannelId in lookupChannelIds.Where(lc => !unreadableChannelIds.Contains(lc)))
        {
            var channelIndex = _postIndex.Get(lookupChannelId);
            stillSearching = stillSearching || !channelIndex.DeepScanned;
            if (channelIndex.Posts.TryGetValue(postKey, out var indexedPost))
            {
                channelPosts.Add(new ChannelPost(lookupChannelId, indexedPost));
            }
        }

        var orderedChannelPosts = channelPosts
            .OrderByDescending(cp => cp.Post.Mine)
            .ThenByDescending(cp => cp.ChannelId == selectedChannelId)
            .ThenByDescending(cp => cp.Post.SentAt)
            .ToList();
        foreach (var channelPost in orderedChannelPosts)
        {
            TeamsThread? thread;
            try
            {
                thread = await _client.GetThreadAsync(channelPost.ChannelId, channelPost.Post.RootId, cancellationToken);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                thread = null;
            }

            // A deleted root still answers with its replies; ParseMessage drops the root itself, so only its absence tells.
            if (thread != null && thread.Messages.Any(tm => tm.Id == channelPost.Post.RootId))
            {
                CollectPeople(thread.Messages, myId);
                return new TeamsThreadResult(TeamsThreadState.Found, thread, myId, channels, channelPost.ChannelId, null);
            }

            AppLogger.Info($"[teams] {ticketKey}: the indexed post is gone, dropped it");
            _postIndex.Update(channelPost.ChannelId, tci =>
            {
                // A sync may have indexed a newer post meanwhile; only the gone one is dropped.
                var isTheGonePost = tci.Posts.TryGetValue(postKey, out var currentPost) && currentPost.RootId == channelPost.Post.RootId;
                return isTheGonePost ? TeamsPostIndex.WithoutPost(tci, postKey) : tci;
            });
        }

        return new TeamsThreadResult(TeamsThreadState.NotPosted, null, myId, channels, selectedChannelId, null, StillSearching: stillSearching);
    }

    // Syncs the channels in order on one page budget until the index holds an own post; returns the recent channels it could not read.
    private async Task<List<string>> SyncLookupChannelsAsync(IReadOnlyList<string> lookupChannelIds, string postKey, string myId,
        CancellationToken cancellationToken)
    {
        var unreadableChannelIds = new List<string>();
        var pageBudget = MaxPagesPerRead;
        foreach (var lookupChannelId in lookupChannelIds)
        {
            var hasOwnPost = lookupChannelIds.Any(lc => _postIndex.Get(lc).Posts.TryGetValue(postKey, out var indexedPost) && indexedPost.Mine);
            if (hasOwnPost)
            {
                break;
            }

            try
            {
                pageBudget = await SyncChannelAsync(lookupChannelId, myId, pageBudget, cancellationToken);
            }
            catch (HttpRequestException ex) when (lookupChannelId != lookupChannelIds[0]
                && (ex.StatusCode == HttpStatusCode.Forbidden || ex.StatusCode == HttpStatusCode.NotFound))
            {
                // A recent channel the user can no longer read must not fail every read of the selected one.
                AppLogger.Info($"[teams] skipped a recent channel while searching ({(int)ex.StatusCode.Value})");
                unreadableChannelIds.Add(lookupChannelId);
            }
        }

        return unreadableChannelIds;
    }

    // Indexes the newest pages since the last sync, then goes on with the deep scan; returns the pages left in the budget.
    private async Task<int> SyncChannelAsync(string channelId, string myId, int pageBudget, CancellationToken cancellationToken)
    {
        if (pageBudget <= 0)
        {
            return pageBudget;
        }

        SemaphoreSlim channelSyncLock;
        lock (_channelSyncLocks)
        {
            if (!_channelSyncLocks.TryGetValue(channelId, out var existingLock))
            {
                existingLock = new SemaphoreSlim(1, 1);
                _channelSyncLocks[channelId] = existingLock;
            }

            channelSyncLock = existingLock;
        }

        await channelSyncLock.WaitAsync(cancellationToken);
        try
        {
            // Read under the lock: a sync that just finished elsewhere moved SyncedAt and the cursor.
            var index = _postIndex.Get(channelId);
            TeamsMessagePage? firstPage = null;
            if (index.SyncedAt == null || _now() - index.SyncedAt.Value >= SyncInterval)
            {
                (pageBudget, firstPage) = await SyncNewPagesAsync(channelId, index.SyncedAt, myId, pageBudget, cancellationToken);
            }

            if (index.DeepScanned || pageBudget <= 0)
            {
                return pageBudget;
            }

            return await ContinueDeepScanAsync(channelId, index, firstPage, myId, pageBudget, cancellationToken);
        }
        finally
        {
            channelSyncLock.Release();
        }
    }

    // From the newest page back to the last sync less a day, never stopping before page three; returns the budget left and page one.
    private async Task<(int PageBudget, TeamsMessagePage? FirstPage)> SyncNewPagesAsync(string channelId, DateTimeOffset? syncedAt,
        string myId, int pageBudget, CancellationToken cancellationToken)
    {
        var now = _now();
        TeamsMessagePage? firstPage = null;
        string? pageLink = null;
        var pageCount = 0;
        while (pageBudget > 0)
        {
            var page = await _client.GetChannelPageAsync(channelId, pageLink, cancellationToken);
            pageBudget--;
            pageCount++;
            _postIndex.Update(channelId, tci => TeamsPostIndex.WithPosts(tci, page.Messages, myId));
            if (pageCount == 1)
            {
                CollectPeople(page.Messages, myId);
                firstPage = page;
            }

            // Never synced: page one only, the deep scan reads on. Pages one and two are ordered by last change, so prove nothing.
            var reachedLastSync = syncedAt == null
                || (pageCount >= MinNewPagesPerSync && page.Messages.Count != 0
                    && page.Messages.Max(tm => tm.SentAt) < syncedAt.Value - SyncOverlap);
            if (string.IsNullOrEmpty(page.BackwardLink) || reachedLastSync)
            {
                break;
            }

            pageLink = page.BackwardLink;
        }

        // Also when the budget ran out first: else a long gap is read again on every read and the deep scan never runs.
        _postIndex.Update(channelId, tci => tci with { SyncedAt = now });
        return (pageBudget, firstPage);
    }

    // Reads on from the saved cursor (page two when none) until the budget, the pages or the 40-page limit run out.
    private async Task<int> ContinueDeepScanAsync(string channelId, TeamsChannelIndex index, TeamsMessagePage? firstPage, string myId,
        int pageBudget, CancellationToken cancellationToken)
    {
        var resumesSavedLink = index.DeepScanLink != null;
        var deepScanLink = index.DeepScanLink ?? firstPage?.BackwardLink;
        if (deepScanLink == null && firstPage == null)
        {
            // The scan starts at page two, so it needs page one's backwardLink.
            var pageOne = await _client.GetChannelPageAsync(channelId, null, cancellationToken);
            pageBudget--;
            CollectPeople(pageOne.Messages, myId);
            _postIndex.Update(channelId, tci => TeamsPostIndex.WithPosts(tci, pageOne.Messages, myId));
            deepScanLink = pageOne.BackwardLink;
        }

        var deepScanPages = index.DeepScanPages;
        if (string.IsNullOrEmpty(deepScanLink) || deepScanPages >= TeamsPostIndex.DeepScanPageLimit)
        {
            _postIndex.Update(channelId, tci => tci with { DeepScanned = true, DeepScanLink = null });
            return pageBudget;
        }

        while (pageBudget > 0 && !string.IsNullOrEmpty(deepScanLink))
        {
            TeamsMessagePage page;
            try
            {
                page = await _client.GetChannelPageAsync(channelId, deepScanLink, cancellationToken);
            }
            catch (HttpRequestException ex) when (resumesSavedLink && ex.StatusCode != null
                && (int)ex.StatusCode.Value >= 400 && (int)ex.StatusCode.Value < 500)
            {
                AppLogger.Info($"[teams] the saved deep scan link was refused ({(int)ex.StatusCode.Value}); the scan starts over next time");
                _postIndex.Update(channelId, tci => tci with { DeepScanPages = 0, DeepScanLink = null });
                return pageBudget;
            }
            catch (InvalidOperationException) when (resumesSavedLink)
            {
                // A saved link from another chat host (Teams moved the region) would fail every read until reset.
                AppLogger.Info("[teams] the saved deep scan link points to another host; the scan starts over next time");
                _postIndex.Update(channelId, tci => tci with { DeepScanPages = 0, DeepScanLink = null });
                return pageBudget;
            }

            resumesSavedLink = false;
            pageBudget--;
            deepScanPages++;
            var scannedPages = deepScanPages;
            var deepScanned = string.IsNullOrEmpty(page.BackwardLink) || scannedPages >= TeamsPostIndex.DeepScanPageLimit;
            var nextLink = deepScanned ? null : page.BackwardLink;
            // Saved after every page, so a timeout, cancel or restart goes on from here.
            _postIndex.Update(channelId, tci => TeamsPostIndex.WithPosts(tci, page.Messages, myId) with
            {
                DeepScanned = deepScanned,
                DeepScanPages = scannedPages,
                DeepScanLink = nextLink,
            });
            deepScanLink = nextLink;
        }

        return pageBudget;
    }

    // The saved channel while it still exists; else the channel whose newest page holds the user's newest ticket post, saved for next time.
    private async Task<string?> ResolveChannelAsync(string myId, IReadOnlyList<TeamsChannel> channels, CancellationToken cancellationToken)
    {
        await _channelDetectionLock.WaitAsync(cancellationToken);
        try
        {
            var savedChannelId = _loadSettings().TeamsChannelId;
            if (!string.IsNullOrEmpty(savedChannelId) && channels.Any(tc => tc.Id == savedChannelId))
            {
                return savedChannelId;
            }

            using var probeLimit = new SemaphoreSlim(MaxParallelProbes);
            var probes = channels.Select(async tc =>
            {
                await probeLimit.WaitAsync(cancellationToken);
                try
                {
                    var page = await _client.GetChannelPageAsync(tc.Id, null, cancellationToken);
                    return (ChannelId: tc.Id, Post: TeamsChat.NewestOwnTicketPost(page.Messages, myId));
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden || ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // One channel the user cannot read (a private one, say) must not fail the whole detection.
                    AppLogger.Info($"[teams] skipped a channel while detecting ({(int)ex.StatusCode.Value})");
                    return (ChannelId: tc.Id, Post: (TeamsMessage?)null);
                }
                finally
                {
                    probeLimit.Release();
                }
            }).ToList();
            var probeResults = await Task.WhenAll(probes);

            string? detectedChannelId = null;
            var newestSentAt = DateTimeOffset.MinValue;
            foreach (var probeResult in probeResults)
            {
                if (probeResult.Post != null && (detectedChannelId == null || probeResult.Post.SentAt > newestSentAt))
                {
                    detectedChannelId = probeResult.ChannelId;
                    newestSentAt = probeResult.Post.SentAt;
                }
            }

            if (detectedChannelId != null)
            {
                var settings = _loadSettings();
                settings.TeamsChannelId = detectedChannelId;
                settings.TeamsRecentChannelIds = WithRecentChannel(settings.TeamsRecentChannelIds, detectedChannelId);
                _saveSettings(settings);
            }

            return detectedChannelId;
        }
        finally
        {
            _channelDetectionLock.Release();
        }
    }

    private async Task<IReadOnlyList<TeamsChannel>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        await _channelListLock.WaitAsync(cancellationToken);
        try
        {
            var snapshot = Volatile.Read(ref _channelList);
            if (snapshot != null && _now() - snapshot.FetchedAt < ChannelCacheLifetime)
            {
                return snapshot.Channels;
            }

            var channels = await _client.ListChannelsAsync(cancellationToken);
            Volatile.Write(ref _channelList, new ChannelListSnapshot(channels, _now()));
            return channels;
        }
        finally
        {
            _channelListLock.Release();
        }
    }

    private static List<string> WithRecentChannel(IReadOnlyList<string> recentChannelIds, string channelId)
    {
        return new[] { channelId }
            .Concat(recentChannelIds.Where(rc => rc != channelId))
            .Take(MaxRecentChannels)
            .ToList();
    }

    // Moves the people seen in these messages to the front of the saved list; settings are written only when it changed.
    private void CollectPeople(IEnumerable<TeamsMessage> messages, string myId)
    {
        var seenPeople = TeamsCompose.People(messages, myId);
        if (seenPeople.Count == 0)
        {
            return;
        }

        lock (_peopleLock)
        {
            var settings = _loadSettings();
            var merged = seenPeople
                .Concat(settings.TeamsPeople.Where(tp => !seenPeople.Exists(sp => sp.Mri == tp.Mri)))
                .Take(MaxPeople)
                .ToList();
            if (!merged.SequenceEqual(settings.TeamsPeople))
            {
                settings.TeamsPeople = merged;
                _saveSettings(settings);
            }
        }
    }

    // The thread's own people first, they are the likeliest to be tagged; then everyone else saved.
    private IReadOnlyList<TeamsPerson> PeopleFor(TeamsThread? thread, string myId)
    {
        var threadPeople = thread == null ? new List<TeamsPerson>() : TeamsCompose.People(thread.Messages, myId);
        return threadPeople
            .Concat(_loadSettings().TeamsPeople.Where(tp => tp.Mri != myId && !threadPeople.Exists(thp => thp.Mri == tp.Mri)))
            .ToList();
    }

    private void CacheImage(string url, byte[] bytes, string contentType)
    {
        if (bytes.Length > MaxCachedImageBytes)
        {
            return;
        }

        lock (_imageCacheLock)
        {
            if (_imageCacheNodes.Remove(url, out var replacedNode))
            {
                _imageCacheOrder.Remove(replacedNode);
                _imageCacheBytes -= replacedNode.Value.Bytes.Length;
            }

            _imageCacheNodes[url] = _imageCacheOrder.AddFirst(new CachedImage(url, bytes, contentType));
            _imageCacheBytes += bytes.Length;
            while (_imageCacheBytes > MaxCachedImageBytes)
            {
                var oldestImage = _imageCacheOrder.Last!.Value;
                _imageCacheOrder.RemoveLast();
                _imageCacheNodes.Remove(oldestImage.Url);
                _imageCacheBytes -= oldestImage.Bytes.Length;
            }
        }
    }

    private static string ToDataUrl(byte[] bytes, string contentType)
    {
        return $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
    }

    // A caller's own cancel is not a failure: it passes through.
    private static bool IsExpectedFailure(Exception exception, CancellationToken callerToken)
    {
        if (exception is OperationCanceledException)
        {
            return !callerToken.IsCancellationRequested;
        }

        return exception is HttpRequestException || exception is InvalidOperationException || exception is JsonException;
    }

    private static string ShortError(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return "Teams did not answer in time";
        }

        return exception.Message;
    }

    private sealed record CachedAvatar(string? DataUrl, DateTimeOffset At);

    private sealed record CachedImage(string Url, byte[] Bytes, string ContentType);

    private sealed record ChannelListSnapshot(IReadOnlyList<TeamsChannel> Channels, DateTimeOffset FetchedAt);

    private sealed record ChannelPost(string ChannelId, TeamsIndexedPost Post);
}
