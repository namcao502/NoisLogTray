using Xunit;

namespace NoisLogTray.Tests;

public class TeamsAttentionTests
{
    private const string Me = "8:orgid:me";
    private static readonly DateTimeOffset Noon = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static TeamsMessage Message(string id, string senderId, int minutes, IReadOnlyDictionary<string, string>? mentions = null)
    {
        return new TeamsMessage(id, senderId, "Sender " + senderId, Noon.AddMinutes(minutes), "body", true, null, null, mentions);
    }

    private static TeamsConversation Conversation(params TeamsMessage[] messages)
    {
        return new TeamsConversation("MDP-1", "19:chat@thread.v2", "MDP-1 chat", new TeamsThread("19:chat@thread.v2", "", messages, true));
    }

    private static TeamsAttentionItem Item(int minutes)
    {
        return new TeamsAttentionItem("MDP-1", "19:chat@thread.v2", "19:chat@thread.v2", "MDP-1 chat", "Bob", Noon.AddMinutes(minutes), false);
    }

    [Fact]
    public void Pending_ReturnsNewestOthersMessage_AfterMyLastMessage()
    {
        var conversation = Conversation(Message("a", "bob", 0), Message("b", Me, 1), Message("c", "bob", 2), Message("d", "amy", 3));

        var item = TeamsAttention.Pending(conversation, Me, null);

        Assert.NotNull(item);
        Assert.Equal("Sender amy", item.SenderName);
        Assert.Equal(Noon.AddMinutes(3), item.SentAt);
        Assert.Equal("19:chat@thread.v2", item.ConversationKey);
    }

    [Fact]
    public void Pending_Null_WhenMyMessageIsLast()
    {
        var conversation = Conversation(Message("a", "bob", 0), Message("b", Me, 1));

        Assert.Null(TeamsAttention.Pending(conversation, Me, null));
    }

    [Fact]
    public void Pending_Null_WhenNewestIsNotAfterSeenAt()
    {
        var conversation = Conversation(Message("a", "bob", 0), Message("b", "bob", 5));

        Assert.Null(TeamsAttention.Pending(conversation, Me, Noon.AddMinutes(5)));
        Assert.NotNull(TeamsAttention.Pending(conversation, Me, Noon.AddMinutes(4)));
    }

    [Fact]
    public void Pending_SetsMentionsMe_WhenAnyPendingMessageTagsMe()
    {
        var mention = new Dictionary<string, string> { ["0"] = Me.ToUpperInvariant() };
        var conversation = Conversation(Message("a", "bob", 0, mention), Message("b", "amy", 1));

        Assert.True(TeamsAttention.Pending(conversation, Me, null)!.MentionsMe);
        Assert.False(TeamsAttention.Pending(Conversation(Message("a", "bob", 0), Message("b", "amy", 1)), Me, null)!.MentionsMe);
    }

    [Fact]
    public void Pending_CountsAllOthersMessages_WhenIHaveNotWritten()
    {
        var mention = new Dictionary<string, string> { ["0"] = Me };
        var conversation = Conversation(Message("a", "bob", 0, mention), Message("b", "amy", 1), Message("c", "bob", 2));

        var item = TeamsAttention.Pending(conversation, Me, null);

        Assert.NotNull(item);
        Assert.Equal(Noon.AddMinutes(2), item.SentAt);
        Assert.True(item.MentionsMe);
    }

    [Fact]
    public void ShouldNotify_False_BeforeBaseline()
    {
        Assert.False(TeamsAttention.ShouldNotify(Item(5), null, false));
    }

    [Fact]
    public void ShouldNotify_True_ForNewConversation_AfterBaseline()
    {
        Assert.True(TeamsAttention.ShouldNotify(Item(5), null, true));
    }

    [Fact]
    public void ShouldNotify_False_WhenNotNewerThanNotifiedAt()
    {
        Assert.False(TeamsAttention.ShouldNotify(Item(5), Noon.AddMinutes(5), true));
        Assert.True(TeamsAttention.ShouldNotify(Item(6), Noon.AddMinutes(5), true));
    }

    [Fact]
    public void Pruned_KeepsAnOldMark_WhoseConversationIsStillRead()
    {
        var now = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        var marks = new Dictionary<string, DateTimeOffset>
        {
            ["still-read"] = now.AddDays(-90),
            ["gone"] = now.AddDays(-90),
            ["recent"] = now.AddDays(-1),
        };

        var pruned = TeamsAttention.Pruned(marks, now, new HashSet<string> { "still-read" });

        Assert.Equal(new[] { "recent", "still-read" }, pruned.Keys.Order().ToArray());
    }

    [Fact]
    public void Pruned_DropsEntriesOlderThanSixtyDays()
    {
        var marks = new Dictionary<string, DateTimeOffset>
        {
            ["old"] = Noon.AddDays(-61),
            ["edge"] = Noon.AddDays(-60),
            ["new"] = Noon.AddDays(-1),
        };

        var pruned = TeamsAttention.Pruned(marks, Noon, new HashSet<string>());

        Assert.Equal(new[] { "edge", "new" }, pruned.Keys.OrderBy(k => k));
        Assert.Equal(3, marks.Count);
    }
}
