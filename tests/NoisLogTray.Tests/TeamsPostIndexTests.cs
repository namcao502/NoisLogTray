namespace NoisLogTray.Tests;

public class TeamsPostIndexTests : IDisposable
{
    private const string MyId = "me";
    private readonly TempDirectory _directory = new();
    private readonly string _filePath;

    public TeamsPostIndexTests()
    {
        _filePath = Path.Combine(_directory.Path, "teams-posts.json");
    }

    public void Dispose()
    {
        _directory.Dispose();
    }

    private static TeamsChannelIndex EmptyIndex()
    {
        return new TeamsChannelIndex(null, false, 0, null, new Dictionary<string, TeamsIndexedPost>());
    }

    private static TeamsMessage Message(string id, string senderId, DateTimeOffset sentAt, string? subject)
    {
        return new TeamsMessage(id, senderId, "name", sentAt, "body", true, subject);
    }

    [Fact]
    public void WithPosts_PrefersOwnPostThenNewest_AndIgnoresNonTicketSubjects()
    {
        var sentAt = DateTimeOffset.UtcNow;
        var messages = new[]
        {
            Message("other-newest", "someone", sentAt.AddMinutes(30), "MDP-1 newest, by someone else"),
            Message("own-old", MyId, sentAt, "MDP-1 old"),
            Message("own-new", MyId, sentAt.AddMinutes(10), "  mdp-1: newer, lower case"),
            Message("other-old", "someone", sentAt.AddMinutes(-5), "MDP-2 older"),
            Message("other-new", "someone", sentAt, "MDP-2 newer"),
            Message("not-at-start", MyId, sentAt, "Re MDP-3"),
            Message("not-a-ticket", MyId, sentAt, "Lunch plans"),
            Message("no-subject", MyId, sentAt, null),
        };

        var index = TeamsPostIndex.WithPosts(EmptyIndex(), messages, MyId);
        var someoneLater = TeamsPostIndex.WithPosts(index, new[] { Message("later", "someone", sentAt.AddHours(1), "MDP-1 later") }, MyId);

        Assert.Equal(new[] { "MDP-1", "MDP-2" }, index.Posts.Keys.OrderBy(k => k));
        Assert.Equal(new TeamsIndexedPost("own-new", sentAt.AddMinutes(10), true), index.Posts["MDP-1"]);
        Assert.Equal(new TeamsIndexedPost("other-new", sentAt, false), index.Posts["MDP-2"]);
        Assert.Equal("own-new", someoneLater.Posts["MDP-1"].RootId);
    }

    [Fact]
    public void WithoutPost_RemovesOnlyThatKey()
    {
        var sentAt = DateTimeOffset.UtcNow;
        var index = TeamsPostIndex.WithPosts(EmptyIndex(), new[] { Message("a", MyId, sentAt, "MDP-1 a"), Message("b", MyId, sentAt, "MDP-2 b") }, MyId);

        var without = TeamsPostIndex.WithoutPost(index, "mdp-1");

        Assert.Equal(new[] { "MDP-2" }, without.Posts.Keys);
        Assert.Equal(2, index.Posts.Count);
    }

    [Fact]
    public void Get_ReturnsEmpty_WhenFileMissing_AndKeepsBadFileAsBad()
    {
        var postIndex = new TeamsPostIndex(_filePath);

        var missing = postIndex.Get("c1");
        File.WriteAllText(_filePath, "{ not json");
        var corrupt = postIndex.Get("c1");

        Assert.Null(missing.SyncedAt);
        Assert.False(missing.DeepScanned);
        Assert.Empty(missing.Posts);
        Assert.Empty(corrupt.Posts);
        Assert.False(File.Exists(_filePath));
        Assert.Equal("{ not json", File.ReadAllText(_filePath + ".bad"));
    }

    [Fact]
    public async Task Update_FromTwoThreads_KeepsBothChannels()
    {
        var firstIndex = new TeamsPostIndex(_filePath);
        var secondIndex = new TeamsPostIndex(_filePath);
        var sentAt = DateTimeOffset.UtcNow;

        var firstWriter = Task.Run(() =>
        {
            for (var postNumber = 0; postNumber < 25; postNumber++)
            {
                var message = Message("a" + postNumber, MyId, sentAt, "MDP-" + postNumber + " a");
                firstIndex.Update("c1", tci => TeamsPostIndex.WithPosts(tci, new[] { message }, MyId));
            }
        });
        var secondWriter = Task.Run(() =>
        {
            for (var postNumber = 0; postNumber < 25; postNumber++)
            {
                var message = Message("b" + postNumber, MyId, sentAt, "MDP-" + postNumber + " b");
                secondIndex.Update("c2", tci => TeamsPostIndex.WithPosts(tci, new[] { message }, MyId));
            }
        });
        await Task.WhenAll(firstWriter, secondWriter);

        Assert.Equal(25, firstIndex.Get("c1").Posts.Count);
        Assert.Equal(25, firstIndex.Get("c2").Posts.Count);
    }

    [Fact]
    public void Update_PrunesPostsOlderThan200Days()
    {
        var postIndex = new TeamsPostIndex(_filePath);
        var now = DateTimeOffset.UtcNow;
        var messages = new[] { Message("old", MyId, now.AddDays(-201), "MDP-1 old"), Message("recent", MyId, now.AddDays(-199), "MDP-2 recent") };

        postIndex.Update("c1", tci => TeamsPostIndex.WithPosts(tci, messages, MyId) with
        {
            SyncedAt = now,
            DeepScanPages = 3,
            DeepScanLink = "link4",
        });
        var read = postIndex.Get("c1");

        Assert.Equal(new[] { "MDP-2" }, read.Posts.Keys);
        Assert.Equal(now, read.SyncedAt);
        Assert.Equal(3, read.DeepScanPages);
        Assert.Equal("link4", read.DeepScanLink);
        Assert.False(read.DeepScanned);
    }

    [Fact]
    public void FindPost_PrefersOwnPost_ThenNewest()
    {
        var sentAt = DateTimeOffset.UtcNow.AddDays(-1);
        var index = new TeamsPostIndex(_filePath);
        index.Update("c1", tci => TeamsPostIndex.WithPosts(tci, new[] { Message("other-newest", "someone", sentAt.AddHours(2), "MDP-1 a") }, MyId));
        index.Update("c2", tci => TeamsPostIndex.WithPosts(tci, new[] { Message("own-old", MyId, sentAt, "MDP-1 b") }, MyId));
        index.Update("c3", tci => TeamsPostIndex.WithPosts(tci, new[] { Message("own-new", MyId, sentAt.AddHours(1), "MDP-1 c") }, MyId));
        index.Update("c4", tci => TeamsPostIndex.WithPosts(tci, new[] { Message("x", MyId, sentAt, "MDP-2 d") }, MyId));

        var found = index.FindPost("MDP-1");

        Assert.NotNull(found);
        Assert.Equal("c3", found.Value.ChannelId);
        Assert.Equal("own-new", found.Value.Post.RootId);
        Assert.Null(index.FindPost("MDP-9"));
    }
}
