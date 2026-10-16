namespace NoisLogTray;

internal sealed record TeamsThreadResult(
    TeamsThreadState State,
    TeamsThread? Thread,
    string MyId,
    IReadOnlyList<TeamsChannel> Channels,
    string? ChannelId,
    string? Error,
    IReadOnlyList<string>? RecentChannelIds = null, // most recent first; given with the channels, for the pickers' "Recent" group
    IReadOnlyList<TeamsPerson>? People = null, // thread people first, then the saved ones, never the user; given with the channels
    bool StillSearching = false, // NotPosted while a searched channel's older pages are not all read yet; the view reads again soon
    IReadOnlyList<TeamsGroupChat>? Chats = null); // the ticket's group chats; null when the chat list could not be read, empty when it has none
