namespace NoisLogTray;

// One channel's part of the post index (teams-posts.json): its ticket posts and how far its pages have been read.
internal sealed record TeamsChannelIndex(
    DateTimeOffset? SyncedAt, // last finished sync of the newest pages
    bool DeepScanned, // the 40-page scan is complete
    int DeepScanPages, // pages read by the deep scan so far
    string? DeepScanLink, // backwardLink to resume the deep scan from
    IReadOnlyDictionary<string, TeamsIndexedPost> Posts); // upper-case ticket key -> its post in this channel
