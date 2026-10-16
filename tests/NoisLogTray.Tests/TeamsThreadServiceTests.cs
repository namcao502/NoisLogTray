using System.Net;
using Xunit;

namespace NoisLogTray.Tests;

public class TeamsThreadServiceTests : IDisposable
{
    private const string Me = "me";
    // Today, not a fixed date: the post index drops posts older than 200 days by the wall clock.
    private static readonly DateTimeOffset Noon = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddHours(12);
    private readonly TempDirectory _directory = new();

    public void Dispose()
    {
        _directory.Dispose();
    }

    private sealed class FakeTeamsChatClient : ITeamsChatClient
    {
        internal List<TeamsChannel> Channels { get; } = new();
        internal Dictionary<(string ChannelId, string? Link), TeamsMessagePage> Pages { get; } = new();
        internal Dictionary<(string ChannelId, string RootId), TeamsThread> Threads { get; } = new();
        internal List<(string ChannelId, string? Link)> PageCalls { get; } = new();
        internal List<(string ChannelId, string RootId)> ThreadCalls { get; } = new();
        internal List<(string ChannelId, string RootId, string Html)> Replies { get; } = new();
        internal int ListCalls { get; set; }
        internal Exception? ListException { get; set; }
        internal Exception? ReplyException { get; set; }
        internal List<(string ChannelId, string Subject, string Html)> Posts { get; } = new();
        internal Exception? PostException { get; set; }
        internal bool ThreadMissingAfterReply { get; set; }
        internal HashSet<string> ForbiddenChannelIds { get; } = new();
        internal List<TeamsMessageBody> ReplyBodies { get; } = new();
        internal List<TeamsMessageBody> PostBodies { get; } = new();
        internal List<string> Uploads { get; } = new();
        internal Func<TeamsImage, Exception?>? UploadFailure { get; set; }
        internal List<string> ImageCalls { get; } = new();
        internal Exception? ImageException { get; set; }
        internal int ImageBytesLength { get; set; } = 4;
        internal Exception? ChangeException { get; set; }
        internal List<string> ProfileCalls { get; } = new();
        internal TaskCompletionSource? ProfileGate { get; set; }
        internal (byte[] Bytes, string ContentType)? ProfileResult { get; set; } = (new byte[] { 1, 2, 3 }, "image/jpeg");
        internal List<(string ConversationId, string MessageId, string? Subject, TeamsMessageBody Body)> Edits { get; } = new();
        internal List<(string ConversationId, string MessageId)> Deletes { get; } = new();

        public Task<string> GetMyIdAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(Me);
        }

        public Task<IReadOnlyList<TeamsChannel>> ListChannelsAsync(CancellationToken cancellationToken)
        {
            ListCalls++;
            if (ListException != null)
            {
                throw ListException;
            }

            return Task.FromResult<IReadOnlyList<TeamsChannel>>(Channels.ToList());
        }

        public Task<TeamsMessagePage> GetChannelPageAsync(string channelId, string? backwardLink, CancellationToken cancellationToken)
        {
            lock (PageCalls)
            {
                PageCalls.Add((channelId, backwardLink));
            }

            if (ForbiddenChannelIds.Contains(channelId))
            {
                throw new HttpRequestException("forbidden", null, System.Net.HttpStatusCode.Forbidden);
            }

            return Task.FromResult(Pages.TryGetValue((channelId, backwardLink), out var page) ? page : new TeamsMessagePage(new List<TeamsMessage>(), null));
        }

        public Task<TeamsThread> GetThreadAsync(string channelId, string rootId, CancellationToken cancellationToken)
        {
            ThreadCalls.Add((channelId, rootId));
            if (Replies.Count != 0 && ThreadMissingAfterReply)
            {
                throw new HttpRequestException("boom");
            }

            if (Threads.TryGetValue((channelId, rootId), out var thread))
            {
                return Task.FromResult(thread);
            }

            throw new HttpRequestException("gone", null, HttpStatusCode.NotFound);
        }

        internal List<TeamsGroupChat> GroupChats { get; } = new();
        internal int ChatListCalls { get; set; }
        internal Exception? ChatListException { get; set; }
        internal Dictionary<string, TeamsThread> ChatThreads { get; } = new();
        internal List<(string ChatId, string Html)> ChatPosts { get; } = new();

        public Task<IReadOnlyList<TeamsGroupChat>> ListChatsAsync(CancellationToken cancellationToken)
        {
            ChatListCalls++;
            if (ChatListException != null)
            {
                throw ChatListException;
            }

            return Task.FromResult<IReadOnlyList<TeamsGroupChat>>(GroupChats.ToList());
        }

        public Task<TeamsThread> GetChatAsync(string chatId, CancellationToken cancellationToken)
        {
            if (!ChatThreads.TryGetValue(chatId, out var chatThread))
            {
                throw new HttpRequestException("boom");
            }

            return Task.FromResult(chatThread);
        }

        public Task<string> PostChatMessageAsync(string chatId, TeamsMessageBody body, CancellationToken cancellationToken)
        {
            ChatPosts.Add((chatId, body.Html));
            return Task.FromResult("chat-msg-1");
        }

        public Task<string> PostReplyAsync(string channelId, string rootId, TeamsMessageBody body, CancellationToken cancellationToken)
        {
            if (ReplyException != null)
            {
                throw ReplyException;
            }

            Replies.Add((channelId, rootId, body.Html));
            ReplyBodies.Add(body);
            return Task.FromResult("reply-1");
        }

        public Task<string> PostMessageAsync(string channelId, string subject, TeamsMessageBody body, CancellationToken cancellationToken)
        {
            if (PostException != null)
            {
                throw PostException;
            }

            Posts.Add((channelId, subject, body.Html));
            PostBodies.Add(body);
            Threads[(channelId, "post-1")] = ThreadOf(channelId, "post-1");
            return Task.FromResult("post-1");
        }

        public Task<string> UploadImageAsync(string channelId, TeamsImage image, CancellationToken cancellationToken)
        {
            var failure = UploadFailure?.Invoke(image);
            if (failure != null)
            {
                throw failure;
            }

            Uploads.Add(image.FileName);
            return Task.FromResult("obj-" + image.FileName);
        }

        public Task<(byte[] Bytes, string ContentType)> GetImageAsync(string url, CancellationToken cancellationToken)
        {
            ImageCalls.Add(url);
            if (ImageException != null)
            {
                throw ImageException;
            }

            return Task.FromResult((new byte[ImageBytesLength], "image/png"));
        }

        public async Task<(byte[] Bytes, string ContentType)?> GetProfilePictureAsync(string mri, string displayName, CancellationToken cancellationToken)
        {
            lock (ProfileCalls)
            {
                ProfileCalls.Add(mri);
            }

            if (ProfileGate != null)
            {
                await ProfileGate.Task;
            }

            return ProfileResult;
        }

        public Task EditMessageAsync(string conversationId, string messageId, string? subject, TeamsMessageBody body, CancellationToken cancellationToken)
        {
            if (ChangeException != null)
            {
                throw ChangeException;
            }

            Edits.Add((conversationId, messageId, subject, body));
            return Task.CompletedTask;
        }

        public Task DeleteMessageAsync(string conversationId, string messageId, CancellationToken cancellationToken)
        {
            if (ChangeException != null)
            {
                throw ChangeException;
            }

            Deletes.Add((conversationId, messageId));
            return Task.CompletedTask;
        }

