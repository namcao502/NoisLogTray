using System.Text;
using System.Text.Json;

namespace NoisLogTray.Tests;

public class TeamsChatTests
{
    private const string MyId = "8:orgid:5765aa42-84c5-43d9-9bf3-08cee75de0a4";
    private const string OtherId = "11111111-2222-3333-4444-555555555555";
    private const string DeepLink = "https://teams.microsoft.com/l/message/x/1?parentMessageId=1";

    private static object Raw(string id, string content, string composeTime, string messageType = "RichText/Html", string fromId = "8:orgid:5765AA42-84c5-43d9-9bf3-08cee75de0a4", Dictionary<string, string>? properties = null)
    {
        return new Dictionary<string, object>
        {
            ["id"] = id,
            ["messagetype"] = messageType,
            ["contenttype"] = "Text",
            ["content"] = content,
            ["from"] = "https://apac.ng.msg.teams.microsoft.com/v1/users/ME/contacts/" + fromId,
            ["imdisplayname"] = "Minh Dinh",
            ["composetime"] = composeTime,
            ["properties"] = properties ?? new Dictionary<string, string>()
        };
    }

    private static JsonElement MessagesRoot(string? backwardLink, params object[] messages)
    {
        var root = new Dictionary<string, object> { ["messages"] = messages };
        if (backwardLink != null)
        {
            root["_metadata"] = new Dictionary<string, string> { ["backwardLink"] = backwardLink };
        }

        return JsonDocument.Parse(JsonSerializer.Serialize(root)).RootElement;
    }

    private static TeamsMessage ParseOne(string content)
    {
        var page = TeamsChat.ParseMessages(MessagesRoot(null, Raw("1", content, "2026-10-14T08:51:07.4770000Z")));
        return Assert.Single(page.Messages);
    }

    private static string Html(string content)
    {
        return TeamsChat.SafeHtml(ParseOne(content), DeepLink, false);
    }

    private static TeamsMessage Message(string id, string senderId, string time, string? subject = null)
    {
        return new TeamsMessage(id, senderId, "Name", DateTimeOffset.Parse(time), "<p>x</p>", false, subject);
    }

    [Fact]
    public void ParseChannels_ReturnsEveryChannelWithItsTeam()
    {
        var json = """
            {"teams":[
              {"displayName":"MDP (Monster Digital Platform)","channels":[{"id":"19:abc@thread.tacv2","displayName":"PR in the hole"},{"id":"19:def@thread.tacv2","displayName":"General"}]},
              {"displayName":"Other","channels":[{"id":"19:ghi@thread.tacv2","displayName":"Random"}]}
            ],"chats":[]}
            """;

        var channels = TeamsChat.ParseChannels(JsonDocument.Parse(json).RootElement);

        Assert.Equal(3, channels.Count);
        Assert.Equal(new TeamsChannel("19:abc@thread.tacv2", "MDP (Monster Digital Platform)", "PR in the hole"), channels[0]);
        Assert.Equal(new TeamsChannel("19:ghi@thread.tacv2", "Other", "Random"), channels[2]);
    }

    [Fact]
    public void ParseMessages_KeepsHtmlAndTextMessages_DropsSystemAndDeleted()
    {
        var root = MessagesRoot(
            null,
            Raw("1", "<p>html</p>", "2026-10-14T08:00:00Z"),
            Raw("2", "plain", "2026-10-14T08:01:00Z", messageType: "Text"),
            Raw("3", "<p>member added</p>", "2026-10-14T08:02:00Z", messageType: "ThreadActivity/AddMember"),
            Raw("4", "<p>gone</p>", "2026-10-14T08:03:00Z", properties: new Dictionary<string, string> { ["deletetime"] = "1791525886264" }),
            Raw("5", "", "2026-10-14T08:04:00Z"),
            Raw("6", "<p>no time</p>", "not a date"));

        var page = TeamsChat.ParseMessages(root);

        Assert.Equal(new[] { "1", "2" }, page.Messages.Select(tm => tm.Id).ToArray());
    }

