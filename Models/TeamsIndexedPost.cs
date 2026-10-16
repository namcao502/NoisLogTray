namespace NoisLogTray;

// A ticket's post in one channel, as the post index keeps it.
internal sealed record TeamsIndexedPost(string RootId, DateTimeOffset SentAt, bool Mine);
