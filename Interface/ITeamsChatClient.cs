namespace NoisLogTray;

internal interface ITeamsChatClient
{
    Task<string> GetMyIdAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<TeamsChannel>> ListChannelsAsync(CancellationToken cancellationToken);

    Task<TeamsMessagePage> GetChannelPageAsync(string channelId, string? backwardLink, CancellationToken cancellationToken);

    Task<TeamsThread> GetThreadAsync(string channelId, string rootId, CancellationToken cancellationToken);

    // Returns the new reply's id; throws TeamsReplyUncertainException when the request may have reached Teams.
    Task<string> PostReplyAsync(string channelId, string rootId, TeamsMessageBody body, CancellationToken cancellationToken);

    // Returns the new post's id (it is also its rootMessageId); same never-twice rules as PostReplyAsync.
    Task<string> PostMessageAsync(string channelId, string subject, TeamsMessageBody body, CancellationToken cancellationToken);

    // Replaces a message's content (and a root post's subject); conversationId is the channel for a root, "channel;messageid=root" for a reply.
    // Throws TeamsReplyUncertainException when the request may have reached Teams.
    Task EditMessageAsync(string conversationId, string messageId, string? subject, TeamsMessageBody body, CancellationToken cancellationToken);

    // Soft-deletes a message; same conversationId and uncertainty rules as EditMessageAsync.
    Task DeleteMessageAsync(string conversationId, string messageId, CancellationToken cancellationToken);

    // Uploads one image to Teams' object store for this channel and returns the AMS objectId.
    Task<string> UploadImageAsync(string channelId, TeamsImage image, CancellationToken cancellationToken);

    // Downloads an AMS image with the chat token; refuses any other URL, a non-image, or more than MaxImageBytes.
    Task<(byte[] Bytes, string ContentType)> GetImageAsync(string url, CancellationToken cancellationToken);

    // Downloads a person's Teams profile picture with the spaces token. Null when there is no usable picture token or Teams refused;
    // never invalidates the session. Throws ArgumentException for an mri that is not a person.
    Task<(byte[] Bytes, string ContentType)?> GetProfilePictureAsync(string mri, string displayName, CancellationToken cancellationToken);

    // The group chats among the newest conversations (at most MaxChatListPages pages), in the order Teams lists them.
    Task<IReadOnlyList<TeamsGroupChat>> ListChatsAsync(CancellationToken cancellationToken);

    // The newest page of a group chat's messages as a thread (ChannelId = chat id, no root).
    Task<TeamsThread> GetChatAsync(string chatId, CancellationToken cancellationToken);

    // Returns the new message's id; same never-twice rules as PostReplyAsync.
    Task<string> PostChatMessageAsync(string chatId, TeamsMessageBody body, CancellationToken cancellationToken);

    void InvalidateSession();
}