    [Fact]
    public void ParseMessages_SortsOldestFirst_WhenThreadComesNewestFirst()
    {
        var root = MessagesRoot(
            null,
            Raw("3", "<p>c</p>", "2026-10-14T08:03:00Z"),
            Raw("2", "<p>b</p>", "2026-10-14T08:02:00Z"),
            Raw("1", "<p>a</p>", "2026-10-14T08:01:00Z"));

        var page = TeamsChat.ParseMessages(root);

        Assert.Equal(new[] { "1", "2", "3" }, page.Messages.Select(tm => tm.Id).ToArray());
    }

    [Fact]
    public void ParseMessages_ReadsSenderIdAndSubject()
    {
        var root = MessagesRoot(
            null,
            Raw("1", "<p>hello</p>", "2026-10-14T08:51:07.4770000Z", properties: new Dictionary<string, string> { ["subject"] = "MDP-9262 Widget" }));

        var message = Assert.Single(TeamsChat.ParseMessages(root).Messages);

        Assert.Equal(MyId, message.SenderId);
        Assert.Equal("Minh Dinh", message.SenderName);
        Assert.Equal("MDP-9262 Widget", message.Subject);
        Assert.Equal(DateTimeOffset.Parse("2026-10-14T08:51:07.477Z"), message.SentAt);
    }

    [Fact]
    public void ParseMessages_MarksPlainTextContent()
    {
        Assert.True(ParseOne("just words").IsPlainText);
        Assert.True(ParseOne("x < y and y > z").IsPlainText);
        Assert.False(ParseOne("<p>words</p>").IsPlainText);
        Assert.False(ParseOne("line<BR>line").IsPlainText);
    }

    [Fact]
    public void ParseMessages_ReturnsBackwardLink_OrNullWhenMissing()
    {
        var message = Raw("1", "<p>a</p>", "2026-10-14T08:00:00Z");

        Assert.Equal("https://example.com/older", TeamsChat.ParseMessages(MessagesRoot("https://example.com/older", message)).BackwardLink);
        Assert.Null(TeamsChat.ParseMessages(MessagesRoot(null, message)).BackwardLink);
    }

    [Fact]
    public void SafeHtml_DropsEventHandlerAttributes()
    {
        var html = Html("<p onclick=\"alert(1)\">hi</p><img src=x onerror=alert(1)>");

        Assert.Equal("<p>hi</p><a class=\"image\" href=\"" + DeepLink + "\">[image]</a>", html);
        Assert.DoesNotContain("onclick", html);
        Assert.DoesNotContain("onerror", html);
    }

    [Fact]
    public void SafeHtml_DropsScriptStyleSvgIframeWithContent()
    {
        var html = Html("<p>a</p><script>alert(1)</script><style>p{}</style><svg><circle/></svg><iframe src=\"x\">inner</iframe><p>b</p>");

        Assert.Equal("<p>a</p><p>b</p>", html);
    }

    [Fact]
    public void SafeHtml_KeepsOnlyHttpLinks()
    {
        Assert.Equal("<p><a href=\"https://a.com/x?y=1&amp;z=2\">go</a></p>", Html("<p><a href=\"https://a.com/x?y=1&amp;z=2\" target=\"_blank\">go</a></p>"));
        Assert.Equal("<a href=\"http://a.com\">go</a>", Html("<a href='http://a.com'>go</a>"));
        Assert.Equal("go", Html("<a href=\"javascript:alert(1)\">go</a>"));
        Assert.Equal("go", Html("<a href=\"data:text/html,x\">go</a>"));
        Assert.Equal("go", Html("<a href=\"//evil.com\">go</a>"));
        Assert.Equal("go", Html("<a href=\"JaVaScRiPt:alert(1)\">go</a>"));
        Assert.Equal("go", Html("<a href=\"java&#x73;cript:alert(1)\">go</a>"));
        Assert.Equal("go", Html("<a href=\"javascript&colon;alert(1)\">go</a>"));
    }

