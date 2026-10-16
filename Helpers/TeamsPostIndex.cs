using System.Text.Json;
using System.Text.RegularExpressions;

namespace NoisLogTray;

// teams-posts.json: each channel's ticket posts and how far its pages were read. Own file: written per page read, it would race settings saves.
internal sealed class TeamsPostIndex
{
    internal const int DeepScanPageLimit = 40;

    private static readonly TimeSpan PostLifetime = TimeSpan.FromDays(200);
    private static readonly Regex TicketSubjectKey = new(@"^\s*([A-Za-z][A-Za-z0-9]+-\d+)(?!\d)", RegexOptions.Compiled);
    private static readonly Dictionary<string, object> GatesByPath = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly object _gate;

    internal TeamsPostIndex(string path)
    {
        _path = path;
        // One lock per file for the whole process, so two instances on one path never lose each other's writes.
        var fullPath = Path.GetFullPath(path);
        lock (GatesByPath)
        {
            if (!GatesByPath.TryGetValue(fullPath, out var gate))
            {
                gate = new object();
                GatesByPath[fullPath] = gate;
            }

            _gate = gate;
        }
    }

    internal TeamsChannelIndex Get(string channelId)
    {
        lock (_gate)
        {
            return ReadUnlocked().GetValueOrDefault(channelId) ?? EmptyIndex();
        }
    }

    // The ticket's post across every channel in the file: an own post first, then the newest.
    internal (string ChannelId, TeamsIndexedPost Post)? FindPost(string postKey)
    {
        lock (_gate)
        {
            (string ChannelId, TeamsIndexedPost Post)? best = null;
            foreach (var (channelId, channelIndex) in ReadUnlocked())
            {
                if (!channelIndex.Posts.TryGetValue(postKey, out var post))
                {
                    continue;
                }

                var winsOverBest = best == null
                    || (post.Mine && !best.Value.Post.Mine)
                    || (post.Mine == best.Value.Post.Mine && post.SentAt > best.Value.Post.SentAt);
                if (winsOverBest)
                {
                    best = (channelId, post);
                }
            }

            return best;
        }
    }

    // Read, change and write in one lock; a failed write is logged and dropped, as Teams can always be read again.
    internal void Update(string channelId, Func<TeamsChannelIndex, TeamsChannelIndex> change)
    {
        lock (_gate)
        {
            var channelIndexes = ReadUnlocked();
            var changedIndexes = new Dictionary<string, TeamsChannelIndex>(channelIndexes)
            {
                [channelId] = change(channelIndexes.GetValueOrDefault(channelId) ?? EmptyIndex()),
            };
            var oldestKeptSentAt = DateTimeOffset.UtcNow - PostLifetime;
            var prunedIndexes = new Dictionary<string, TeamsChannelIndex>();
            foreach (var (changedChannelId, changedIndex) in changedIndexes)
            {
                prunedIndexes[changedChannelId] = changedIndex with
                {
                    Posts = changedIndex.Posts.Where(kvp => kvp.Value.SentAt >= oldestKeptSentAt).ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                };
            }

            var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(prunedIndexes, Options));
                File.Move(temporaryPath, _path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                AppLogger.Error($"[teams] Writing the post index failed: {exception.Message}");
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception deleteError) when (deleteError is IOException || deleteError is UnauthorizedAccessException)
                {
                    AppLogger.Error($"[teams] Could not delete {temporaryPath}: {deleteError.Message}");
                }
            }
        }
    }

    // A subject starting with a ticket key is that ticket's post; an own post beats another's, then the newer one wins.
    internal static TeamsChannelIndex WithPosts(TeamsChannelIndex index, IEnumerable<TeamsMessage> messages, string myId)
    {
        var posts = new Dictionary<string, TeamsIndexedPost>(index.Posts);
        foreach (var message in messages)
        {
            if (message.Subject == null)
            {
                continue;
            }

            var keyMatch = TicketSubjectKey.Match(message.Subject);
            if (!keyMatch.Success)
            {
                continue;
            }

            var ticketKey = keyMatch.Groups[1].Value.ToUpperInvariant();
            var post = new TeamsIndexedPost(message.Id, message.SentAt, string.Equals(message.SenderId, myId, StringComparison.Ordinal));
            if (posts.TryGetValue(ticketKey, out var indexedPost))
            {
                var winsOverIndexed = (post.Mine && !indexedPost.Mine) || (post.Mine == indexedPost.Mine && post.SentAt > indexedPost.SentAt);
                if (!winsOverIndexed)
                {
                    continue;
                }
            }

            posts[ticketKey] = post;
        }

        return index with { Posts = posts };
    }

    internal static TeamsChannelIndex WithoutPost(TeamsChannelIndex index, string ticketKey)
    {
        var postKey = ticketKey.ToUpperInvariant();
        return index with
        {
            Posts = index.Posts.Where(kvp => kvp.Key != postKey).ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        };
    }

    private static TeamsChannelIndex EmptyIndex()
    {
        return new TeamsChannelIndex(null, false, 0, null, new Dictionary<string, TeamsIndexedPost>());
    }

    // Missing or unreadable reads as empty; a corrupt file is kept aside as .bad.
    private Dictionary<string, TeamsChannelIndex> ReadUnlocked()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new Dictionary<string, TeamsChannelIndex>();
            }

            var parsed = JsonSerializer.Deserialize<Dictionary<string, TeamsChannelIndex?>>(File.ReadAllText(_path), Options)
                ?? new Dictionary<string, TeamsChannelIndex?>();
            var channelIndexes = new Dictionary<string, TeamsChannelIndex>();
            foreach (var (channelId, channelIndex) in parsed)
            {
                if (channelIndex == null)
                {
                    continue;
                }

                // A hand-edited file may leave posts out; the deserializer then passes null despite the type.
                channelIndexes[channelId] = channelIndex.Posts == null
                    ? channelIndex with { Posts = new Dictionary<string, TeamsIndexedPost>() }
                    : channelIndex;
            }

            return channelIndexes;
        }
        catch (JsonException exception)
        {
            AppLogger.Error($"[teams] Post index corrupt, keeping a .bad copy: {exception.Message}");
            try
            {
                File.Move(_path, _path + ".bad", overwrite: true);
            }
            catch (Exception moveError) when (moveError is IOException || moveError is UnauthorizedAccessException)
            {
                AppLogger.Error($"[teams] Could not keep the post index's .bad copy: {moveError.Message}");
            }

            return new Dictionary<string, TeamsChannelIndex>();
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            AppLogger.Error($"[teams] Reading the post index failed: {exception.Message}");
            return new Dictionary<string, TeamsChannelIndex>();
        }
    }
}
