namespace NoisLogTray;

// Oldest first, root first. A group chat has its chat id in ChannelId and an empty RootId.
internal sealed record TeamsThread(string ChannelId, string RootId, IReadOnlyList<TeamsMessage> Messages, bool IsChat = false);