    [Fact]
    public void SafeHtml_RendersMentionEmojiAndImagePlaceholder()
    {
        var html = Html("<p>hi <span itemtype=\"http://schema.skype.com/Mention\" itemscope itemid=\"0\">Minh</span> <img itemtype=\"http://schema.skype.com/Emoji\" alt=\"😊\" src=\"https://e/x.png\"> <img itemtype=\"http://schema.skype.com/AMSImage\" src=\"https://asm.skype.com/x\"> <span style=\"color:red\">plain</span></p>");

        Assert.Equal("<p>hi <span class=\"mention\">Minh</span> 😊 <a class=\"image\" href=\"" + DeepLink + "\">[image]</a> plain</p>", html);
    }

    [Fact]
    public void SafeHtml_RendersAmsImageAsLoadableImg()
    {
        var html = Html("<p><img itemtype=\"http://schema.skype.com/AMSImage\" src=\"https://as-api.asm.skype.com/v1/objects/0-jhb-d6-abc123/views/imgo\" width=\"10\" height=\"10\" alt=\"x\"></p>");

        Assert.Equal("<p><img class=\"ams\" data-src=\"https://as-api.asm.skype.com/v1/objects/0-jhb-d6-abc123/views/imgo\" alt=\"image\"></p>", html);
    }

    [Fact]
    public void SafeHtml_KeepsPlaceholder_ForImageOnAnotherHost()
    {
        var html = Html("<img itemtype=\"http://schema.skype.com/AMSImage\" src=\"https://evil.example.com/v1/objects/abc/views/imgo\">");

        Assert.Equal("<a class=\"image\" href=\"" + DeepLink + "\">[image]</a>", html);
    }

    [Fact]
    public void IsAmsImageUrl_RejectsQueryFragmentTrailingNewlineAndOtherHosts()
    {
        const string good = "https://as-api.asm.skype.com/v1/objects/0-jhb-d6-abc/views/imgo";
        Assert.True(TeamsChat.IsAmsImageUrl(good));
        Assert.False(TeamsChat.IsAmsImageUrl(good + "?x=1"));
        Assert.False(TeamsChat.IsAmsImageUrl(good + "#f"));
        Assert.False(TeamsChat.IsAmsImageUrl(good + "\n"));
        Assert.False(TeamsChat.IsAmsImageUrl("http://as-api.asm.skype.com/v1/objects/abc/views/imgo"));
        Assert.False(TeamsChat.IsAmsImageUrl("https://asm.skype.com.evil.com/v1/objects/abc/views/imgo"));
        Assert.False(TeamsChat.IsAmsImageUrl("https://evil.com/v1/objects/abc/views/imgo"));
    }

    [Fact]
    public void SafeHtml_EscapesPlainTextAndDecodedEntities()
    {
        Assert.Equal("x &lt; y &amp; &quot;z&quot;", TeamsChat.SafeHtml(ParseOne("x < y & \"z\""), DeepLink, false));
        Assert.Equal("<p>&lt;script&gt;alert(1)&lt;/script&gt;</p>", Html("<p>&lt;script&gt;alert(1)&lt;/script&gt;</p>"));
    }

    [Fact]
    public void SafeHtml_ClosesUnbalancedAndMisnestedTags()
    {
        Assert.Equal("<b>x</b>", Html("<b>x"));
        Assert.Equal("<p>a</p>b", Html("<p>a</p></p>b"));
        Assert.Equal("<b><i>y</i></b>z", Html("<b><i>y</b>z"));
        Assert.Equal("a<br>b", Html("a<br/>b"));
        Assert.Equal("<span class=\"mention\">Minh Dinh x</span>",
            Html("<span itemtype=\"http://schema.skype.com/Mention\">Minh <span>Dinh</span> x</span>"));
    }

