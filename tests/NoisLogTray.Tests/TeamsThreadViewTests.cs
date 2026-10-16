using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NoisLogTray.Tests;

public class TeamsThreadViewTests
{
    private const string MyId = "8:orgid:a";
    private const string OtherId = "8:orgid:b";
    private const string ChannelId = "19:abc@thread.tacv2";
    private const string RootId = "1792000000000";
    private const string MinhMri = "8:orgid:0f8fad5b-d9cb-469f-a165-70867728950e";

    private static readonly byte[] PngBytes = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
    private static readonly TeamsChannel PrInTheHole = new(ChannelId, "MDP", "PR in the hole");
    private static readonly TeamsChannel General = new("19:general@thread.tacv2", "MDP", "General");
    private static readonly TeamsGroupChat ChatA = new("19:chata@thread.v2", "MDP-1 Cart feature");
    private static readonly TeamsGroupChat ChatB = new("19:chatb@thread.v2", "MDP-1 Hotfix");
    private static readonly TeamsChannel Alert = new("19:alert@thread.tacv2", "Task-MDP", "Alert");

    // A view whose reads answer with ReadResult (or never), and whose post answers with PostResult.
    private sealed class Rig : IDisposable
    {
        internal Rig(string? initialChatId = null)
        {
            View = new TeamsThreadView("MDP-1",
                (chatId, _, _) =>
                {
                    ReadCalls++;
                    ReadChatIds.Add(chatId);
                    return ReadResult == null ? new TaskCompletionSource<TeamsThreadResult>().Task : Task.FromResult(ReadResult);
                },
                _ =>
                {
                    WaitCalls++;
                    return new TaskCompletionSource<TeamsThreadResult>().Task;
                },
                (_, _, _, _, _) => Task.FromResult<(TeamsAnalysis? Analysis, string? Error)>((null, null)),
                (_, _, _) => Task.FromResult(new TeamsReplyResult(TeamsReplyState.Sent, null, null)),
                (subject, draft, _) =>
                {
                    Posts.Add((subject, draft));
                    return Task.FromResult(PostResult);
                },
                (_, _, _, _, _) => Task.FromResult(new TeamsReplyResult(TeamsReplyState.Sent, null, null)),
                (_, _, _) => Task.FromResult(new TeamsReplyResult(TeamsReplyState.Sent, null, null)),
                (_, messageId, _) =>
                {
                    HandOffs.Add(messageId);
                    return Task.FromResult<string?>(null);
                },
                () => (Draft, PostedAt),
                (_, _) => Task.FromResult<string?>(null),
                (mri, name, _) =>
                {
                    AvatarCalls.Add((mri, name));
                    return Task.FromResult<string?>(null);
                },
                () => Task.FromResult(false),
                SelectedChannels.Add,
                initialChatId,
                (conversationKey, chatId, ask, lastIsMine) => AskReports.Add((conversationKey, chatId, ask, lastIsMine)),
                (conversationKey, prompt, _) =>
                {
                    CommentHandOffs.Add((conversationKey, prompt));
                    return Task.FromResult<string?>(null);
                },
                (conversationKey, action) => FlowActions.Add((conversationKey, action)));
            View.ConversationChanged += ConversationChanges.Add;
        }

        internal TeamsThreadView View { get; }

        internal TeamsThreadResult? ReadResult { get; set; }

        internal TeamsReplyResult PostResult { get; set; } = new(TeamsReplyState.Sent, null, null);

        internal string Draft { get; set; } = "MDP-1 x\r\n\r\nmô tả";

        internal DateTimeOffset? PostedAt { get; set; }

        internal List<(string Subject, TeamsDraft Draft)> Posts { get; } = new();

        internal List<string> SelectedChannels { get; } = new();

        internal List<string> HandOffs { get; } = new();

        internal List<(string ConversationKey, string? ChatId, TeamsMessage? Ask, bool LastIsMine)> AskReports { get; } = new();

        internal List<(string ConversationKey, string Prompt)> CommentHandOffs { get; } = new();

        internal List<(string ConversationKey, string Action)> FlowActions { get; } = new();

        internal List<string?> ReadChatIds { get; } = new();

        internal List<string?> ConversationChanges { get; } = new();

        internal List<(string Mri, string Name)> AvatarCalls { get; } = new();

        internal int ReadCalls { get; private set; }

        internal int WaitCalls { get; private set; }

        public void Dispose()
        {
            View.Dispose();
        }
    }

    [Fact]
    public void ThreadScripts_UseInnerHtmlOnlyInTheTwoSinks()
    {
        var pageFolder = Path.Combine(AppContext.BaseDirectory, "Assets", "teams");
        var threadScript = File.ReadAllText(Path.Combine(pageFolder, "thread.js"));
        var composerScript = File.ReadAllText(Path.Combine(pageFolder, "composer.js"));
        var codeBlockScript = File.ReadAllText(Path.Combine(pageFolder, "codeblock.js"));
        var page = File.ReadAllText(Path.Combine(pageFolder, "index.html"));

        Assert.Single(Regex.Matches(threadScript, "innerHTML"));
        Assert.Single(Regex.Matches(composerScript, "innerHTML"));
        // The code blocks draw their colors through the composer's sink.
        Assert.Empty(Regex.Matches(codeBlockScript, "innerHTML"));
        foreach (var script in new[] { threadScript, composerScript, codeBlockScript })
        {
            Assert.Empty(Regex.Matches(script, @"outerHTML|insertAdjacentHTML|document\.write"));
        }
        Assert.Empty(Regex.Matches(page, @"<script\b(?![^>]*\bsrc\s*=)[^>]*>", RegexOptions.IgnoreCase));
        Assert.Empty(Regex.Matches(page, @"<[^>]*\son[a-z]+\s*=", RegexOptions.IgnoreCase));
    }

