namespace NoisLogTray;

// The ticket's Teams group chats: the cached chat list, reading a chat and re-reading it after a change.
internal sealed partial class TeamsThreadService
{
    private static readonly TimeSpan ChatListLifetime = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _chatListLock = new(1, 1);
    private ChatListSnapshot? _chatList;
    private bool _chatListFailing; // logged once per run of failures, as the 10s refresh would repeat it

    private sealed record ChatListSnapshot(IReadOnlyList<TeamsGroupChat> Chats, DateTimeOffset FetchedAt);

    // The ticket's chats; null only when the list was never read. A failed refresh keeps the old list and its time.
    private async Task<IReadOnlyList<TeamsGroupChat>?> GetTicketChatsAsync(string ticketKey, CancellationToken cancellationToken)
    {
        await _chatListLock.WaitAsync(cancellationToken);
        try
        {
            var snapshot = _chatList;
            if (snapshot == null || _now() - snapshot.FetchedAt >= ChatListLifetime)
            {
                try
                {
                    var chats = await _client.ListChatsAsync(cancellationToken);
                    snapshot = new ChatListSnapshot(chats, _now());
                    _chatList = snapshot;
                    _chatListFailing = false;
                }
                catch (Exception ex) when (IsExpectedFailure(ex, cancellationToken))
                {
                    if (!_chatListFailing)
                    {
                        AppLogger.Info($"[teams] {ticketKey}: chat list failed ({ex.GetType().Name})");
                    }
                    _chatListFailing = true;
                }
            }

            return snapshot == null ? null : TeamsChat.FindTicketChats(snapshot.Chats, ticketKey);
        }
        finally
        {
            _chatListLock.Release();
        }
    }

    private async Task<TeamsThreadResult> ReadChatCoreAsync(string ticketKey, string chatId, CancellationToken cancellationToken)
    {
        var myId = await _client.GetMyIdAsync(cancellationToken);
        var ticketChats = await GetTicketChatsAsync(ticketKey, cancellationToken);
        if (ticketChats == null || !ticketChats.Any(tgc => tgc.Id == chatId))
        {
            return new TeamsThreadResult(TeamsThreadState.Failed, null, "", Array.Empty<TeamsChannel>(), null,
                Lang.T("This chat is not one of the ticket's chats.", "Chat này không thuộc ticket."), Chats: ticketChats);
        }

        var channels = await GetChannelsAsync(cancellationToken);
        var chatThread = await _client.GetChatAsync(chatId, cancellationToken);
        CollectPeople(chatThread.Messages, myId);
        return new TeamsThreadResult(TeamsThreadState.Found, chatThread, myId, channels, chatId, null, Chats: ticketChats);
    }

    // Null when the re-read fails: the change is already in Teams, so the view reads again on its own.
    private async Task<TeamsThreadResult?> RereadChatAsync(string ticketKey, TeamsThread thread, CancellationToken cancellationToken)
    {
        try
        {
            var myId = await _client.GetMyIdAsync(cancellationToken);
            var channels = await GetChannelsAsync(cancellationToken);
            var ticketChats = await GetTicketChatsAsync(ticketKey, cancellationToken);
            var rereadThread = await _client.GetChatAsync(thread.ChannelId, cancellationToken);
            CollectPeople(rereadThread.Messages, myId);
            return new TeamsThreadResult(TeamsThreadState.Found, rereadThread, myId, channels, thread.ChannelId, null,
                _loadSettings().TeamsRecentChannelIds, PeopleFor(rereadThread, myId), Chats: ticketChats);
        }
        catch (Exception ex) when (ex is TeamsSignedOutException || IsExpectedFailure(ex, cancellationToken))
        {
            return null;
        }
    }

    // Saves the images as <index>.<ext> in a fresh directory for Claude to read; an image that fails to load is left out.
    internal async Task<IReadOnlyList<TeamsScreenshot>> SaveImagesAsync(IReadOnlyList<TeamsScreenshot> screenshots, string directory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        foreach (var staleFile in Directory.GetFiles(directory))
        {
            File.Delete(staleFile);
        }

        var saved = new List<TeamsScreenshot>();
        foreach (var screenshot in screenshots)
        {
            var dataUrl = await GetImageDataUrlAsync(screenshot.Url, cancellationToken);
            var separatorIndex = dataUrl == null ? -1 : dataUrl.IndexOf(";base64,", StringComparison.Ordinal);
            if (dataUrl == null || separatorIndex == -1)
            {
                continue;
            }

            var extension = dataUrl["data:image/".Length..separatorIndex];
            if (extension.Length == 0 || !extension.All(char.IsAsciiLetterOrDigit))
            {
                continue;
            }

            var path = Path.Combine(directory, $"{saved.Count}.{(extension == "jpeg" ? "jpg" : extension)}");
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(dataUrl[(separatorIndex + ";base64,".Length)..]), cancellationToken);
            saved.Add(screenshot with { Path = path });
        }

        return saved;
    }
}