    [Fact]
    public void NewestOwnTicketPost_IgnoresOthersAndNonTicketSubjects()
    {
        var ownOld = Message("1", MyId, "2026-10-14T08:00:00Z", "MDP-1 a");
        var ownNew = Message("2", MyId, "2026-10-14T09:00:00Z", "  MDP-22 b");
        var ownNotTicket = Message("3", MyId, "2026-10-14T10:00:00Z", "Lunch plans");
        var otherTicket = Message("4", OtherId, "2026-10-14T11:00:00Z", "MDP-3 c");

        Assert.Equal("2", TeamsChat.NewestOwnTicketPost(new[] { ownOld, ownNew, ownNotTicket, otherTicket }, MyId)?.Id);
        Assert.Null(TeamsChat.NewestOwnTicketPost(new[] { ownNotTicket, otherTicket }, MyId));
    }

    [Fact]
    public void NeedsReply_True_WhenLastMessageIsSomeoneElses()
    {
        var thread = new[] { Message("1", MyId, "2026-10-14T08:00:00Z"), Message("2", OtherId, "2026-10-14T09:00:00Z") };

        Assert.True(TeamsChat.NeedsReply(thread, MyId));
    }

    [Fact]
    public void NeedsReply_False_WhenLastMessageIsMine()
    {
        var thread = new[] { Message("1", OtherId, "2026-10-14T08:00:00Z"), Message("2", MyId, "2026-10-14T09:00:00Z") };

        Assert.False(TeamsChat.NeedsReply(thread, MyId));
        Assert.False(TeamsChat.NeedsReply(Array.Empty<TeamsMessage>(), MyId));
    }

    [Fact]
    public void ToPlainText_TurnsBreaksIntoLines_EmojiIntoAlt_ImagesIntoMarker()
    {
        var message = ParseOne("<p>one<br>two &amp; three</p><p>four <img itemtype=\"http://schema.skype.com/Emoji\" alt=\"😊\"></p><ul><li>a</li><li>b</li></ul><p></p><p></p><div><img itemtype=\"http://schema.skype.com/AMSImage\" src=\"https://x\"></div>");

        Assert.Equal("one\ntwo & three\nfour 😊\na\nb\n\n[image]", TeamsChat.ToPlainText(message));
    }

    [Fact]
    public void DeepLink_EscapesChannelId()
    {
        Assert.Equal(
            "https://teams.microsoft.com/l/message/19%3Aabc%40thread.tacv2/179?parentMessageId=179",
            TeamsChat.DeepLink("19:abc@thread.tacv2", "179"));
    }