    [Fact]
    public void IndexHtml_KeepsStrictCsp_AndLoadsMarkedBeforeScripts()
    {
        var page = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "teams", "index.html"));

        Assert.Contains("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'self'; img-src 'self' data: https://statics.teams.cdn.office.net; style-src 'self' 'unsafe-inline'\">", page);
        var scriptSources = Regex.Matches(page, @"<script\s+src=""(?<src>[^""]+)""").Select(m => m.Groups["src"].Value).ToList();
        Assert.Equal(new[] { "../marked/marked.umd.js", "../highlight/highlight.min.js", "codeblock.js", "composer.js", "thread.js" }, scriptSources);
    }

    [Fact]
    public void BuildThreadMessage_GivesHcmTimeInitialsAndMine()
    {
        var thread = new TeamsThread(ChannelId, RootId, new[]
        {
            new TeamsMessage(RootId, MyId, "Nam Nguyen (DU2 - NOIS)", new DateTimeOffset(2026, 10, 14, 9, 20, 0, TimeSpan.Zero),
                "<p>Grid and export</p>", false, "MDP-9310 Permitted SKU UI change requests"),
            new TeamsMessage("1792000000001", OtherId, "Minh Hoàng (DU2)", new DateTimeOffset(2026, 10, 14, 10, 2, 0, TimeSpan.Zero),
                "sort Size theo SeqNo?", true, null),
        });

        var message = TeamsThreadView.BuildThreadMessage(Found(thread));

        Assert.Equal("thread", message["type"]!.GetValue<string>());
        var root = message["messages"]![0]!;
        var reply = message["messages"]![1]!;
        Assert.Equal(RootId, root["id"]!.GetValue<string>());
        Assert.Equal("Nam Nguyen (DU2 - NOIS)", root["senderName"]!.GetValue<string>());
        Assert.Equal("NN", root["initials"]!.GetValue<string>());
        Assert.Equal("14/10 16:20", root["timeText"]!.GetValue<string>());
        Assert.True(root["mine"]!.GetValue<bool>());
        Assert.False(root["edited"]!.GetValue<bool>());
        Assert.Equal("MDP-9310 Permitted SKU UI change requests", root["subject"]!.GetValue<string>());
        Assert.Equal("<p>Grid and export</p>", root["html"]!.GetValue<string>());
        Assert.Equal("MH", reply["initials"]!.GetValue<string>());
        Assert.Equal("14/10 17:02", reply["timeText"]!.GetValue<string>());
        Assert.False(reply["mine"]!.GetValue<bool>());
        Assert.True(reply["isPlain"]!.GetValue<bool>());
        Assert.Null(reply["subject"]);
        Assert.False(message["stillSearching"]!.GetValue<bool>());
    }

    [Fact]
    public void BuildThreadMessage_AddsSenderMri_OnlyForPersons()
    {
        var sentAt = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
        var thread = new TeamsThread(ChannelId, RootId, new[]
        {
            new TeamsMessage(RootId, MinhMri, "Minh Hoang", sentAt, "a", true, "MDP-1"),
            new TeamsMessage("2", "28:bot-id", "A bot", sentAt.AddMinutes(1), "b", true, null),
        });

        var messages = TeamsThreadView.BuildThreadMessage(Found(thread))["messages"]!.AsArray();

        Assert.Equal(MinhMri, messages[0]!["senderMri"]!.GetValue<string>());
        Assert.Null(messages[1]!["senderMri"]);
    }

    [Fact]
    public async Task LoadAvatarAsync_ReturnsNull_ForNonPersonMri_WithoutCallingLoader()
    {
        using var rig = new Rig();

        var dataUrl = await rig.View.LoadAvatarAsync("28:bot-id", "A bot", CancellationToken.None);

        Assert.Null(dataUrl);
        Assert.Empty(rig.AvatarCalls);
    }

    [Fact]
    public async Task LoadAvatarAsync_TruncatesLongName()
    {
        using var rig = new Rig();

        await rig.View.LoadAvatarAsync(MinhMri, new string('a', 500), CancellationToken.None);

        Assert.Single(rig.AvatarCalls);
        Assert.Equal(MinhMri, rig.AvatarCalls[0].Mri);
        Assert.Equal(200, rig.AvatarCalls[0].Name.Length);
    }

    [Theory]
    [InlineData(true, true, true, false, true)]
    [InlineData(false, true, true, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, true, true, true, false)]
    public void PollTick_ReadsOnlyWhenVisibleForegroundAndFound(bool isVisible, bool isForeground, bool isThreadShown, bool isBusy, bool expected)
    {
        Assert.Equal(expected, TeamsThreadView.ShouldPoll(isVisible, isForeground, isThreadShown, isBusy));
    }

    [Fact]
    public void PollResult_WithSameMessages_SendsNothing()
    {
        var sentAt = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
        var root = new TeamsMessage(RootId, MyId, "Nam", sentAt, "a", true, "MDP-1");
        var reply = new TeamsMessage("2", OtherId, "Minh", sentAt.AddMinutes(1), "b", true, null);
        var previous = new TeamsThread(ChannelId, RootId, new[] { root, reply });

        Assert.True(TeamsThreadView.HasSameMessages(previous, new TeamsThread(ChannelId, RootId, new[] { root, reply })));
        Assert.False(TeamsThreadView.HasSameMessages(previous, new TeamsThread(ChannelId, RootId, new[] { root })));
        var newer = new TeamsMessage("3", OtherId, "Minh", sentAt.AddMinutes(2), "c", true, null);
        Assert.False(TeamsThreadView.HasSameMessages(previous, new TeamsThread(ChannelId, RootId, new[] { root, reply, newer })));
        var edited = reply with { EditedAt = sentAt.AddMinutes(5) };
        Assert.False(TeamsThreadView.HasSameMessages(previous, new TeamsThread(ChannelId, RootId, new[] { root, edited })));
        Assert.False(TeamsThreadView.HasSameMessages(null, previous));
    }

    [Fact]
    public void PollResult_WithChangedReactions_IsNotTheSame()
    {
        var sentAt = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
        var root = new TeamsMessage(RootId, MyId, "Nam", sentAt, "a", true, "MDP-1");
        var previous = new TeamsThread(ChannelId, RootId, new[] { root });
        var liked = root with { Reactions = new[] { new TeamsReaction("like", new[] { OtherId }) } };
        var likedTwice = root with { Reactions = new[] { new TeamsReaction("like", new[] { OtherId, MyId }) } };

        Assert.False(TeamsThreadView.HasSameMessages(previous, new TeamsThread(ChannelId, RootId, new[] { liked })));
        Assert.True(TeamsThreadView.HasSameMessages(new TeamsThread(ChannelId, RootId, new[] { liked }), new TeamsThread(ChannelId, RootId, new[] { liked })));
        Assert.False(TeamsThreadView.HasSameMessages(new TeamsThread(ChannelId, RootId, new[] { liked }), new TeamsThread(ChannelId, RootId, new[] { likedTwice })));
    }

    [Fact]
    public void BuildThreadMessage_CarriesReactions_WithMineAndNames()
    {
        var sentAt = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
        var root = new TeamsMessage(RootId, MyId, "Nam Nguyen", sentAt, "a", true, "MDP-1",
            Reactions: new[] { new TeamsReaction("like", new[] { OtherId, MyId, "8:orgid:c" }), new TeamsReaction("heart", new[] { OtherId }) });
        var reply = new TeamsMessage("2", OtherId, "Minh Hoang", sentAt.AddMinutes(1), "b", true, null);
        var result = Found(new TeamsThread(ChannelId, RootId, new[] { root, reply })) with
        {
            People = new[] { new TeamsPerson("8:orgid:c", "Lan Tran") },
        };

        var reactions = TeamsThreadView.BuildThreadMessage(result)["messages"]![0]!["reactions"]!.AsArray();

        Assert.Equal(2, reactions.Count);
        Assert.Equal("like", reactions[0]!["key"]!.GetValue<string>());
        Assert.Equal(3, reactions[0]!["count"]!.GetValue<int>());
        Assert.True(reactions[0]!["mine"]!.GetValue<bool>());
        Assert.Equal("Minh Hoang, Nam Nguyen, Lan Tran", reactions[0]!["names"]!.GetValue<string>());
        Assert.Equal(TeamsChat.EmojiUrl("like"), reactions[0]!["url"]!.GetValue<string>());
        Assert.False(reactions[1]!["mine"]!.GetValue<bool>());
        Assert.Null(TeamsThreadView.BuildThreadMessage(result)["messages"]![1]!["reactions"]);
    }

    [Fact]
    public void BuildThreadMessage_ColorIndex_IsStablePerSender()
    {
        var sentAt = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
        var thread = new TeamsThread(ChannelId, RootId, new[]
        {
            new TeamsMessage(RootId, MyId, "Nam Nguyen", sentAt, "a", true, "MDP-1"),
            new TeamsMessage("2", OtherId, "Minh Hoang", sentAt.AddMinutes(1), "b", true, null),
            new TeamsMessage("3", MyId, "Nam Nguyen", sentAt.AddMinutes(2), "c", true, null),
        });

        var messages = TeamsThreadView.BuildThreadMessage(Found(thread))["messages"]!.AsArray();

        // The sum of the id's character codes, mod 8: 802 for "8:orgid:a", 803 for "8:orgid:b".
        Assert.Equal(2, messages[0]!["colorIndex"]!.GetValue<int>());
        Assert.Equal(3, messages[1]!["colorIndex"]!.GetValue<int>());
        Assert.Equal(2, messages[2]!["colorIndex"]!.GetValue<int>());
    }

    [Fact]
    public void BuildThreadMessage_SetsNeedsReplyAndDeepLink()
    {
        var sentAt = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
        var root = new TeamsMessage(RootId, MyId, "Nam Nguyen", sentAt, "a", true, "MDP-1");
        var question = new TeamsMessage("2", OtherId, "Minh Hoang", sentAt.AddMinutes(1), "b", true, null);
        var answer = new TeamsMessage("3", MyId, "Nam Nguyen", sentAt.AddMinutes(2), "c", true, null);

        var waiting = TeamsThreadView.BuildThreadMessage(Found(new TeamsThread(ChannelId, RootId, new[] { root, question })));
        var answered = TeamsThreadView.BuildThreadMessage(Found(new TeamsThread(ChannelId, RootId, new[] { root, question, answer })));

        Assert.True(waiting["needsReply"]!.GetValue<bool>());
        Assert.False(answered["needsReply"]!.GetValue<bool>());
        Assert.Equal(TeamsChat.DeepLink(ChannelId, RootId), waiting["deepLink"]!.GetValue<string>());
        Assert.Equal(ChannelId, waiting["channelId"]!.GetValue<string>());
        Assert.Equal("MDP / PR in the hole", waiting["channelName"]!.GetValue<string>());
    }

    [Fact]
    public void BuildThreadMessage_GroupsChannels_RecentFirst()
    {
        var thread = new TeamsThread(ChannelId, RootId, new[]
        {
            new TeamsMessage(RootId, MyId, "Nam Nguyen", new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero), "a", true, "MDP-1"),
        });
        var result = Found(thread) with { Channels = new[] { PrInTheHole, General, Alert }, RecentChannelIds = new[] { Alert.Id, "19:gone@thread.tacv2" } };

        var groups = TeamsThreadView.BuildThreadMessage(result)["channelGroups"]!.AsArray();
        var noRecent = TeamsThreadView.BuildThreadMessage(Found(thread))["channelGroups"]!.AsArray();

        Assert.Equal(3, groups.Count);
        Assert.True(groups[0]!["recent"]!.GetValue<bool>());
        Assert.Equal(Lang.T("Recent", "Gần đây"), groups[0]!["title"]!.GetValue<string>());
        Assert.Equal("Task-MDP / Alert", groups[0]!["channels"]![0]!["name"]!.GetValue<string>());
        Assert.False(groups[1]!["recent"]!.GetValue<bool>());
        Assert.Equal("MDP", groups[1]!["title"]!.GetValue<string>());
        Assert.Equal(new[] { "PR in the hole", "General" }, groups[1]!["channels"]!.AsArray().Select(jn => jn!["name"]!.GetValue<string>()));
        Assert.Equal(Alert.Id, groups[2]!["channels"]![0]!["id"]!.GetValue<string>());
        Assert.False(Assert.Single(noRecent)!["recent"]!.GetValue<bool>());
    }

    [Fact]
    public void BuildThreadMessage_MarksOwnEditableMessagesWithEditHtml()
    {
        var sentAt = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
        var mentionMris = new Dictionary<string, string> { ["0"] = MinhMri };
        var thread = new TeamsThread(ChannelId, RootId, new[]
        {
            new TeamsMessage(RootId, MyId, "Nam Nguyen", sentAt,
                "<p>Hi <span itemtype=\"http://schema.skype.com/Mention\" itemscope itemid=\"0\">Minh</span></p>", false, "MDP-1 x",
                sentAt.AddMinutes(5), mentionMris),
            new TeamsMessage("2", OtherId, "Minh Dinh", sentAt.AddMinutes(1), "<p>ok</p>", false, null),
            new TeamsMessage("3", MyId, "Nam Nguyen", sentAt.AddMinutes(2), "line one\nline two", true, null),
        });

        var messages = TeamsThreadView.BuildThreadMessage(Found(thread))["messages"]!.AsArray();

        Assert.True(messages[0]!["editable"]!.GetValue<bool>());
        Assert.True(messages[0]!["edited"]!.GetValue<bool>());
        Assert.Contains($"<span class=\"mention\" data-mri=\"{MinhMri}\">Minh</span>", messages[0]!["editHtml"]!.GetValue<string>());
        Assert.False(messages[1]!["editable"]!.GetValue<bool>()); // someone else's
        Assert.Null(messages[1]!["editHtml"]);
        Assert.True(messages[2]!["editable"]!.GetValue<bool>());
        Assert.Equal("<p>line one</p><p>line two</p>", messages[2]!["editHtml"]!.GetValue<string>());
    }

    [Fact]
    public void BuildThreadMessage_DisablesEditForUnsupportedContent()
    {
        var sentAt = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
        var thread = new TeamsThread(ChannelId, RootId, new[]
        {
            new TeamsMessage(RootId, MyId, "Nam Nguyen", sentAt, "<table><tr><td>a</td></tr></table>", false, "MDP-1 x"),
            new TeamsMessage("2", MyId, "Nam Nguyen", sentAt.AddMinutes(1), "<p>see file</p>", false, null, HasFiles: true),
        });

        var messages = TeamsThreadView.BuildThreadMessage(Found(thread))["messages"]!.AsArray();

        Assert.All(messages, jn =>
        {
            Assert.True(jn!["mine"]!.GetValue<bool>());
            Assert.False(jn["editable"]!.GetValue<bool>());
            Assert.Null(jn["editHtml"]);
        });
    }

    [Fact]
    public void BuildAnalysisMessage_CarriesIssuesAndReplies()
    {
        var analysis = new TeamsAnalysis(new[] { new TeamsIssue("Export fails", "fixed") }, "Null id", "Retest", new[] { "Dạ e sửa rồi\nmời a test" });

        var message = TeamsThreadView.BuildAnalysisMessage(analysis);

        Assert.Equal("analysis", message["type"]!.GetValue<string>());
        Assert.Equal("done", message["kind"]!.GetValue<string>());
        Assert.Equal("Null id", message["diagnosis"]!.GetValue<string>());
        Assert.Equal("Retest", message["nextStep"]!.GetValue<string>());
        var issue = message["issues"]!.AsArray().Single()!;
        Assert.Equal("Export fails", issue["text"]!.GetValue<string>());
        Assert.Equal("fixed", issue["status"]!.GetValue<string>());
        var reply = message["items"]!.AsArray().Single()!;
        Assert.Equal("Dạ e sửa rồi\nmời a test", reply["text"]!.GetValue<string>());
        Assert.Contains("<p>", reply["html"]!.GetValue<string>());
    }

    [Fact]
    public void BuildAnalysisMessage_GivesEachReplyItsComposerHtml()
    {
        var analysis = new TeamsAnalysis(Array.Empty<TeamsIssue>(), "", "", new[] { "line one\nline two", "second" });

        var items = TeamsThreadView.BuildAnalysisMessage(analysis)["items"]!.AsArray();

        Assert.Equal(2, items.Count);
        Assert.Equal("<p>line one</p><p>line two</p>", items[0]!["html"]!.GetValue<string>());
        Assert.Equal("<p>second</p>", items[1]!["html"]!.GetValue<string>());
    }

    [Fact]
    public void ThreadJs_HasNoUseSuggestionButton_AndRendersSuggestionRow()
    {
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "teams", "thread.js"));
        var page = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "teams", "index.html"));

        Assert.DoesNotContain("useSuggestion", script);
        Assert.Contains("replySuggestions", script);
        Assert.Contains("id=\"replySuggestions\"", page);
    }

    [Fact]
    public void IndexHtml_HasNoSidePanel_AndHasAnalysisStrip()
    {
        var page = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "teams", "index.html"));
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "teams", "thread.js"));

        Assert.DoesNotContain("id=\"side\"", page);
        Assert.DoesNotContain("noOneWaiting", script);
        Assert.Contains("id=\"analysisStrip\"", page);
        Assert.Contains("id=\"replySuggestions\"", page);
        Assert.Contains("id=\"sideColumn\"", page);
        Assert.Contains("min-width: 1300px", page);
    }

    [Fact]
    public void BuildDraftMessage_SplitsSubjectFromMarkdown()
    {
        var draft = "MDP-1 Title\r\n\r\nWhat changed\r\n\r\n[BE]\r\nmain: https://x/1\r\ndev: https://x/2";
        var postedAt = new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.FromHours(7));

        var message = TeamsThreadView.BuildDraftMessage("MDP-1", NotPosted(), draft, postedAt, reload: true);
        var neverPosted = TeamsThreadView.BuildDraftMessage("MDP-1", NotPosted(), "MDP-1 Title", null, reload: false);

        Assert.Equal("draft", message["type"]!.GetValue<string>());
        Assert.Equal("MDP-1 Title", message["subject"]!.GetValue<string>());
        Assert.Equal("What changed\n\n[BE]\nmain: https://x/1\ndev: https://x/2", message["markdown"]!.GetValue<string>());
        Assert.Contains("09:30", message["postedAt"]!.GetValue<string>());
        Assert.Contains(@"docs\MDP-1\teams-post.md", message["source"]!.GetValue<string>());
        Assert.True(message["reload"]!.GetValue<bool>());
        Assert.Equal(ChannelId, message["channelId"]!.GetValue<string>());
        Assert.NotEmpty(message["channelGroups"]!.AsArray());
        Assert.Equal("", neverPosted["markdown"]!.GetValue<string>());
        Assert.Null(neverPosted["postedAt"]);
        Assert.False(neverPosted["reload"]!.GetValue<bool>());
    }

    [Fact]
    public void ApplyResult_NotPosted_SendsDraftMode()
    {
        using var rig = new Rig();
        rig.View.ApplyResult(Found(new TeamsThread(ChannelId, RootId, new[] { Message(RootId, MyId, 0, "MDP-1 x") })));
        Assert.NotNull(rig.View.LatestMessage("thread"));

        rig.View.ApplyResult(NotPosted());

        Assert.Null(rig.View.LatestMessage("thread"));
        var draft = rig.View.LatestMessage("draft");
        Assert.NotNull(draft);
        Assert.Equal("MDP-1 x", draft["subject"]!.GetValue<string>());
        Assert.Equal("mô tả", draft["markdown"]!.GetValue<string>());
    }

    [Fact]
    public void ApplyResult_FailedWhileDrafting_KeepsDraft()
    {
        using var rig = new Rig();
        rig.View.ApplyResult(Failed());
        Assert.False(rig.View.LatestMessage("state")!["inline"]!.GetValue<bool>()); // no draft yet: the error covers the page

        rig.View.ApplyResult(NotPosted());
        rig.View.ApplyResult(Failed());

        Assert.NotNull(rig.View.LatestMessage("draft"));
        var state = rig.View.LatestMessage("state")!;
        Assert.Equal("failed", state["kind"]!.GetValue<string>());
        Assert.True(state["inline"]!.GetValue<bool>());
    }

    [Fact]
    public void ApplyResult_PostsPeople_OnlyWhenTheResultHasThem()
    {
        using var rig = new Rig();
        var withPeople = NotPosted() with
        {
            People = new[] { new TeamsPerson(MinhMri, "Minh Dinh (DU2 - Tech Leader)"), new TeamsPerson(OtherId, "Lan Tran") },
        };

        rig.View.ApplyResult(withPeople);
        rig.View.ApplyResult(Failed());

        var people = rig.View.LatestMessage("people")!["people"]!.AsArray();
        Assert.Equal(2, people.Count);
        Assert.Equal(MinhMri, people[0]!["mri"]!.GetValue<string>());
        Assert.Equal("Minh Dinh (DU2 - Tech Leader)", people[0]!["name"]!.GetValue<string>());
        Assert.Equal("Minh Dinh", people[0]!["shortName"]!.GetValue<string>());
        Assert.Equal("Lan Tran", people[1]!["shortName"]!.GetValue<string>());
    }

    [Fact]
    public void ReadResult_RaisesNeedsReply()
    {
        using var rig = new Rig();
        var raised = new List<bool>();
        rig.View.NeedsReplyChanged += raised.Add;
        var root = Message(RootId, MyId, 0, "MDP-1 x");
        var question = Message("2", OtherId, 1, null);
        var answer = Message("3", MyId, 2, null);

        foreach (var result in new[]
        {
            Found(new TeamsThread(ChannelId, RootId, new[] { root, question })),
            Found(new TeamsThread(ChannelId, RootId, new[] { root, question, answer })),
            NotPosted(),
            Failed(),
        })
        {
            rig.ReadResult = result;
            rig.View.HandleMessage(PageMessage(new JsonObject { ["type"] = "refresh" }).RootElement);
        }

        Assert.Equal(4, rig.ReadCalls);
        Assert.Equal(new[] { true, false, false, false }, raised);
    }

    [Fact]
    public void HandleMessage_HandToClaude_IgnoresOwnOrUnknownMessage()
    {
        using var rig = new Rig();
        var question = Message("2", OtherId, 1, null);
        var thread = new TeamsThread(ChannelId, RootId, new[] { Message(RootId, MyId, 0, "MDP-1 x"), question });
        rig.View.ApplyResult(Found(thread));

        rig.View.HandleMessage(PageMessage(new JsonObject { ["type"] = "handToClaude", ["messageId"] = RootId }).RootElement);
        rig.View.HandleMessage(PageMessage(new JsonObject { ["type"] = "handToClaude", ["messageId"] = "999" }).RootElement);
        Assert.Empty(rig.HandOffs);

        rig.View.HandleMessage(PageMessage(new JsonObject { ["type"] = "handToClaude", ["messageId"] = question.Id }).RootElement);
        Assert.Equal(new[] { question.Id }, rig.HandOffs);
    }

    [Fact]
    public void BuildCommentFlowMessage_CarriesStagePromptAndReply()
    {
        var flow = new TeamsCommentFlow("MDP-1", "key", "", "5", "Minh Dinh", TeamsCommentStage.HandedOff, false, 2);

        var message = TeamsThreadView.BuildCommentFlowMessage(flow);

        Assert.Equal("commentFlow", message["type"]!.GetValue<string>());
        Assert.Equal("key", message["conversationKey"]!.GetValue<string>());
        Assert.Equal("handedOff", message["stage"]!.GetValue<string>());
        Assert.Equal("Minh Dinh", message["sender"]!.GetValue<string>());
        Assert.Equal("pr-comments MDP-1", message["prompt"]!.GetValue<string>());
        Assert.Equal(2, message["openThreads"]!.GetValue<int>());
        Assert.Equal(TeamsReply.DoneReply, message["reply"]!.GetValue<string>());
        Assert.Contains(TeamsReply.DoneReply, message["replyHtml"]!.GetValue<string>());
    }

    [Fact]
    public void SetCommentFlow_PostsOnlyForTheShownConversation()
    {
        using var rig = new Rig();
        var thread = new TeamsThread(ChannelId, RootId, new[] { Message(RootId, MyId, 0, "MDP-1 x"), Message("2", OtherId, 1, null) });
        rig.View.ApplyResult(Found(thread));
        var shownKey = TeamsChat.ConversationKey(thread);

        rig.View.SetCommentFlow(new TeamsCommentFlow("MDP-1", "other", "", "2", "Minh", TeamsCommentStage.Asked));
        Assert.Null(rig.View.LatestMessage("commentFlow"));

        rig.View.SetCommentFlow(new TeamsCommentFlow("MDP-1", shownKey, "", "2", "Minh", TeamsCommentStage.Asked));
        Assert.Equal("asked", rig.View.LatestMessage("commentFlow")!["stage"]!.GetValue<string>());

        rig.View.SetCommentFlow(null);
        Assert.Null(rig.View.LatestMessage("commentFlow"));
    }

    [Fact]
    public void HandleMessage_HandCommentsToClaude_PassesPrompt_OnlyForShownConversation()
    {
        using var rig = new Rig();
        var thread = new TeamsThread(ChannelId, RootId, new[] { Message(RootId, MyId, 0, "MDP-1 x"), Message("2", OtherId, 1, null) });
        rig.View.ApplyResult(Found(thread));
        var shownKey = TeamsChat.ConversationKey(thread);

        rig.View.HandleMessage(PageMessage(new JsonObject { ["type"] = "handCommentsToClaude", ["conversationKey"] = "other", ["prompt"] = "x" }).RootElement);
        Assert.Empty(rig.CommentHandOffs);

        rig.View.HandleMessage(PageMessage(new JsonObject { ["type"] = "handCommentsToClaude", ["conversationKey"] = shownKey, ["prompt"] = "pr-comments MDP-1" }).RootElement);
        rig.View.HandleMessage(PageMessage(new JsonObject { ["type"] = "dismissCommentFlow", ["conversationKey"] = shownKey }).RootElement);
        rig.View.HandleMessage(PageMessage(new JsonObject { ["type"] = "commentFlowDone", ["conversationKey"] = "other" }).RootElement);

        Assert.Equal(new[] { (shownKey, "pr-comments MDP-1") }, rig.CommentHandOffs);
        Assert.Equal(new[] { (shownKey, "dismiss") }, rig.FlowActions);
    }

    [Fact]
    public void ApplyResult_ReportsCommentAsk_WithLastIsMine()
    {
        using var rig = new Rig();
        var root = Message(RootId, MyId, 0, "MDP-1 x");
        var ask = new TeamsMessage("2", OtherId, "name", new DateTimeOffset(2026, 10, 14, 9, 1, 0, TimeSpan.Zero), "check cmt giúp a", true, null);
        var thread = new TeamsThread(ChannelId, RootId, new[] { root, ask });
        var key = TeamsChat.ConversationKey(thread);

        rig.View.ApplyResult(Found(thread));
        rig.View.ApplyResult(Found(new TeamsThread(ChannelId, RootId, new[] { root, ask, Message("3", MyId, 2, "ok") })));

        Assert.Equal(2, rig.AskReports.Count);
        Assert.Equal(key, rig.AskReports[0].ConversationKey);
        Assert.True(string.IsNullOrEmpty(rig.AskReports[0].ChatId));
        Assert.Equal("2", rig.AskReports[0].Ask!.Id);
        Assert.False(rig.AskReports[0].LastIsMine);
        Assert.Null(rig.AskReports[1].Ask);
        Assert.True(rig.AskReports[1].LastIsMine);
    }

    [Fact]
    public void HandleMessage_SelectConversation_IgnoresUnknownChat()
    {
        using var rig = new Rig();
        rig.View.ApplyResult(WithChats(Found(new TeamsThread(ChannelId, RootId, new[] { Message(RootId, MyId, 0, "MDP-1 x") })), ChatA));
        rig.ConversationChanges.Clear();

        rig.View.HandleMessage(PageMessage(new JsonObject { ["type"] = "selectConversation", ["chatId"] = "19:unknown@thread.v2" }).RootElement);

        Assert.Equal(0, rig.ReadCalls);
        Assert.Empty(rig.ConversationChanges);
    }

    [Fact]
    public void HandleMessage_SelectConversation_SwitchesAndReads()
    {
        using var rig = new Rig();
        rig.View.ApplyResult(WithChats(Found(new TeamsThread(ChannelId, RootId, new[] { Message(RootId, MyId, 0, "MDP-1 x") })), ChatA));

        rig.View.HandleMessage(PageMessage(new JsonObject { ["type"] = "selectConversation", ["chatId"] = ChatA.Id }).RootElement);

        Assert.Equal(new string?[] { ChatA.Id }, rig.ReadChatIds);
        Assert.Equal(ChatA.Id, rig.ConversationChanges[rig.ConversationChanges.Count - 1]);
    }

    [Fact]
    public void ApplyResult_PicksFirstChat_WhenNotPostedAndNoChoice()
    {
        using var rig = new Rig();

        rig.View.ApplyResult(WithChats(NotPosted(), ChatA, ChatB));

        Assert.Equal(new string?[] { ChatA.Id }, rig.ConversationChanges);
        Assert.Equal(new string?[] { ChatA.Id }, rig.ReadChatIds);
    }

    [Fact]
    public void ApplyResult_KeepsPost_WhenUserChosePost()
    {
        using var rig = new Rig("");

        rig.View.ApplyResult(WithChats(NotPosted(), ChatA));

        Assert.Empty(rig.ConversationChanges);
        Assert.Equal(0, rig.ReadCalls);
    }

    [Fact]
    public void ApplyResult_FallsBackToPost_WhenSelectedChatIsGone()
    {
        using var rig = new Rig("19:gone@thread.v2");

        rig.View.ApplyResult(WithChats(NotPosted(), ChatA));

        Assert.Equal(new string?[] { "" }, rig.ConversationChanges);
        Assert.Equal(new string?[] { null }, rig.ReadChatIds);
    }

    [Fact]
    public void ApplyResult_KeepsChat_WhenChatsIsNull()
    {
        using var rig = new Rig(ChatA.Id);

        rig.View.ApplyResult(NotPosted());

        Assert.Empty(rig.ConversationChanges);
        Assert.Equal(0, rig.ReadCalls);
    }

    [Fact]
    public void BuildThreadMessage_MarksChat_AndUsesChatDeepLinks()
    {
        var chat = new TeamsThread(ChatA.Id, "", new[]
        {
            Message("1792000000001", OtherId, 1, null),
            Message("1792000000002", OtherId, 2, null),
        }, true);

        var message = TeamsThreadView.BuildThreadMessage(Found(chat));

        Assert.True(message["isChat"]!.GetValue<bool>());
        Assert.Equal(TeamsChat.ChatDeepLink(ChatA.Id, "1792000000002"), message["deepLink"]!.GetValue<string>());
        Assert.False(TeamsThreadView.BuildThreadMessage(Found(new TeamsThread(ChannelId, RootId, new[] { Message(RootId, MyId, 0, "MDP-1 x") })))["isChat"]!.GetValue<bool>());
    }

    [Fact]
    public void BuildConversationsMessage_ListsPostThenChats()
    {
        var message = TeamsThreadView.BuildConversationsMessage(new[] { ChatA, ChatB }, ChatB.Id);

        Assert.Equal("conversations", message["type"]!.GetValue<string>());
        var items = message["items"]!.AsArray();
        Assert.Equal(3, items.Count);
        Assert.Null(items[0]!["id"]);
        Assert.Equal("Post on the channel", items[0]!["label"]!.GetValue<string>());
        Assert.Equal(ChatA.Id, items[1]!["id"]!.GetValue<string>());
        Assert.Equal("Chat: " + ChatA.Topic, items[1]!["label"]!.GetValue<string>());
        Assert.Equal(ChatB.Id, message["selected"]!.GetValue<string>());
    }

    [Fact]
    public void SelectChannel_ReadsNow()
    {
        using var rig = new Rig();

        rig.View.HandleMessage(PageMessage(new JsonObject { ["type"] = "selectChannel", ["channelId"] = Alert.Id }).RootElement);

        Assert.Equal(new[] { Alert.Id }, rig.SelectedChannels);
        Assert.Equal(1, rig.ReadCalls);
    }

    [Fact]
    public void PostRequest_FirstWarnsWhenAlreadyPosted_ThenPosts()
    {
        using var rig = new Rig { PostedAt = new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.FromHours(7)) };
        rig.View.ApplyResult(NotPosted());

        rig.View.HandleMessage(PostRequest("<p>mô tả</p>").RootElement);
        Assert.Empty(rig.Posts);
        Assert.Equal("warning", rig.View.LatestMessage("postState")!["kind"]!.GetValue<string>());

        // Other content is warned about again; the same content a second time posts.
        rig.View.HandleMessage(PostRequest("<p>mô tả mới</p>").RootElement);
        Assert.Empty(rig.Posts);
        rig.View.HandleMessage(PostRequest("<p>mô tả mới</p>").RootElement);

        var (subject, draft) = Assert.Single(rig.Posts);
        Assert.Equal("MDP-1 x", subject);
        Assert.Equal("<p>mô tả mới</p>", draft.Html);
    }

    [Fact]
    public void PostRequest_AfterUncertain_WarnsBeforeReposting()
    {
        using var rig = new Rig { PostResult = new TeamsReplyResult(TeamsReplyState.Uncertain, null, "timed out") };
        rig.View.ApplyResult(NotPosted());

        rig.View.HandleMessage(PostRequest("<p>mô tả</p>").RootElement);
        Assert.Single(rig.Posts);
        Assert.Equal("uncertain", rig.View.LatestMessage("postState")!["kind"]!.GetValue<string>());
        Assert.Equal(1, rig.ReadCalls); // a post that may have gone up is looked for right away

        rig.View.HandleMessage(PostRequest("<p>mô tả</p>").RootElement);
        Assert.Single(rig.Posts);
        Assert.Equal("warning", rig.View.LatestMessage("postState")!["kind"]!.GetValue<string>());

        rig.View.HandleMessage(PostRequest("<p>mô tả</p>").RootElement);
        Assert.Equal(2, rig.Posts.Count);
    }

    [Fact]
    public void PostSent_RaisesPostedAndWaits()
    {
        using var rig = new Rig();
        var posted = 0;
        rig.View.Posted += _ => posted++;
        rig.View.ApplyResult(NotPosted());

        rig.View.HandleMessage(PostRequest("<p>mô tả</p>").RootElement);

        Assert.Equal(1, posted);
        Assert.Equal(1, rig.WaitCalls);
        Assert.Equal("sent", rig.View.LatestMessage("postState")!["kind"]!.GetValue<string>());
        Assert.Equal("waitingPost", rig.View.LatestMessage("state")!["kind"]!.GetValue<string>());
    }

    [Fact]
    public void ParseDraft_ReadsHtmlMentionsAndImages()
    {
        var html = "<p>Hi <span data-mention=\"0\">Minh Dinh</span></p><p><img data-image=\"0\"></p>";
        var message = PageMessage(new JsonObject
        {
            ["type"] = "post",
            ["subject"] = " MDP-1 x ",
            ["html"] = html,
            ["mentions"] = new JsonArray(Mention("Minh Dinh", MinhMri, "Minh Dinh (DU2 - Tech Leader)")),
            ["images"] = new JsonArray(Image(@"..\..\shot.png", "data:image/png;base64," + Convert.ToBase64String(PngBytes), 120, 0)),
        });

        var (subject, draft, error) = TeamsThreadView.ParseDraft(message.RootElement);

        Assert.Null(error);
        Assert.Equal(" MDP-1 x ", subject);
        Assert.NotNull(draft);
        Assert.Equal(html, draft.Html);
        Assert.Equal(new TeamsMention("Minh Dinh", MinhMri, "Minh Dinh (DU2 - Tech Leader)"), Assert.Single(draft.Mentions));
        var image = Assert.Single(draft.Images);
        Assert.Equal(PngBytes, image.Bytes);
        Assert.Equal("shot.png", image.FileName);
        Assert.Equal(120, image.Width);
        Assert.Equal(1, image.Height);
    }

    [Fact]
    public void ParseDraft_RejectsHtmlOver2Mb()
    {
        var message = PageMessage(new JsonObject
        {
            ["type"] = "send",
            ["html"] = "<p>" + new string('a', 2 * 1024 * 1024) + "</p>",
            ["mentions"] = new JsonArray(),
            ["images"] = new JsonArray(),
        });

        var (_, draft, error) = TeamsThreadView.ParseDraft(message.RootElement);

        Assert.Null(draft);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("data:image/bmp;base64,Qk0=")]
    [InlineData("data:image/png;base64,@@@")]
    [InlineData("https://as-api.asm.skype.com/v1/objects/0-a/views/imgo")]
    [InlineData("data:image/png;base64,aGVsbG8=")] // "hello": the type says PNG, the bytes do not
    public void ParseDraft_RejectsBadDataUrlOrUnknownType(string dataUrl)
    {
        var message = PageMessage(new JsonObject
        {
            ["type"] = "send",
            ["html"] = "<p>see</p><img data-image=\"0\">",
            ["mentions"] = new JsonArray(),
            ["images"] = new JsonArray(Image("pic.png", dataUrl, 10, 10)),
        });

        var (_, draft, error) = TeamsThreadView.ParseDraft(message.RootElement);

        Assert.Null(draft);
        Assert.NotNull(error);
        Assert.Contains("pic.png", error);
    }

    // The html points at mentions by index, so a bad entry keeps its slot; a non-person mri is blanked and posts as text.
    [Fact]
    public void ParseDraft_KeepsMentionSlots_AndBlanksABadMri()
    {
        var message = PageMessage(new JsonObject
        {
            ["type"] = "send",
            ["html"] = "<p><span data-mention=\"0\">Bot</span> <span data-mention=\"1\">Minh</span></p>",
            ["mentions"] = new JsonArray(
                Mention("Bot", "28:0f8fad5b-d9cb-469f-a165-70867728950e", "Jira Bot"),
                Mention("Minh", MinhMri, "Minh Dinh")),
            ["images"] = new JsonArray(),
        });

        var (_, draft, error) = TeamsThreadView.ParseDraft(message.RootElement);

        Assert.Null(error);
        Assert.NotNull(draft);
        Assert.Equal(new[] { new TeamsMention("Bot", "", "Jira Bot"), new TeamsMention("Minh", MinhMri, "Minh Dinh") }, draft.Mentions);
        Assert.Empty(draft.Images);
    }

    [Fact]
    public void ParseDraft_RejectsImagesOver20MbInAll()
    {
        // Each under the 10 MB limit, 21 MB in all.
        var bytes = new byte[7 * 1024 * 1024];
        PngBytes.CopyTo(bytes, 0);
        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(bytes);
        var message = PageMessage(new JsonObject
        {
            ["type"] = "send",
            ["html"] = "",
            ["mentions"] = new JsonArray(),
            ["images"] = new JsonArray(Image("a.png", dataUrl, 10, 10), Image("b.png", dataUrl, 10, 10), Image("c.png", dataUrl, 10, 10)),
        });

        var (_, draft, error) = TeamsThreadView.ParseDraft(message.RootElement);

        Assert.Null(draft);
        Assert.NotNull(error);
    }

    private static JsonDocument PostRequest(string html)
    {
        return PageMessage(new JsonObject
        {
            ["type"] = "post",
            ["subject"] = "MDP-1 x",
            ["html"] = html,
            ["mentions"] = new JsonArray(),
            ["images"] = new JsonArray(),
        });
    }

    private static JsonDocument PageMessage(JsonObject message)
    {
        return JsonDocument.Parse(message.ToJsonString());
    }

    private static JsonObject Mention(string text, string mri, string displayName)
    {
        return new JsonObject { ["text"] = text, ["mri"] = mri, ["displayName"] = displayName };
    }

    private static JsonObject Image(string name, string dataUrl, int width, int height)
    {
        return new JsonObject { ["name"] = name, ["dataUrl"] = dataUrl, ["width"] = width, ["height"] = height };
    }

    private static TeamsMessage Message(string id, string senderId, int minutes, string? subject)
    {
        return new TeamsMessage(id, senderId, "name", new DateTimeOffset(2026, 10, 14, 9, minutes, 0, TimeSpan.Zero), "text", true, subject);
    }

    private static TeamsThreadResult Found(TeamsThread thread)
    {
        return new TeamsThreadResult(TeamsThreadState.Found, thread, MyId, new[] { PrInTheHole }, ChannelId, null);
    }

    private static TeamsThreadResult NotPosted()
    {
        return new TeamsThreadResult(TeamsThreadState.NotPosted, null, MyId, new[] { PrInTheHole, Alert }, ChannelId, null, new[] { ChannelId });
    }

    private static TeamsThreadResult WithChats(TeamsThreadResult result, params TeamsGroupChat[] chats)
    {
        return result with { Chats = chats };
    }

    private static TeamsThreadResult Failed()
    {
        return new TeamsThreadResult(TeamsThreadState.Failed, null, "", Array.Empty<TeamsChannel>(), null, "boom");
    }
}