        public void InvalidateSession()
        {
        }
    }

    private sealed class Rig
    {
        private readonly string _postIndexPath;

        internal Rig(string directory)
        {
            _postIndexPath = Path.Combine(directory, "teams-posts.json");
            AvatarsDirectory = Path.Combine(directory, "avatars");
        }

        internal string AvatarsDirectory { get; }

        internal FakeTeamsChatClient Client { get; } = new();
        internal AppSettings Settings { get; set; } = new();
        internal DateTimeOffset Now { get; set; } = Noon;
        internal int SaveCount { get; private set; }

        internal TeamsPostIndex PostIndex
        {
            get { return new TeamsPostIndex(_postIndexPath); }
        }

        // An own post already in the channel's index, with the channel fully scanned, so a read needs no page.
        internal void IndexOwnPost(string channelId, string ticketKey, string rootId)
        {
            PostIndex.Update(channelId, tci => TeamsPostIndex.WithPosts(tci with { SyncedAt = Now, DeepScanned = true },
                new[] { Post(rootId, ticketKey + " subject") }, Me));
        }

        internal TeamsThreadService Service()
        {
            return new TeamsThreadService(
                Client,
                PostIndex,
                () => JsonRoundTrip(Settings),
                saved =>
                {
                    SaveCount++;
                    Settings = JsonRoundTrip(saved);
                },
                () => Now,
                AvatarsDirectory);
        }

        private static AppSettings JsonRoundTrip(AppSettings settings)
        {
            var path = Path.Combine(Path.GetTempPath(), "teams-thread-" + Guid.NewGuid() + ".json");
            try
            {
                AppSettings.Save(settings, path);
                return AppSettings.Load(path);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    private static TeamsMessage Post(string id, string subject, string senderId = Me, int minutes = 0)
    {
        return new TeamsMessage(id, senderId, "name", Noon.AddMinutes(minutes), "body", true, subject);
    }

    private static TeamsThread ThreadOf(string channelId, string rootId)
    {
        return new TeamsThread(channelId, rootId, new List<TeamsMessage> { Post(rootId, "MDP-1 subject") });
    }

    private static TeamsMessagePage PageOf(string? backwardLink, params TeamsMessage[] messages)
    {
        return new TeamsMessagePage(messages.ToList(), backwardLink);
    }

    private Rig RigWithChannels(params string[] channelIds)
    {
        var rig = new Rig(_directory.Path);
        foreach (var channelId in channelIds)
        {
            rig.Client.Channels.Add(new TeamsChannel(channelId, "team", channelId));
        }

        return rig;
    }

    private static TeamsDraft Draft(string text, params TeamsImage[] images)
    {
        return new TeamsDraft(TeamsCompose.ParagraphsFromText(text), Array.Empty<TeamsMention>(), images);
    }

    private static TeamsImage Png(string fileName, int length = 16)
    {
        var bytes = new byte[length];
        bytes[0] = 0x89;
        bytes[1] = 0x50;
        bytes[2] = 0x4E;
        bytes[3] = 0x47;
        return new TeamsImage(bytes, fileName, 40, 30);
    }

    private static string Mri(int number)
    {
        return "8:orgid:00000000-0000-0000-0000-" + number.ToString("D12");
    }

    private static TeamsMessage Said(int personNumber, string name, int minutes)
    {
        return new TeamsMessage("m" + personNumber + "-" + minutes, Mri(personNumber), name, Noon.AddMinutes(minutes), "x", true, null);
    }

    private static TeamsThread ThreadWith(string channelId, string rootId, params TeamsMessage[] messages)
    {
        return new TeamsThread(channelId, rootId, new List<TeamsMessage> { Post(rootId, "MDP-1 subject") }.Concat(messages).ToList());
    }

    private const string AmsUrl = "https://eu-api.asm.skype.com/v1/objects/0-weu-d1-abc/views/imgo";

    [Fact]
    public async Task ReadAsync_UsesIndexedOwnPost_WithoutSearching()
    {
        var rig = RigWithChannels("c1");
        rig.Settings.TeamsChannelId = "c1";
        rig.IndexOwnPost("c1", "MDP-1", "r1");
        rig.Client.Threads[("c1", "r1")] = ThreadOf("c1", "r1");

        var result = await rig.Service().ReadAsync("mdp-1", null, CancellationToken.None);

        Assert.Equal(TeamsThreadState.Found, result.State);
        Assert.Equal("r1", result.Thread!.RootId);
        Assert.Equal("c1", result.ChannelId);
        Assert.Empty(rig.Client.PageCalls);
    }

    [Fact]
    public async Task ReadAsync_DropsIndexedPost_WhenThreadIsGone_ThenFindsTheNewOneOnTheNextRead()
    {
        var rig = RigWithChannels("c1");
        rig.Settings.TeamsChannelId = "c1";
        rig.PostIndex.Update("c1", tci => TeamsPostIndex.WithPosts(tci, new[] { Post("old", "MDP-1 subject") }, Me));
        rig.Client.Pages[("c1", null)] = PageOf(null, Post("new", "MDP-1 subject"));
        rig.Client.Threads[("c1", "new")] = ThreadOf("c1", "new");
        var service = rig.Service();

        var first = await service.ReadAsync("MDP-1", null, CancellationToken.None);
        var droppedIndex = rig.PostIndex.Get("c1");
        var second = await service.ReadAsync("MDP-1", null, CancellationToken.None);

        Assert.Equal(TeamsThreadState.NotPosted, first.State);
        Assert.True(first.StillSearching);
        Assert.Empty(droppedIndex.Posts);
        Assert.Equal(TeamsThreadState.Found, second.State);
        Assert.Equal("new", rig.PostIndex.Get("c1").Posts["MDP-1"].RootId);
    }

    [Fact]
    public async Task ReadAsync_IgnoresIndexedPost_InChannelNotRecent()
    {
        var rig = RigWithChannels("c1", "c2");
        rig.Settings.TeamsChannelId = "c2";
        rig.Settings.TeamsRecentChannelIds = new List<string> { "c2" };
        rig.IndexOwnPost("c1", "MDP-1", "r1");
        rig.Client.Threads[("c1", "r1")] = ThreadOf("c1", "r1");

        var result = await rig.Service().ReadAsync("MDP-1", null, CancellationToken.None);

        Assert.Equal(TeamsThreadState.NotPosted, result.State);
        Assert.DoesNotContain(("c1", "r1"), rig.Client.ThreadCalls);
        Assert.Equal("r1", rig.PostIndex.Get("c1").Posts["MDP-1"].RootId);
    }

    [Fact]
    public async Task ReadAsync_FindsPostInRecentChannel_WhenSelectedChannelHasNone()
    {
        var rig = RigWithChannels("c1", "c2");
        rig.Settings.TeamsChannelId = "c1";
        rig.Settings.TeamsRecentChannelIds = new List<string> { "c1", "c2" };
        rig.Client.Pages[("c1", null)] = PageOf(null, Post("a", "MDP-5 x"));
        rig.Client.Pages[("c2", null)] = PageOf(null, Post("r2", "MDP-1 subject", senderId: "someone"));
        rig.Client.Threads[("c2", "r2")] = ThreadOf("c2", "r2");

        var result = await rig.Service().ReadAsync("MDP-1", null, CancellationToken.None);

        Assert.Equal(TeamsThreadState.Found, result.State);
        Assert.Equal("c2", result.ChannelId);
        Assert.Equal("r2", result.Thread!.RootId);
        Assert.Equal("c1", rig.Settings.TeamsChannelId);
    }

    [Fact]
    public async Task ReadAsync_PrefersOwnPostAcrossChannels_ThenSelectedChannel()
    {
        var rig = RigWithChannels("c1", "c2", "c3");
        rig.Settings.TeamsChannelId = "c1";
        rig.Settings.TeamsRecentChannelIds = new List<string> { "c1", "c2", "c3" };
        rig.Client.Pages[("c1", null)] = PageOf(null,
            Post("someone-1", "MDP-1 x", senderId: "someone", minutes: 50), Post("someone-2", "MDP-2 x", senderId: "someone", minutes: 1));
        rig.Client.Pages[("c2", null)] = PageOf(null, Post("mine-1", "MDP-1 mine"));
        rig.Client.Pages[("c3", null)] = PageOf(null, Post("someone-newer-2", "MDP-2 y", senderId: "someone", minutes: 40));
        foreach (var (channelId, rootId) in new[] { ("c1", "someone-1"), ("c1", "someone-2"), ("c2", "mine-1"), ("c3", "someone-newer-2") })
        {
            rig.Client.Threads[(channelId, rootId)] = ThreadOf(channelId, rootId);
        }
        var service = rig.Service();

        var own = await service.ReadAsync("MDP-1", null, CancellationToken.None);
        var selected = await service.ReadAsync("MDP-2", null, CancellationToken.None);

        Assert.Equal(("c2", "mine-1"), (own.ChannelId, own.Thread!.RootId));
        Assert.Equal(("c1", "someone-2"), (selected.ChannelId, selected.Thread!.RootId));
    }

    [Fact]
    public async Task DeepScan_ReadsAtMost15PagesPerRead_AndResumesFromCursor()
    {
        var rig = RigWithChannels("c1");
        rig.Settings.TeamsChannelId = "c1";
        rig.Client.Pages[("c1", null)] = PageOf("link1", Post("a", "MDP-5 x"));
        for (var pageNumber = 1; pageNumber <= 60; pageNumber++)
        {
            rig.Client.Pages[("c1", "link" + pageNumber)] = PageOf("link" + (pageNumber + 1), Post("m" + pageNumber, "MDP-5 x"));
        }

        var first = await rig.Service().ReadAsync("MDP-1", null, CancellationToken.None);
        var afterFirst = rig.PostIndex.Get("c1");
        rig.Now = Noon.AddSeconds(30);
        var second = await rig.Service().ReadAsync("MDP-1", null, CancellationToken.None);
        rig.Now = Noon.AddSeconds(50);
        var third = await rig.Service().ReadAsync("MDP-1", null, CancellationToken.None);

        Assert.Equal(15, TeamsThreadService.MaxPagesPerRead);
        Assert.Equal(TeamsThreadState.NotPosted, first.State);
        Assert.Equal(14, afterFirst.DeepScanPages);
        Assert.Equal("link15", afterFirst.DeepScanLink);
        Assert.Equal(("c1", "link15"), rig.Client.PageCalls[15]);
        Assert.True(second.StillSearching);
        Assert.False(third.StillSearching);
        Assert.Equal(1 + TeamsPostIndex.DeepScanPageLimit, rig.Client.PageCalls.Count);
        Assert.True(rig.PostIndex.Get("c1").DeepScanned);
    }

    [Fact]
    public async Task Sync_DoesNotStopBeforeTheThirdPage()
    {
        const int MonthMinutes = 30 * 24 * 60;
        var rig = RigWithChannels("c1");
        rig.Settings.TeamsChannelId = "c1";
        rig.PostIndex.Update("c1", tci => tci with { SyncedAt = Noon.AddHours(-2), DeepScanned = true });
        // The first two pages are ordered by last change, so an old message just edited can fill page one.
        rig.Client.Pages[("c1", null)] = PageOf("p2", Post("edited", "MDP-7 x", minutes: -MonthMinutes));
        rig.Client.Pages[("c1", "p2")] = PageOf("p3", Post("new", "MDP-1 subject", senderId: "someone", minutes: -60));
        rig.Client.Pages[("c1", "p3")] = PageOf("p4", Post("older", "MDP-8 x", minutes: -MonthMinutes));
        rig.Client.Pages[("c1", "p4")] = PageOf("p5", Post("oldest", "MDP-9 x", minutes: -2 * MonthMinutes));
        rig.Client.Threads[("c1", "new")] = ThreadOf("c1", "new");

        var result = await rig.Service().ReadAsync("MDP-1", null, CancellationToken.None);

        Assert.Equal(TeamsThreadState.Found, result.State);
        Assert.Equal("new", result.Thread!.RootId);
        Assert.Equal(new (string, string?)[] { ("c1", null), ("c1", "p2"), ("c1", "p3") }, rig.Client.PageCalls);
        Assert.Equal(Noon, rig.PostIndex.Get("c1").SyncedAt);
    }

    [Fact]
    public async Task ReadAsync_DoesNotResyncWithin60Seconds()
    {
        var rig = RigWithChannels("c1");
        rig.Settings.TeamsChannelId = "c1";
        rig.Client.Pages[("c1", null)] = PageOf(null, Post("a", "MDP-5 x"));
        var service = rig.Service();

        await service.ReadAsync("MDP-1", null, CancellationToken.None);
        rig.Now = Noon.AddSeconds(59);
        await service.ReadAsync("MDP-1", null, CancellationToken.None);
        var callsWithin60Seconds = rig.Client.PageCalls.Count;
        rig.Now = Noon.AddSeconds(61);
        await service.ReadAsync("MDP-1", null, CancellationToken.None);

        Assert.Equal(1, callsWithin60Seconds);
        Assert.Equal(2, rig.Client.PageCalls.Count);
    }

    [Fact]
    public async Task ReadAsync_DropsDeletedRoot_AndReportsNotPosted()
    {
        var rig = RigWithChannels("c1");
        rig.Settings.TeamsChannelId = "c1";
        rig.IndexOwnPost("c1", "MDP-1", "r1");
        // Teams still answers a deleted root's thread; the root itself comes back deleted and is parsed away.
        rig.Client.Threads[("c1", "r1")] = new TeamsThread("c1", "r1", new List<TeamsMessage> { Said(1, "Ann", 1) });

        var result = await rig.Service().ReadAsync("MDP-1", null, CancellationToken.None);

        Assert.Equal(TeamsThreadState.NotPosted, result.State);
        Assert.Equal("c1", result.ChannelId);
        Assert.False(result.StillSearching);
        Assert.Empty(rig.PostIndex.Get("c1").Posts);
    }

    [Fact]
    public async Task ReadAsync_ReportsStillSearching_UntilDeepScanned()
    {
        var rig = RigWithChannels("c1");
        rig.Settings.TeamsChannelId = "c1";
        rig.Client.Pages[("c1", null)] = PageOf("link1", Post("a", "MDP-5 x"));
        for (var pageNumber = 1; pageNumber < 20; pageNumber++)
        {
            rig.Client.Pages[("c1", "link" + pageNumber)] = PageOf("link" + (pageNumber + 1), Post("m" + pageNumber, "MDP-5 x"));
        }
        rig.Client.Pages[("c1", "link20")] = PageOf(null, Post("last", "MDP-5 x"));
        var service = rig.Service();

        var first = await service.ReadAsync("MDP-1", null, CancellationToken.None);
        rig.Now = Noon.AddSeconds(5);
        var second = await service.ReadAsync("MDP-1", null, CancellationToken.None);

        Assert.Equal(TeamsThreadState.NotPosted, first.State);
        Assert.True(first.StillSearching);
        Assert.Equal(TeamsThreadState.NotPosted, second.State);
        Assert.False(second.StillSearching);
        Assert.Equal(21, rig.Client.PageCalls.Count);
    }

    [Fact]
    public async Task ReadAsync_SkipsRecentChannelItCannotRead()
    {
        var rig = RigWithChannels("c1", "c2");
        rig.Settings.TeamsChannelId = "c1";
        rig.Settings.TeamsRecentChannelIds = new List<string> { "c1", "c2" };
        rig.Client.ForbiddenChannelIds.Add("c2");

        var result = await rig.Service().ReadAsync("MDP-1", null, CancellationToken.None);

        Assert.Equal(TeamsThreadState.NotPosted, result.State);
        Assert.False(result.StillSearching);
    }

    [Fact]
    public async Task ReadAsync_DetectsChannel_FromNewestOwnTicketPost_AndSavesIt()
    {
        var rig = RigWithChannels("c1", "c2", "c3");
        rig.Client.Pages[("c1", null)] = PageOf(null, Post("a", "MDP-7 old", minutes: 1));
        rig.Client.Pages[("c2", null)] = PageOf(null, Post("b", "MDP-8 new", minutes: 5), Post("x", "MDP-9 other", senderId: "someone", minutes: 50));
        rig.Client.Pages[("c3", null)] = PageOf(null, Post("c", "MDP-6 mid", minutes: 3));

        var result = await rig.Service().ReadAsync("MDP-1", null, CancellationToken.None);

        Assert.Equal(TeamsThreadState.NotPosted, result.State);
        Assert.Equal("c2", result.ChannelId);
        Assert.Equal("c2", rig.Settings.TeamsChannelId);
    }

    [Fact]
    public async Task ReadAsync_DetectsChannel_SkipsChannelItCannotRead()
    {
        var rig = RigWithChannels("c1", "c2");
        rig.Client.ForbiddenChannelIds.Add("c1");
        rig.Client.Pages[("c2", null)] = PageOf(null, Post("b", "MDP-8 new"));

        var result = await rig.Service().ReadAsync("MDP-1", null, CancellationToken.None);

        Assert.Equal(TeamsThreadState.NotPosted, result.State);
        Assert.Equal("c2", rig.Settings.TeamsChannelId);
    }

    [Fact]
    public async Task ReadAsync_DetectsChannel_ProbesEachChannelOnce()
    {
        var rig = RigWithChannels("c1", "c2", "c3", "c4", "c5", "c6");
        rig.Client.Pages[("c4", null)] = PageOf(null, Post("a", "MDP-7 x"));

        var service = rig.Service();
        await service.ReadAsync("MDP-1", null, CancellationToken.None);
        await service.ReadAsync("MDP-2", null, CancellationToken.None);

        foreach (var probedChannelId in new[] { "c1", "c2", "c3", "c5", "c6" })
        {
            Assert.Single(rig.Client.PageCalls, pc => pc.ChannelId == probedChannelId);
        }
    }

    [Fact]
    public async Task ReadAsync_FillsChannels_OnIndexedPostPath()
    {
        var rig = RigWithChannels("c1", "c2");
        rig.Settings.TeamsChannelId = "c1";
        rig.IndexOwnPost("c1", "MDP-1", "r1");
        rig.Client.Threads[("c1", "r1")] = ThreadOf("c1", "r1");

        var service = rig.Service();
        var first = await service.ReadAsync("MDP-1", null, CancellationToken.None);
        var second = await service.ReadAsync("MDP-1", null, CancellationToken.None);

        Assert.Equal(2, first.Channels.Count);
        Assert.Equal(2, second.Channels.Count);
        Assert.Equal(1, rig.Client.ListCalls);

        rig.Now = Noon.AddMinutes(11);
        await service.ReadAsync("MDP-1", null, CancellationToken.None);
        Assert.Equal(2, rig.Client.ListCalls);
    }

    [Fact]
    public async Task ReadAsync_PagesBack_UntilPostFound_AndIndexesIt()
    {
        var rig = RigWithChannels("c1");
        rig.Settings.TeamsChannelId = "c1";
        rig.Client.Pages[("c1", null)] = PageOf("p2", Post("a", "MDP-5 x"));
        rig.Client.Pages[("c1", "p2")] = PageOf("p3", Post("b", "MDP-4 x"));
        rig.Client.Pages[("c1", "p3")] = PageOf("p4", Post("target", "MDP-1 subject"));
        rig.Client.Threads[("c1", "target")] = ThreadOf("c1", "target");

        var result = await rig.Service().ReadAsync("MDP-1", null, CancellationToken.None);

        // The deep scan reads on to the last page; it indexes every ticket, not just this one.
        Assert.Equal(TeamsThreadState.Found, result.State);
        Assert.Equal(4, rig.Client.PageCalls.Count);
        Assert.Equal(new TeamsIndexedPost("target", Noon, true), rig.PostIndex.Get("c1").Posts["MDP-1"]);
        Assert.Equal("b", rig.PostIndex.Get("c1").Posts["MDP-4"].RootId);
    }

    [Fact]
    public async Task ReadAsync_ReturnsSignedOut_WhenClientThrowsSignedOut()
    {
        var rig = RigWithChannels("c1");
        rig.Client.ListException = new TeamsSignedOutException();

        var result = await rig.Service().ReadAsync("MDP-1", null, CancellationToken.None);

        Assert.Equal(TeamsThreadState.SignedOut, result.State);
        Assert.Equal("", result.MyId);
    }

    [Fact]
    public async Task ReadAsync_ReturnsFailed_OnHttpError()
    {
        var rig = RigWithChannels("c1");
        rig.Client.ListException = new HttpRequestException("503 from Teams");

        var result = await rig.Service().ReadAsync("MDP-1", null, CancellationToken.None);

        Assert.Equal(TeamsThreadState.Failed, result.State);
        Assert.Equal("503 from Teams", result.Error);
    }

    [Fact]
    public async Task ReadAsync_PropagatesCallerCancel()
    {
        var rig = RigWithChannels("c1");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Service().ReadAsync("MDP-1", null, cancellation.Token));
    }

    [Fact]
    public void SelectChannel_KeepsPostsOfOtherChannels()
    {
        var rig = RigWithChannels("c1", "c2");
        rig.IndexOwnPost("c1", "MDP-1", "r1");
        rig.IndexOwnPost("c2", "MDP-2", "r2");

        rig.Service().SelectChannel("c2");

        Assert.Equal("c2", rig.Settings.TeamsChannelId);
        Assert.Equal("r1", rig.PostIndex.Get("c1").Posts["MDP-1"].RootId);
        Assert.Equal("r2", rig.PostIndex.Get("c2").Posts["MDP-2"].RootId);
    }

    [Fact]
    public void SelectChannel_PutsChannelFirstInRecent_DedupesAndCapsAtThree()
    {
        var rig = RigWithChannels("c1", "c2", "c3", "c4");
        var service = rig.Service();

        service.SelectChannel("c1");
        service.SelectChannel("c2");
        service.SelectChannel("c3");
        service.SelectChannel("c1");
        Assert.Equal(new[] { "c1", "c3", "c2" }, rig.Settings.TeamsRecentChannelIds);

        service.SelectChannel("c4");
        Assert.Equal(new[] { "c4", "c1", "c3" }, rig.Settings.TeamsRecentChannelIds);
        Assert.Equal(TeamsThreadService.MaxRecentChannels, rig.Settings.TeamsRecentChannelIds.Count);
    }

    [Fact]
    public async Task ReadAsync_DetectedChannel_JoinsRecent()
    {
        var rig = RigWithChannels("c1");
        rig.Client.Pages[("c1", null)] = PageOf(null, Post("a", "MDP-7 old"));

        await rig.Service().ReadAsync("MDP-1", null, CancellationToken.None);

        Assert.Equal(new[] { "c1" }, rig.Settings.TeamsRecentChannelIds);
    }

    [Fact]
    public async Task ReadAsync_GivesRecentChannelIds_WithTheChannels_AndNoneWithoutThem()
    {
        var rig = RigWithChannels("c1", "c2");
        rig.Settings.TeamsChannelId = "c1";
        rig.Settings.TeamsRecentChannelIds = new List<string> { "c2", "c1" };
        rig.IndexOwnPost("c1", "MDP-1", "r1");
        rig.Client.Threads[("c1", "r1")] = ThreadOf("c1", "r1");

        var found = await rig.Service().ReadAsync("MDP-1", null, CancellationToken.None);
        rig.Client.ListException = new TeamsSignedOutException();
        var signedOut = await rig.Service().ReadAsync("MDP-1", null, CancellationToken.None);

        Assert.Equal(new[] { "c2", "c1" }, found.RecentChannelIds);
        Assert.Null(signedOut.RecentChannelIds);
    }

    [Fact]
    public async Task SendReplyAsync_GivesRecentChannelIds_WithTheRereadThread()
    {
        var rig = RigWithChannels("c1");
        rig.Settings.TeamsRecentChannelIds = new List<string> { "c1" };
        rig.Client.Threads[("c1", "r1")] = ThreadOf("c1", "r1");

        var result = await rig.Service().SendReplyAsync("MDP-1", ThreadOf("c1", "r1"), Draft("hello"), CancellationToken.None);

        Assert.Equal(new[] { "c1" }, result.After!.RecentChannelIds);
    }

    [Fact]
    public async Task PostAnnouncementAsync_PostsToSelectedChannel_AndReturnsThread()
    {
        var rig = RigWithChannels("c1");
        rig.Settings.TeamsChannelId = "c1";

        var result = await rig.Service().PostAnnouncementAsync("MDP-1", " MDP-1 subject ", Draft("line one\nline two"), CancellationToken.None);

        Assert.Equal(TeamsReplyState.Sent, result.State);
        Assert.Equal(TeamsThreadState.Found, result.After!.State);
        Assert.Equal("post-1", result.After.Thread!.RootId);
        Assert.Equal(("c1", "MDP-1 subject", "<p>line one</p><p>line two</p>"), rig.Client.Posts.Single());
    }

    [Fact]
    public async Task PostAnnouncementAsync_IndexesTheNewPost()
    {
        var rig = RigWithChannels("c1");
        rig.Settings.TeamsChannelId = "c1";

        var result = await rig.Service().PostAnnouncementAsync("mdp-1", "MDP-1 subject", Draft("body"), CancellationToken.None);

        Assert.Equal(TeamsReplyState.Sent, result.State);
        Assert.Equal(new TeamsIndexedPost("post-1", Noon, true), rig.PostIndex.Get("c1").Posts["MDP-1"]);
        // The read after the post finds it in the index, so it reads no channel page.
        Assert.Empty(rig.Client.PageCalls);
    }

    [Fact]
    public async Task PostAnnouncementAsync_Fails_WhenNoChannelSelected()
    {
        var rig = RigWithChannels("c1");

        var result = await rig.Service().PostAnnouncementAsync("MDP-1", "subject", Draft("body"), CancellationToken.None);

        Assert.Equal(TeamsReplyState.Failed, result.State);
        Assert.Equal("Pick a channel first", result.Error);
        Assert.Empty(rig.Client.Posts);
    }

    [Theory]
    [InlineData("MDP-1 subject", "")]
    [InlineData("  ", "body without subject")]
    public async Task PostAnnouncementAsync_RefusesMissingSubjectOrBody_WithoutCallingClient(string subject, string text)
    {
        var rig = RigWithChannels("c1");
        rig.Settings.TeamsChannelId = "c1";
        rig.Client.PostException = new InvalidOperationException("must not be called");

        var result = await rig.Service().PostAnnouncementAsync("MDP-1", subject, Draft(text), CancellationToken.None);

        Assert.Equal(TeamsReplyState.Failed, result.State);
        Assert.Empty(rig.Client.Posts);
        Assert.Empty(rig.PostIndex.Get("c1").Posts);
    }

    [Fact]
    public async Task PostAnnouncementAsync_ReturnsUncertain_WhenClientIsUnsure()
    {
        var rig = RigWithChannels("c1");
        rig.Settings.TeamsChannelId = "c1";
        rig.Client.PostException = new TeamsReplyUncertainException("maybe");

        var result = await rig.Service().PostAnnouncementAsync("MDP-1", "subject", Draft("body"), CancellationToken.None);

        Assert.Equal(TeamsReplyState.Uncertain, result.State);
        Assert.Null(result.After);
        Assert.Empty(rig.PostIndex.Get("c1").Posts);
    }

    [Fact]
    public async Task SendReplyAsync_PostsToThreadRoot_AndReturnsRereadThread()
    {
        var rig = RigWithChannels("c1");
        rig.Client.Threads[("c1", "r1")] = ThreadOf("c1", "r1");

        var result = await rig.Service().SendReplyAsync("MDP-1", ThreadOf("c1", "r1"), Draft(" hello\nworld "), CancellationToken.None);

        Assert.Equal(TeamsReplyState.Sent, result.State);
        Assert.Equal(TeamsThreadState.Found, result.After!.State);
        Assert.Equal(("c1", "r1", "<p>hello</p><p>world</p>"), rig.Client.Replies.Single());
        Assert.Single(rig.Client.ThreadCalls);
    }

    [Fact]
    public async Task SendReplyAsync_StaysSent_WhenRereadFails()
    {
        var rig = RigWithChannels("c1");
        rig.Client.ThreadMissingAfterReply = true;

        var result = await rig.Service().SendReplyAsync("MDP-1", ThreadOf("c1", "r1"), Draft("hi"), CancellationToken.None);

        Assert.Equal(TeamsReplyState.Sent, result.State);
        Assert.Null(result.After);
    }

    [Fact]
    public async Task SendReplyAsync_ReturnsUncertain_WhenClientIsUnsure()
    {
        var rig = RigWithChannels("c1");
        rig.Client.ReplyException = new TeamsReplyUncertainException("maybe");

        var result = await rig.Service().SendReplyAsync("MDP-1", ThreadOf("c1", "r1"), Draft("hi"), CancellationToken.None);

        Assert.Equal(TeamsReplyState.Uncertain, result.State);
        Assert.Null(result.After);
    }

    [Fact]
    public async Task SendReplyAsync_RefusesBlankText_WithoutCallingClient()
    {
        var rig = RigWithChannels("c1");
        rig.Client.ReplyException = new InvalidOperationException("must not be called");

        var result = await rig.Service().SendReplyAsync("MDP-1", ThreadOf("c1", "r1"), Draft("  \n "), CancellationToken.None);

        Assert.Equal(TeamsReplyState.Failed, result.State);
        Assert.Empty(rig.Client.Replies);
        Assert.Empty(rig.Client.ThreadCalls);
    }

    [Fact]
    public async Task SendReplyAsync_UploadsImagesThenPostsBodyWithMentionsAndReferences()
    {
        var rig = RigWithChannels("c1");
        var mention = new TeamsMention("Ann", Mri(1), "Ann Lee (Dev)");
        var draft = new TeamsDraft("<p>hi <span data-mention=\"0\">Ann</span></p>", new[] { mention }, new[] { Png("a.png"), Png("b.png") });

        var result = await rig.Service().SendReplyAsync("MDP-1", ThreadOf("c1", "r1"), draft, CancellationToken.None);

        Assert.Equal(TeamsReplyState.Sent, result.State);
        Assert.Equal(new[] { "a.png", "b.png" }, rig.Client.Uploads);
        var body = rig.Client.ReplyBodies.Single();
        Assert.Equal(new[] { "obj-a.png", "obj-b.png" }, body.AmsReferences);
        Assert.Contains("itemtype=\"http://schema.skype.com/Mention\"", body.Html);
        Assert.Contains(Mri(1), body.MentionsJson);
    }

    [Fact]
    public async Task SendReplyAsync_UploadFailure_PostsNothing()
    {
        var rig = RigWithChannels("c1");
        rig.Client.UploadFailure = image => image.FileName == "b.png" ? new HttpRequestException("503") : null;

        var result = await rig.Service().SendReplyAsync("MDP-1", ThreadOf("c1", "r1"), Draft("hi", Png("a.png"), Png("b.png")), CancellationToken.None);

        Assert.Equal(TeamsReplyState.Failed, result.State);
        Assert.Equal("Could not upload b.png", result.Error);
        Assert.Empty(rig.Client.Replies);
    }

    [Fact]
    public async Task SendReplyAsync_RefusesTooManyOrBadImages_WithoutCallingClient()
    {
        var rig = RigWithChannels("c1");
        rig.Client.ReplyException = new InvalidOperationException("must not be called");
        var tooMany = Enumerable.Range(0, TeamsCompose.MaxImages + 1).Select(n => Png("i" + n + ".png")).ToArray();
        var notAnImage = new TeamsImage(new byte[] { 1, 2, 3, 4 }, "notes.txt", 1, 1);

        var manyResult = await rig.Service().SendReplyAsync("MDP-1", ThreadOf("c1", "r1"), Draft("hi", tooMany), CancellationToken.None);
        var badResult = await rig.Service().SendReplyAsync("MDP-1", ThreadOf("c1", "r1"), Draft("hi", notAnImage), CancellationToken.None);

        Assert.Equal(TeamsReplyState.Failed, manyResult.State);
        Assert.Equal("At most 6 images per message.", manyResult.Error);
        Assert.Equal(TeamsReplyState.Failed, badResult.State);
        Assert.Equal("notes.txt is not a PNG, JPEG or GIF image.", badResult.Error);
        Assert.Empty(rig.Client.Uploads);
        Assert.Empty(rig.Client.Replies);
    }

    [Fact]
    public async Task SendReplyAsync_AllowsImageOnlyReply()
    {
        var rig = RigWithChannels("c1");
        rig.Client.Threads[("c1", "r1")] = ThreadOf("c1", "r1");

        var result = await rig.Service().SendReplyAsync("MDP-1", ThreadOf("c1", "r1"), Draft("  ", Png("a.png")), CancellationToken.None);

        Assert.Equal(TeamsReplyState.Sent, result.State);
        Assert.Equal(new[] { "obj-a.png" }, rig.Client.ReplyBodies.Single().AmsReferences);
    }

    [Fact]
    public async Task PostAnnouncementAsync_PostsBodyWithImages()
    {
        var rig = RigWithChannels("c1");
        rig.Settings.TeamsChannelId = "c1";

        var result = await rig.Service().PostAnnouncementAsync("MDP-1", "MDP-1 subject", Draft("see this", Png("a.png")), CancellationToken.None);

        Assert.Equal(TeamsReplyState.Sent, result.State);
        var body = rig.Client.PostBodies.Single();
        Assert.Equal(new[] { "obj-a.png" }, body.AmsReferences);
        Assert.StartsWith("<p>see this</p>", body.Html);
        Assert.Equal("MDP-1 subject", rig.Client.Posts.Single().Subject);
    }

    [Fact]
    public async Task ReadAsync_CollectsPeople_ThreadFirst_WithoutMe_AndPersistsThem()
    {
        var rig = RigWithChannels("c1");
        rig.Settings.TeamsChannelId = "c1";
        rig.IndexOwnPost("c1", "MDP-1", "r1");
        rig.Settings.TeamsPeople = new List<TeamsPerson> { new(Mri(3), "Cat"), new(Mri(1), "Old Ann") };
        rig.Client.Threads[("c1", "r1")] = ThreadWith("c1", "r1", Said(1, "Ann", 1), Said(2, "Bob", 2), new TeamsMessage("mine", Me, "Me", Noon, "x", true, null));

        var result = await rig.Service().ReadAsync("MDP-1", null, CancellationToken.None);

        var expected = new[] { new TeamsPerson(Mri(2), "Bob"), new TeamsPerson(Mri(1), "Ann"), new TeamsPerson(Mri(3), "Cat") };
        Assert.Equal(expected, result.People);
        Assert.Equal(expected, rig.Settings.TeamsPeople);
    }

    [Fact]
    public async Task ReadAsync_DoesNotCollectPeople_FromChannelDetectionProbes()
    {
        var rig = RigWithChannels("c1", "c2");
        rig.Client.Pages[("c1", null)] = PageOf(null, Post("a", "MDP-7 old"), new TeamsMessage("p1", Mri(1), "Ann", Noon, "x", true, null));
        rig.Client.Pages[("c2", null)] = PageOf(null, new TeamsMessage("p2", Mri(2), "Bob", Noon, "x", true, null));

        var result = await rig.Service().ReadAsync("MDP-1", null, CancellationToken.None);

        Assert.Equal("c1", result.ChannelId);
        Assert.Equal(new[] { new TeamsPerson(Mri(1), "Ann") }, rig.Settings.TeamsPeople);
    }

    [Fact]
    public async Task ReadAsync_GivesPeople_AlsoWhenNotPosted()
    {
        var rig = RigWithChannels("c1");
        rig.Settings.TeamsChannelId = "c1";
        rig.Settings.TeamsPeople = new List<TeamsPerson> { new(Mri(3), "Cat") };

        var result = await rig.Service().ReadAsync("MDP-1", null, CancellationToken.None);

        Assert.Equal(TeamsThreadState.NotPosted, result.State);
        Assert.Equal(new[] { new TeamsPerson(Mri(3), "Cat") }, result.People);
    }

    [Fact]
    public async Task SendReplyAsync_AfterResult_StillCarriesPeople()
    {
        var rig = RigWithChannels("c1");
        rig.Settings.TeamsPeople = new List<TeamsPerson> { new(Mri(3), "Cat") };
        rig.Client.Threads[("c1", "r1")] = ThreadWith("c1", "r1", Said(1, "Ann", 1));

        var result = await rig.Service().SendReplyAsync("MDP-1", ThreadOf("c1", "r1"), Draft("hi"), CancellationToken.None);

        Assert.Equal(new[] { new TeamsPerson(Mri(1), "Ann"), new TeamsPerson(Mri(3), "Cat") }, result.After!.People);
    }

    [Fact]
    public async Task SendReplyAsync_UploadTimeoutOrMissingId_FailsWithoutPosting()
    {
        foreach (var failure in new Exception[] { new TaskCanceledException("timeout"), new InvalidOperationException("no object id") })
        {
            var rig = RigWithChannels("c1");
            rig.Client.UploadFailure = image => failure;

            var result = await rig.Service().SendReplyAsync("MDP-1", ThreadOf("c1", "r1"), Draft("hi", Png("a.png")), CancellationToken.None);

            Assert.Equal(TeamsReplyState.Failed, result.State);
            Assert.Equal("Could not upload a.png", result.Error);
            Assert.Empty(rig.Client.Replies);
        }
    }

    [Fact]
    public async Task SendReplyAsync_RefusesImagesOver20MbInAll()
    {
        var rig = RigWithChannels("c1");
        var images = new[] { Png("a.png", 8 * 1024 * 1024), Png("b.png", 8 * 1024 * 1024), Png("c.png", 8 * 1024 * 1024) };

        var result = await rig.Service().SendReplyAsync("MDP-1", ThreadOf("c1", "r1"), Draft("hi", images), CancellationToken.None);

        Assert.Equal(TeamsReplyState.Failed, result.State);
        Assert.Equal("The images are over 20 MB in all.", result.Error);
        Assert.Empty(rig.Client.Uploads);
    }

    [Fact]
    public async Task ReadAsync_DoesNotRewriteSettings_WhenNoNewPeople()
    {
        var rig = RigWithChannels("c1");
        rig.Settings.TeamsChannelId = "c1";
        rig.IndexOwnPost("c1", "MDP-1", "r1");
        rig.Client.Threads[("c1", "r1")] = ThreadWith("c1", "r1", Said(1, "Ann", 1));
        var service = rig.Service();

        await service.ReadAsync("MDP-1", null, CancellationToken.None);
        var savesAfterFirstRead = rig.SaveCount;
        await service.ReadAsync("MDP-1", null, CancellationToken.None);

        Assert.Equal(1, savesAfterFirstRead);
        Assert.Equal(savesAfterFirstRead, rig.SaveCount);
    }

    [Fact]
    public async Task GetImageDataUrlAsync_ReturnsDataUrl_AndCaches()
    {
        var rig = RigWithChannels("c1");
        var service = rig.Service();

        var first = await service.GetImageDataUrlAsync(AmsUrl, CancellationToken.None);
        var second = await service.GetImageDataUrlAsync(AmsUrl, CancellationToken.None);

        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(new byte[4]), first);
        Assert.Equal(first, second);
        Assert.Single(rig.Client.ImageCalls);
    }

    [Fact]
    public async Task GetImageDataUrlAsync_ReturnsNull_ForForeignUrlOrFailure()
    {
        var rig = RigWithChannels("c1");
        var service = rig.Service();

        var foreign = await service.GetImageDataUrlAsync("https://example.com/a.png", CancellationToken.None);
        rig.Client.ImageException = new HttpRequestException("gone", null, HttpStatusCode.NotFound);
        var failed = await service.GetImageDataUrlAsync(AmsUrl, CancellationToken.None);

        Assert.Null(foreign);
        Assert.Null(failed);
        Assert.Equal(new[] { AmsUrl }, rig.Client.ImageCalls);
    }

    [Fact]
    public async Task GetImageDataUrlAsync_EvictsOldestPastByteLimit()
    {
        var rig = RigWithChannels("c1");
        rig.Client.ImageBytesLength = 12 * 1024 * 1024;
        var service = rig.Service();
        var urls = new[] { "a", "b", "c" }.Select(id => AmsUrl.Replace("abc", id)).ToArray();

        foreach (var url in urls)
        {
            await service.GetImageDataUrlAsync(url, CancellationToken.None);
        }

        await service.GetImageDataUrlAsync(urls[2], CancellationToken.None);
        await service.GetImageDataUrlAsync(urls[0], CancellationToken.None);

        Assert.Equal(urls.Append(urls[0]), rig.Client.ImageCalls);
    }

    [Fact]
    public async Task SaveImagesAsync_WritesFilesInOrder_AndClearsOldOnes()
    {
        var rig = RigWithChannels("c1");
        var service = rig.Service();
        var directory = Path.Combine(_directory.Path, "shots");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "stale.png"), "old");
        var sentAt = Noon;
        var shots = new[]
        {
            new TeamsScreenshot(AmsUrl.Replace("abc", "a"), "Linh", sentAt),
            new TeamsScreenshot("https://example.com/foreign.png", "Linh", sentAt),
            new TeamsScreenshot(AmsUrl.Replace("abc", "b"), "Binh", sentAt.AddMinutes(1)),
        };

        var saved = await service.SaveImagesAsync(shots, directory, CancellationToken.None);

        Assert.Equal(new[] { "Linh", "Binh" }, saved.Select(ts => ts.SenderName));
        Assert.Equal(new[] { Path.Combine(directory, "0.png"), Path.Combine(directory, "1.png") }, saved.Select(ts => ts.Path));
        Assert.Equal(new[] { "0.png", "1.png" }, Directory.GetFiles(directory).Select(Path.GetFileName).OrderBy(name => name));
        Assert.Equal(4, File.ReadAllBytes(saved[0].Path!).Length);
    }

    private const string AvatarMri = "8:orgid:11111111-2222-3333-4444-555555555555";
    private const string AvatarObjectId = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public async Task GetAvatarDataUrlAsync_UsesFreshDiskCache_AndRefetchesAfter7Days()
    {
        var rig = RigWithChannels("c1");
        var directory = Path.Combine(rig.AvatarsDirectory, "HR64");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, AvatarObjectId + ".png");
        File.WriteAllBytes(file, new byte[] { 9 });
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-1));

        var fresh = await rig.Service().GetAvatarDataUrlAsync(AvatarMri, "Name", CancellationToken.None);

        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(new byte[] { 9 }), fresh);
        Assert.Empty(rig.Client.ProfileCalls);

        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-8));
        var stale = await rig.Service().GetAvatarDataUrlAsync(AvatarMri, "Name", CancellationToken.None);

        Assert.Equal("data:image/jpeg;base64," + Convert.ToBase64String(new byte[] { 1, 2, 3 }), stale);
        Assert.Single(rig.Client.ProfileCalls);
        Assert.Equal(new[] { AvatarObjectId + ".jpg" }, Directory.GetFiles(directory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task GetAvatarDataUrlAsync_SharesOneDownload_AndACancelledCallerDoesNotCancelIt()
    {
        var rig = RigWithChannels("c1");
        rig.Client.ProfileGate = new TaskCompletionSource();
        var service = rig.Service();
        using var cancellation = new CancellationTokenSource();

        var cancelled = service.GetAvatarDataUrlAsync(AvatarMri, "Name", cancellation.Token);
        var other = service.GetAvatarDataUrlAsync(AvatarMri, "Name", CancellationToken.None);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        rig.Client.ProfileGate.SetResult();

        Assert.NotNull(await other);
        Assert.Single(rig.Client.ProfileCalls);
        Assert.NotNull(await service.GetAvatarDataUrlAsync(AvatarMri, "Name", CancellationToken.None));
        Assert.Single(rig.Client.ProfileCalls);
    }

    [Fact]
    public async Task GetAvatarDataUrlAsync_RemembersNullFor10Minutes()
    {
        var rig = RigWithChannels("c1");
        rig.Client.ProfileResult = null;
        var service = rig.Service();

        Assert.Null(await service.GetAvatarDataUrlAsync(AvatarMri, "Name", CancellationToken.None));
        rig.Now = rig.Now.AddMinutes(9);
        Assert.Null(await service.GetAvatarDataUrlAsync(AvatarMri, "Name", CancellationToken.None));
        Assert.Single(rig.Client.ProfileCalls);

        rig.Now = rig.Now.AddMinutes(2);
        rig.Client.ProfileResult = (new byte[] { 5 }, "image/png");
        Assert.NotNull(await service.GetAvatarDataUrlAsync(AvatarMri, "Name", CancellationToken.None));
        Assert.Equal(2, rig.Client.ProfileCalls.Count);
    }

    private static TeamsMessage OwnMessage(string id, string content, string? subject = null, string senderId = Me, bool hasFiles = false)
    {
        return new TeamsMessage(id, senderId, "name", Noon, content, !content.Contains('<'), subject, null, null, hasFiles);
    }

    private static TeamsThread RootAndReply(string replyContent, string replySenderId = Me)
    {
        return new TeamsThread("c1", "r1", new List<TeamsMessage>
        {
            OwnMessage("r1", "root text", "MDP-1 subject"),
            OwnMessage("m2", replyContent, null, replySenderId),
        });
    }

    private static TeamsDraft HtmlDraft(string html)
    {
        return new TeamsDraft(html, Array.Empty<TeamsMention>(), Array.Empty<TeamsImage>());
    }

    [Fact]
    public async Task EditAsync_RefusesSomeoneElsesMessage()
    {
        var rig = RigWithChannels("c1");
        var thread = RootAndReply("their words", "someone-else");

        var result = await rig.Service().EditAsync("MDP-1", thread, "m2", null, HtmlDraft("<p>x</p>"), CancellationToken.None);
        var missing = await rig.Service().EditAsync("MDP-1", thread, "nope", null, HtmlDraft("<p>x</p>"), CancellationToken.None);
        var deleteResult = await rig.Service().DeleteAsync("MDP-1", thread, "m2", CancellationToken.None);

        Assert.Equal(TeamsReplyState.Failed, result.State);
        Assert.Equal("You can only change your own messages.", result.Error);
        Assert.Equal(TeamsReplyState.Failed, missing.State);
        Assert.Equal(TeamsReplyState.Failed, deleteResult.State);
        Assert.Empty(rig.Client.Edits);
        Assert.Empty(rig.Client.Deletes);
    }

    [Fact]
    public async Task EditAsync_RefusesRootSubjectWithoutTicketKey()
    {
        var rig = RigWithChannels("c1");
        var thread = RootAndReply("mine");

        var wrongSubject = await rig.Service().EditAsync("MDP-1", thread, "r1", "Something else", HtmlDraft("<p>x</p>"), CancellationToken.None);
        var noSubject = await rig.Service().EditAsync("MDP-1", thread, "r1", null, HtmlDraft("<p>x</p>"), CancellationToken.None);

        Assert.Equal(TeamsReplyState.Failed, wrongSubject.State);
        Assert.Equal(TeamsReplyState.Failed, noSubject.State);
        Assert.Empty(rig.Client.Edits);
    }

    [Fact]
    public async Task EditAsync_UsesThreadConversationForReply_AndChannelForRoot()
    {
        var rig = RigWithChannels("c1");
        rig.Client.Threads[("c1", "r1")] = RootAndReply("mine");
        var thread = RootAndReply("<p>old</p><img itemtype=\"http://schema.skype.com/AMSImage\" src=\"https://as-api.asm.skype.com/v1/objects/keep-1/views/imgo\">");

        var replyResult = await rig.Service().EditAsync("MDP-1", thread, "m2", "ignored subject",
            HtmlDraft("<p>new</p><img data-ams=\"keep-1\" width=\"20\" height=\"10\"><img data-ams=\"foreign-9\" width=\"20\" height=\"10\">"), CancellationToken.None);
        var rootResult = await rig.Service().EditAsync("mdp-1", thread, "r1", "  MDP-1 renamed ", HtmlDraft("<p>root</p>"), CancellationToken.None);

        Assert.Equal(TeamsReplyState.Sent, replyResult.State);
        Assert.Equal(TeamsThreadState.Found, replyResult.After!.State);
        Assert.Equal(TeamsReplyState.Sent, rootResult.State);
        var (replyConversation, replyMessageId, replySubject, replyBody) = rig.Client.Edits[0];
        Assert.Equal("c1;messageid=r1", replyConversation);
        Assert.Equal("m2", replyMessageId);
        Assert.Null(replySubject);
        Assert.Equal(new[] { "keep-1" }, replyBody.AmsReferences);
        Assert.StartsWith("<p>new</p><img ", replyBody.Html);
        Assert.DoesNotContain("foreign-9", replyBody.Html);
        var (rootConversation, rootMessageId, rootSubject, _) = rig.Client.Edits[1];
        Assert.Equal("c1", rootConversation);
        Assert.Equal("r1", rootMessageId);
        Assert.Equal("MDP-1 renamed", rootSubject);
    }

    [Fact]
    public async Task EditAsync_RefusesMessagesTheComposerCannotRoundTrip()
    {
        var rig = RigWithChannels("c1");
        var thread = new TeamsThread("c1", "r1", new List<TeamsMessage>
        {
            OwnMessage("r1", "root text", "MDP-1 subject"),
            OwnMessage("m2", "<p>x</p>", null, Me, true),
        });

        var result = await rig.Service().EditAsync("MDP-1", thread, "m2", null, HtmlDraft("<p>x</p>"), CancellationToken.None);

        Assert.Equal(TeamsReplyState.Failed, result.State);
        Assert.Empty(rig.Client.Edits);
    }

    [Fact]
    public async Task EditAsync_ReturnsUncertain_WhenClientIsUnsure()
    {
        var rig = RigWithChannels("c1");
        rig.Client.ChangeException = new TeamsReplyUncertainException("maybe");

        var result = await rig.Service().EditAsync("MDP-1", RootAndReply("mine"), "m2", null, HtmlDraft("<p>x</p>"), CancellationToken.None);

        Assert.Equal(TeamsReplyState.Uncertain, result.State);
        Assert.Null(result.After);
    }

    [Fact]
    public async Task DeleteAsync_Root_DropsIndexEntryAndReturnsNotPosted()
    {
        var rig = RigWithChannels("c1");
        rig.Settings.TeamsChannelId = "c1";
        rig.IndexOwnPost("c1", "MDP-1", "r1");
        var thread = RootAndReply("mine");

        var result = await rig.Service().DeleteAsync("MDP-1", thread, "r1", CancellationToken.None);

        Assert.Equal(TeamsReplyState.Sent, result.State);
        Assert.Equal(("c1", "r1"), rig.Client.Deletes.Single());
        Assert.False(rig.PostIndex.Get("c1").Posts.ContainsKey("MDP-1"));
        Assert.Equal(TeamsThreadState.NotPosted, result.After!.State);
    }

    [Fact]
    public async Task DeleteAsync_Reply_RereadsThread()
    {
        var rig = RigWithChannels("c1");
        rig.Client.Threads[("c1", "r1")] = ThreadOf("c1", "r1");

        var result = await rig.Service().DeleteAsync("MDP-1", RootAndReply("mine"), "m2", CancellationToken.None);

        Assert.Equal(TeamsReplyState.Sent, result.State);
        Assert.Equal(("c1;messageid=r1", "m2"), rig.Client.Deletes.Single());
        Assert.Equal(TeamsThreadState.Found, result.After!.State);
        Assert.Contains(("c1", "r1"), rig.Client.ThreadCalls);
    }

    [Fact]
    public async Task PostAnnouncementAsync_UsesGivenSubject()
    {
        var rig = RigWithChannels("c1");
        rig.Settings.TeamsChannelId = "c1";

        var result = await rig.Service().PostAnnouncementAsync("MDP-1", "  MDP-1 custom subject  ", HtmlDraft("<p><b>bold</b></p>"), CancellationToken.None);

        Assert.Equal(TeamsReplyState.Sent, result.State);
        Assert.Equal(("c1", "MDP-1 custom subject", "<p><b>bold</b></p>"), rig.Client.Posts.Single());
    }

    private const string ChatId = "19:chat1@thread.v2";

    private static TeamsThread ChatThread(string chatId = ChatId)
    {
        return new TeamsThread(chatId, "", new List<TeamsMessage> { OwnMessage("g1", "<p>old</p>"), OwnMessage("g2", "<p>other</p>", null, "someone-else") }, true);
    }

    private Rig RigWithChat()
    {
        var rig = RigWithChannels("c1");
        rig.Client.GroupChats.Add(new TeamsGroupChat(ChatId, "MDP-1 Release cart"));
        rig.Client.GroupChats.Add(new TeamsGroupChat("19:other@thread.v2", "MDP-2 Something"));
        rig.Client.ChatThreads[ChatId] = ChatThread();
        return rig;
    }

    [Fact]
    public async Task ReadAsync_ReturnsChatThread_WhenChatIdIsATicketChat()
    {
        var rig = RigWithChat();

        var result = await rig.Service().ReadAsync("MDP-1", ChatId, CancellationToken.None);

        Assert.Equal(TeamsThreadState.Found, result.State);
        Assert.True(result.Thread!.IsChat);
        Assert.Equal(ChatId, result.ChannelId);
        Assert.Equal("c1", result.Channels.Single().Id);
        Assert.Equal(ChatId, result.Chats!.Single().Id);
    }

    [Fact]
    public async Task ReadAsync_Fails_WhenChatIdIsNotATicketChat()
    {
        var rig = RigWithChat();

        var result = await rig.Service().ReadAsync("MDP-1", "19:other@thread.v2", CancellationToken.None);

        Assert.Equal(TeamsThreadState.Failed, result.State);
        Assert.Equal("This chat is not one of the ticket's chats.", result.Error);
    }

    [Fact]
    public async Task ReadAsync_AddsTicketChats_ToPostResult()
    {
        var rig = RigWithChat();
        rig.Settings.TeamsChannelId = "c1";
        rig.IndexOwnPost("c1", "MDP-1", "r1");
        rig.Client.Threads[("c1", "r1")] = ThreadOf("c1", "r1");

        var result = await rig.Service().ReadAsync("MDP-1", null, CancellationToken.None);

        Assert.Equal(TeamsThreadState.Found, result.State);
        Assert.Equal(ChatId, result.Chats!.Single().Id);
    }

    [Fact]
    public async Task ReadAsync_KeepsPostResult_WhenChatListFails()
    {
        var rig = RigWithChat();
        rig.Client.ChatListException = new HttpRequestException("boom");
        rig.Settings.TeamsChannelId = "c1";
        rig.IndexOwnPost("c1", "MDP-1", "r1");
        rig.Client.Threads[("c1", "r1")] = ThreadOf("c1", "r1");

        var result = await rig.Service().ReadAsync("MDP-1", null, CancellationToken.None);

        Assert.Equal(TeamsThreadState.Found, result.State);
        Assert.Null(result.Chats);
    }

    [Fact]
    public async Task ReadAsync_ReusesChatList_WithinLifetime()
    {
        var rig = RigWithChat();
        var service = rig.Service();

        await service.ReadAsync("MDP-1", ChatId, CancellationToken.None);
        rig.Now = rig.Now.AddSeconds(100);
        await service.ReadAsync("MDP-1", ChatId, CancellationToken.None);
        Assert.Equal(1, rig.Client.ChatListCalls);

        rig.Now = rig.Now.AddSeconds(30);
        await service.ReadAsync("MDP-1", ChatId, CancellationToken.None);
        Assert.Equal(2, rig.Client.ChatListCalls);
    }

    [Fact]
    public async Task ReadAsync_UsesStaleChatList_WhenRefreshFails()
    {
        var rig = RigWithChat();
        var service = rig.Service();
        await service.ReadAsync("MDP-1", ChatId, CancellationToken.None);
        rig.Now = rig.Now.AddMinutes(5);
        rig.Client.ChatListException = new HttpRequestException("boom");

        var chatResult = await service.ReadAsync("MDP-1", ChatId, CancellationToken.None);
        var postResult = await service.ReadAsync("MDP-1", null, CancellationToken.None);

        Assert.Equal(TeamsThreadState.Found, chatResult.State);
        Assert.Equal(ChatId, postResult.Chats!.Single().Id);
    }

    [Fact]
    public async Task SendReplyAsync_PostsChatMessage_WhenThreadIsChat()
    {
        var rig = RigWithChat();

        var result = await rig.Service().SendReplyAsync("MDP-1", ChatThread(), Draft("hello"), CancellationToken.None);

        Assert.Equal(TeamsReplyState.Sent, result.State);
        Assert.Equal(ChatId, rig.Client.ChatPosts.Single().ChatId);
        Assert.Empty(rig.Client.Replies);
        Assert.True(result.After!.Thread!.IsChat);
    }

    [Fact]
    public async Task EditAsync_EditsInChatConversation_WhenThreadIsChat()
    {
        var rig = RigWithChat();

        var result = await rig.Service().EditAsync("MDP-1", ChatThread(), "g1", "ignored", HtmlDraft("<p>new</p>"), CancellationToken.None);

        Assert.Equal(TeamsReplyState.Sent, result.State);
        var (conversationId, messageId, subject, _) = rig.Client.Edits.Single();
        Assert.Equal(ChatId, conversationId);
        Assert.Equal("g1", messageId);
        Assert.Null(subject);
    }

    [Fact]
    public async Task DeleteAsync_DeletesInChat_AndLeavesPostIndex()
    {
        var rig = RigWithChat();
        rig.Settings.TeamsChannelId = "c1";
        rig.IndexOwnPost("c1", "MDP-1", "r1");

        var result = await rig.Service().DeleteAsync("MDP-1", ChatThread(), "g1", CancellationToken.None);

        Assert.Equal(TeamsReplyState.Sent, result.State);
        Assert.Equal((ChatId, "g1"), rig.Client.Deletes.Single());
        Assert.True(rig.PostIndex.Get("c1").Posts.ContainsKey("MDP-1"));
        Assert.True(result.After!.Thread!.IsChat);
    }

    [Fact]
    public async Task ReadConversationsAsync_ReadsIndexedPostAndTicketChats()
    {
        var rig = RigWithChat();
        rig.IndexOwnPost("c1", "MDP-1", "r1");
        rig.Client.Threads[("c1", "r1")] = ThreadOf("c1", "r1");

        var (myId, conversations) = await rig.Service().ReadConversationsAsync(new[] { "MDP-1" }, CancellationToken.None);

        Assert.Equal(Me, myId);
        Assert.Equal(2, conversations.Count);
        var post = conversations.Single(tc => tc.ChatId == null);
        Assert.Equal("MDP-1", post.TicketKey);
        Assert.Equal("", post.Title);
        var chat = conversations.Single(tc => tc.ChatId == ChatId);
        Assert.Equal("MDP-1 Release cart", chat.Title);
        Assert.True(chat.Thread.IsChat);
    }

    [Fact]
    public async Task ReadConversationsAsync_SkipsAFailedConversation()
    {
        var rig = RigWithChat();
        rig.Client.GroupChats.Add(new TeamsGroupChat("19:broken@thread.v2", "MDP-1 broken"));

        var (_, conversations) = await rig.Service().ReadConversationsAsync(new[] { "MDP-1" }, CancellationToken.None);

        Assert.Equal(ChatId, conversations.Single().ChatId);
    }
}
