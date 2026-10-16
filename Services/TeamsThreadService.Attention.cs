using System.Net;

namespace NoisLogTray;

// The background read behind the reminders: each ticket's indexed post and group chats, without searching or syncing.
internal sealed partial class TeamsThreadService
{
    internal async Task<(string MyId, IReadOnlyList<TeamsConversation> Conversations)> ReadConversationsAsync(
        IReadOnlyList<string> ticketKeys, CancellationToken cancellationToken)
    {
        var myId = await _client.GetMyIdAsync(cancellationToken);
        var conversations = new List<TeamsConversation>();
        var failedCount = 0;
        foreach (var ticketKey in ticketKeys)
        {
            var indexedPost = _postIndex.FindPost(ticketKey.ToUpperInvariant());
            if (indexedPost != null)
            {
                try
                {
                    var postThread = await _client.GetThreadAsync(indexedPost.Value.ChannelId, indexedPost.Value.Post.RootId, cancellationToken);
                    if (postThread.Messages.Any(tm => tm.Id == indexedPost.Value.Post.RootId))
                    {
                        conversations.Add(new TeamsConversation(ticketKey, null, "", postThread));
                    }
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // The post is gone; a normal read drops it from the index.
                }
                catch (Exception ex) when (IsExpectedFailure(ex, cancellationToken))
                {
                    failedCount++;
                }
            }

            var ticketChats = await GetTicketChatsAsync(ticketKey, cancellationToken);
            foreach (var ticketChat in ticketChats ?? new List<TeamsGroupChat>())
            {
                try
                {
                    var chatThread = await _client.GetChatAsync(ticketChat.Id, cancellationToken);
                    conversations.Add(new TeamsConversation(ticketKey, ticketChat.Id, ticketChat.Topic, chatThread));
                }
                catch (Exception ex) when (IsExpectedFailure(ex, cancellationToken))
                {
                    failedCount++;
                }
            }
        }

        if (failedCount != 0)
        {
            AppLogger.Info($"[teams] attention: {failedCount} conversation(s) could not be read");
        }

        return (myId, conversations);
    }
}