    private static string Token(string payloadJson)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "header." + payload + ".signature";
    }

    [Fact]
    public void DecodeJwtClaim_ReturnsStringClaim()
    {
        var token = Token("{\"oid\":\"abc-123\",\"exp\":1791525886}");

        Assert.Equal("abc-123", GraphTscClient.DecodeJwtClaim(token, "oid"));
    }

    [Fact]
    public void DecodeJwtClaim_ReturnsNull_ForMalformedTokenOrMissingClaim()
    {
        Assert.Null(GraphTscClient.DecodeJwtClaim("not-a-jwt", "oid"));
        Assert.Null(GraphTscClient.DecodeJwtClaim("a.%%%.c", "oid"));
        Assert.Null(GraphTscClient.DecodeJwtClaim(Token("{\"exp\":1}"), "oid"));
        Assert.Null(GraphTscClient.DecodeJwtClaim(Token("{\"exp\":1}"), "exp"));
    }

    private const string AmsImageUrl = "https://as-api.asm.skype.com/v1/objects/0-jhb-d6-abc123/views/imgo";
    private const string MentionTag = "<span itemtype=\"http://schema.skype.com/Mention\" itemscope itemid=\"{0}\">Minh</span>";

    private static TeamsMessage ParseWithProperties(string content, object properties)
    {
        var page = TeamsChat.ParseMessages(MessagesRoot(null, new Dictionary<string, object>
        {
            ["id"] = "1",
            ["messagetype"] = "RichText/Html",
            ["content"] = content,
            ["from"] = "https://apac.ng.msg.teams.microsoft.com/v1/users/ME/contacts/" + MyId,
            ["composetime"] = "2026-10-14T08:51:07.4770000Z",
            ["properties"] = properties,
        }));
        return Assert.Single(page.Messages);
    }

    private static string MentionsProperty()
    {
        return JsonSerializer.Serialize(new object[]
        {
            new { itemid = "0", mri = "8:orgid:aaaaaaaa-0000-0000-0000-000000000000", mentionType = "person" },
            new { itemid = "1", mri = "19:channel@thread.tacv2", mentionType = "channel" },
            new { itemid = "2", mri = "8:orgid:bbbbbbbb-0000-0000-0000-000000000000" },
            new { itemid = "3", mentionType = "person" },
        });
    }

    private static TeamsMessage EditableCandidate(string content, IReadOnlyDictionary<string, string>? mentionMris = null, bool hasFiles = false)
    {
        return new TeamsMessage("1", MyId, "Name", DateTimeOffset.Parse("2026-10-14T08:00:00Z"), content, !content.Contains('<'), null, null, mentionMris, hasFiles);
    }

    [Fact]
    public void ParseMessage_ReadsEditTimeMentionMrisAndFiles()
    {
        var edited = ParseWithProperties("<p>x</p>", new
        {
            edittime = 1792000000000L,
            mentions = MentionsProperty(),
            files = JsonSerializer.Serialize(new[] { new { id = "f" } }),
        });

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1792000000000L), edited.EditedAt);
        Assert.Equal(new Dictionary<string, string>
        {
            ["0"] = "8:orgid:aaaaaaaa-0000-0000-0000-000000000000",
            ["2"] = "8:orgid:bbbbbbbb-0000-0000-0000-000000000000",
        }, edited.MentionMris);
        Assert.True(edited.HasFiles);

        var plain = ParseWithProperties("<p>x</p>", new { edittime = "1792000000000", files = "[]" });
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1792000000000L), plain.EditedAt);
        Assert.Null(plain.MentionMris);
        Assert.False(plain.HasFiles);

        var untouched = ParseWithProperties("<p>x</p>", new { subject = "s" });
        Assert.Null(untouched.EditedAt);
        Assert.False(untouched.HasFiles);
    }

    [Fact]
    public void ParseMessages_ReadsReactions_AndSkipsBadKeysAndEmptyOnes()
    {
        var reacted = ParseWithProperties("<p>x</p>", new
        {
            emotions = new object[]
            {
                new { key = "like", users = new[] { new { mri = "8:orgid:A", time = 1L, value = "1" }, new { mri = "8:orgid:b", time = 2L, value = "2" } } },
                new { key = "smilingfacewithtear", users = new[] { new { mri = "8:orgid:b", time = 3L, value = "3" } } },
                new { key = "../evil", users = new[] { new { mri = "8:orgid:b", time = 4L, value = "4" } } },
                new { key = "heart", users = Array.Empty<object>() },
            },
        });

        Assert.NotNull(reacted.Reactions);
        Assert.Equal(new[] { "like", "smilingfacewithtear" }, reacted.Reactions.Select(tr => tr.Key));
        Assert.Equal(new[] { "8:orgid:a", "8:orgid:b" }, reacted.Reactions[0].UserMris);

        var plain = ParseWithProperties("<p>x</p>", new { subject = "s" });
        Assert.Null(plain.Reactions);
    }

    [Fact]
    public void EmojiUrl_PointsAtTeamsEmoticonCdn()
    {
        Assert.Equal("https://statics.teams.cdn.office.net/evergreen-assets/personal-expressions/v2/assets/emoticons/like/default/30_f.png",
            TeamsChat.EmojiUrl("like"));
    }

    [Fact]
    public void SafeHtml_ForEditor_EmitsMentionMriAndAmsObjectId()
    {
        var content = "<p>hi " + string.Format(MentionTag, "0") + " " + string.Format(MentionTag, "5").Replace("Minh", "Gone")
            + " <img itemtype=\"http://schema.skype.com/AMSImage\" src=\"" + AmsImageUrl + "\" width=\"40\" height=\"9999\"></p>";
        var message = ParseWithProperties(content, new { mentions = MentionsProperty() });

        var editorHtml = TeamsChat.SafeHtml(message, DeepLink, true);
        var displayHtml = TeamsChat.SafeHtml(message, DeepLink, false);

        Assert.Equal("<p>hi <span class=\"mention\" data-mri=\"8:orgid:aaaaaaaa-0000-0000-0000-000000000000\">Minh</span> Gone "
            + "<img class=\"ams\" data-src=\"" + AmsImageUrl + "\" data-ams=\"0-jhb-d6-abc123\" width=\"40\" height=\"1000\"></p>", editorHtml);
        Assert.Equal("<p>hi <span class=\"mention\">Minh</span> <span class=\"mention\">Gone</span> <img class=\"ams\" data-src=\"" + AmsImageUrl + "\" alt=\"image\"></p>", displayHtml);
        Assert.Equal(new[] { "0-jhb-d6-abc123" }, TeamsChat.AmsObjectIds(content));
    }

    [Fact]
    public void IsEditable_FalseForFilesEmojiTablesAndChannelMentions()
    {
        var personMentions = new Dictionary<string, string> { ["0"] = "8:orgid:aaaaaaaa-0000-0000-0000-000000000000" };
        var amsImage = "<img itemtype=\"http://schema.skype.com/AMSImage\" src=\"" + AmsImageUrl + "\">";

        Assert.True(TeamsChat.IsEditable(EditableCandidate("just text")));
        Assert.True(TeamsChat.IsEditable(EditableCandidate("<p>a <b>b</b> <a href=\"https://x.io\">l</a><br><ul><li>x</li></ul></p><div>d</div>")));
        Assert.True(TeamsChat.IsEditable(EditableCandidate("<p>" + string.Format(MentionTag, "0") + amsImage + "</p>", personMentions)));

        Assert.False(TeamsChat.IsEditable(EditableCandidate("<p>x</p>", null, true)));
        Assert.False(TeamsChat.IsEditable(EditableCandidate("<p>x <img itemtype=\"http://schema.skype.com/Emoji\" alt=\"x\"></p>")));
        Assert.False(TeamsChat.IsEditable(EditableCandidate("<table><tr><td>x</td></tr></table>")));
        Assert.False(TeamsChat.IsEditable(EditableCandidate("<h2>x</h2>")));
        Assert.False(TeamsChat.IsEditable(EditableCandidate("<p><span style=\"color:red\">x</span></p>")));
        Assert.False(TeamsChat.IsEditable(EditableCandidate("<p><img itemtype=\"http://schema.skype.com/AMSImage\" src=\"https://evil.example.com/v1/objects/a/views/imgo\"></p>")));
        Assert.False(TeamsChat.IsEditable(EditableCandidate("<p>" + string.Format(MentionTag, "1") + "</p>", personMentions)));
        Assert.False(TeamsChat.IsEditable(EditableCandidate("<p>" + string.Format(MentionTag, "0") + "</p>")));
    }

    [Fact]
    public void ChannelGroups_RecentFirstMaxThree_ThenOneGroupPerTeam()
    {
        var channels = new List<TeamsChannel>
        {
            new("c1", "Alpha", "One"),
            new("c2", "Beta", "Two"),
            new("c3", "Alpha", "Three"),
            new("c4", "Beta", "Four"),
            new("c5", "Gamma", "Five"),
        };

        var groups = TeamsChat.ChannelGroups(channels, new[] { "c4", "gone", "c1", "c4", "c5", "c2" });

        Assert.Equal(new[] { "Recent", "Alpha", "Beta", "Gamma" }, groups.Select(g => g.Title));
        Assert.Equal(new[] { "c4", "c1", "c5" }, groups[0].Channels.Select(tc => tc.Id));
        Assert.Equal(new[] { "c1", "c3" }, groups[1].Channels.Select(tc => tc.Id));
        Assert.Equal(new[] { "c2", "c4" }, groups[2].Channels.Select(tc => tc.Id));
        Assert.DoesNotContain(TeamsChat.ChannelGroups(channels, Array.Empty<string>()), g => g.Title == "Recent");
    }

    private const string CodeItemId = "codeBlockEditor-6fa364d2-1f6e-449d-a1fe-9ec2fd6c5ad0";
    private const string CodeBlockContent = "<pre class=\"language-sql language-was-manually-selected\" itemid=\"" + CodeItemId + "\" onclick=\"x\"><code>&nbsp;<span class=\"hljs-keyword\">UPDATE</span>&nbsp;a<br><span class=\"hljs-title function_\">f</span></code></pre>";

    [Fact]
    public void SafeHtml_KeepsCodeBlockLanguageAndHljsSpans()
    {
        var expected = "<pre class=\"language-sql language-was-manually-selected\" itemid=\"" + CodeItemId + "\"><code>\u00A0<span class=\"hljs-keyword\">UPDATE</span>\u00A0a<br><span class=\"hljs-title function_\">f</span></code></pre>";

        Assert.Equal(expected, Html(CodeBlockContent));
        Assert.Equal(expected, TeamsChat.SafeHtml(ParseOne(CodeBlockContent), DeepLink, true));
        Assert.Equal("<p>a</p>", Html("<p><span class=\"hljs-keyword\">a</span></p>"));
    }

    [Fact]
    public void SafeHtmlForEditor_ThenBuildBody_KeepsCodeTextLanguageAndItemId()
    {
        var editorHtml = TeamsChat.SafeHtml(ParseOne(CodeBlockContent), DeepLink, true);

        var body = TeamsCompose.BuildBody(editorHtml, new List<TeamsMention>(), new List<(string ObjectId, TeamsImage Image)>(), new HashSet<string>());

        Assert.Equal(TeamsCompose.CodeBlockMarker(CodeItemId) + "<pre class=\"language-sql language-was-manually-selected\" itemid=\"" + CodeItemId + "\"><code>&nbsp;<span class=\"hljs-keyword\">UPDATE</span>&nbsp;a<br><span class=\"hljs-title function_\">f</span></code></pre>", body.Html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SafeHtml_DropsTeamsCodeBlockMarkerParagraph(bool forEditor)
    {
        var content = "<p>a</p>\n<p itemtype=\"http://schema.skype.com/CodeBlockEditor\" id=\"x_" + CodeItemId + "\">\n&nbsp;</p>\n<pre class=\"language-sql\" itemid=\"" + CodeItemId + "\"><code>x</code></pre>";

        var html = TeamsChat.SafeHtml(EditableCandidate(content), "https://teams.example/deep", forEditor);

        Assert.DoesNotContain("CodeBlockEditor\"", html);
        Assert.DoesNotContain("x_codeBlockEditor", html);
        Assert.Contains("<pre class=\"language-sql\" itemid=\"" + CodeItemId + "\">", html);
    }

    [Fact]
    public void IsEditable_TrueForMessageWithCodeBlock()
    {
        Assert.True(TeamsChat.IsEditable(EditableCandidate("<p>x</p>" + CodeBlockContent)));
        Assert.False(TeamsChat.IsEditable(EditableCandidate("<p><span class=\"hljs-keyword\">x</span></p>")));
    }

    private static string AmsImg(string url)
    {
        return $"<img itemtype=\"http://schema.skype.com/AMSImage\" src=\"{url}\">";
    }

    private const string ChatListJson = """
        {"conversations":[
          {"id":"19:aaa@thread.v2","threadProperties":{"topic":"  MDP-9126 Release Cart  "}},
          {"id":"19:meeting_xyz@thread.v2","threadProperties":{"topic":"Weekly sync"}},
          {"id":"19:one@unq.gbl.spaces","threadProperties":{}},
          {"id":"19:chan@thread.tacv2","threadProperties":{"topic":"General"}},
          {"id":"19:bbb@thread.v2","threadProperties":{"topic":"   "}},
          {"id":"19:ccc@thread.v2","threadProperties":{"topic":"mdp-912 other"}}
        ],"_metadata":{"backwardLink":"https://teams.test/next?syncState=1"}}
        """;

    [Fact]
    public void ParseChats_KeepsGroupChatsWithTopic_WhenListHasMeetingsOneToOneAndChannels()
    {
        var (chats, _) = TeamsChat.ParseChats(JsonDocument.Parse(ChatListJson).RootElement);

        Assert.Equal(new[] { "19:aaa@thread.v2", "19:ccc@thread.v2" }, chats.Select(tgc => tgc.Id).ToArray());
        Assert.Equal("MDP-9126 Release Cart", chats[0].Topic);
    }

    [Fact]
    public void ParseChats_ReturnsBackwardLink()
    {
        var (_, backwardLink) = TeamsChat.ParseChats(JsonDocument.Parse(ChatListJson).RootElement);

        Assert.Equal("https://teams.test/next?syncState=1", backwardLink);
    }

    [Fact]
    public void FindTicketChats_MatchesTopicStartingWithKey_CaseInsensitive()
    {
        var chats = new[] { new TeamsGroupChat("a", "  mdp-9126 Release"), new TeamsGroupChat("a", "MDP-9126 dup"), new TeamsGroupChat("b", "MDP-9126") };

        var found = TeamsChat.FindTicketChats(chats, "MDP-9126");

        Assert.Equal(new[] { "a", "b" }, found.Select(tgc => tgc.Id).ToArray());
    }

    [Fact]
    public void FindTicketChats_SkipsLongerKey_WhenNextCharIsDigit()
    {
        var found = TeamsChat.FindTicketChats(new[] { new TeamsGroupChat("a", "MDP-9126 x") }, "MDP-912");

        Assert.Empty(found);
    }

    [Fact]
    public void FindTicketChats_SkipsTopicWithKeyInTheMiddle()
    {
        var found = TeamsChat.FindTicketChats(new[] { new TeamsGroupChat("a", "Re: MDP-9126 x") }, "MDP-9126");

        Assert.Empty(found);
    }

    [Fact]
    public void ConversationKey_UsesChatId_ForChat()
    {
        var thread = new TeamsThread("19:aaa@thread.v2", "", new List<TeamsMessage>(), true);

        Assert.Equal("19:aaa@thread.v2", TeamsChat.ConversationKey(thread));
    }

    [Fact]
    public void ConversationKey_UsesChannelAndRoot_ForPost()
    {
        var thread = new TeamsThread("19:ch@thread.tacv2", "100", new List<TeamsMessage>());

        Assert.Equal("19:ch@thread.tacv2;messageid=100", TeamsChat.ConversationKey(thread));
    }

    [Fact]
    public void MentionsMe_True_WhenMentionMrisHoldsMyId()
    {
        var mentioned = Message("1", OtherId, "2026-10-14T08:00:00Z") with
        {
            MentionMris = new Dictionary<string, string> { ["0"] = MyId.ToUpperInvariant() }
        };

        Assert.True(TeamsChat.MentionsMe(mentioned, MyId));
        Assert.False(TeamsChat.MentionsMe(Message("2", OtherId, "2026-10-14T08:00:00Z"), MyId));
    }

    [Fact]
    public void ImageUrls_ReturnsAmsImagesInOrder()
    {
        var first = "https://as-api.asm.skype.com/v1/objects/0-jhb-d4-aaa/views/imgo";
        var second = "https://as-api.asm.skype.com/v1/objects/0-jhb-d4-bbb/views/imgo";
        var content = AmsImg(first) + AmsImg(second) + AmsImg(first) + "<img src=\"https://evil.test/x.png\">";
        var message = Message("1", OtherId, "2026-10-14T08:00:00Z") with { Content = content };

        Assert.Equal(new[] { first, second }, TeamsChat.ImageUrls(message).ToArray());
    }
}
