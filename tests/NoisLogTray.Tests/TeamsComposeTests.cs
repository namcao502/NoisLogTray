using System.Text.Json;
using System.Text.RegularExpressions;

namespace NoisLogTray.Tests;

public class TeamsComposeTests
{
    private const string MinhMri = "8:orgid:11111111-2222-3333-4444-555555555555";
    private const string LanMri = "8:orgid:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string MentionType = "http://schema.skype.com/Mention";

    private static readonly IReadOnlyList<(string ObjectId, TeamsImage Image)> NoImages = new List<(string ObjectId, TeamsImage Image)>();

    private static TeamsMessage Message(string senderId, string senderName, string sentAt)
    {
        return new TeamsMessage(Guid.NewGuid().ToString(), senderId, senderName, DateTimeOffset.Parse(sentAt), "<p>x</p>", false, null);
    }

    private static readonly IReadOnlySet<string> NoKeptAmsIds = new HashSet<string>();

    private static TeamsMessageBody Build(string html, params TeamsMention[] mentions)
    {
        return TeamsCompose.BuildBody(html, mentions, NoImages, NoKeptAmsIds);
    }

    private static string Span(int itemId, string text)
    {
        return $"<span itemtype=\"{MentionType}\" itemscope itemid=\"{itemId}\">{text}</span>";
    }

    [Fact]
    public void ShortName_DropsTrailingParenthesis()
    {
        Assert.Equal("Minh Dinh", TeamsCompose.ShortName("Minh Dinh (Monster Digital)"));
        Assert.Equal("Nam", TeamsCompose.ShortName("Nam  (Ext)  "));
    }

    [Fact]
    public void ShortName_KeepsNameWithoutParenthesis()
    {
        Assert.Equal("Minh Dinh", TeamsCompose.ShortName("Minh Dinh"));
        Assert.Equal("(Ext)", TeamsCompose.ShortName("(Ext)"));
    }

    [Fact]
    public void People_DedupesBySender_NewestNameFirst_WithoutMeOrBots()
    {
        var myMri = "8:orgid:5765aa42-84c5-43d9-9bf3-08cee75de0a4";
        var messages = new[]
        {
            Message(MinhMri, "Minh Old", "2026-10-14T08:00:00Z"),
            Message(LanMri, "Lan", "2026-10-14T09:00:00Z"),
            Message(MinhMri, "Minh New", "2026-10-14T10:00:00Z"),
            Message(myMri, "Me", "2026-10-14T11:00:00Z"),
            Message("28:bot-id", "Some Bot", "2026-10-14T12:00:00Z"),
            Message("8:orgid:99999999-2222-3333-4444-555555555555", "", "2026-10-14T13:00:00Z")
        };

        var people = TeamsCompose.People(messages, myMri);

        Assert.Equal(new[] { new TeamsPerson(MinhMri, "Minh New"), new TeamsPerson(LanMri, "Lan") }, people);
    }

    [Fact]
    public void ImageProblem_AcceptsPngJpegGif_RejectsEmptyOtherAndOversize()
    {
        Assert.Null(TeamsCompose.ImageProblem(new TeamsImage(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1 }, "a.png", 1, 1)));
        Assert.Null(TeamsCompose.ImageProblem(new TeamsImage(new byte[] { 0xFF, 0xD8, 0xFF, 1 }, "a.jpg", 1, 1)));
        Assert.Null(TeamsCompose.ImageProblem(new TeamsImage(new byte[] { 0x47, 0x49, 0x46, 0x38, 1 }, "a.gif", 1, 1)));
        Assert.Equal(TeamsImageProblem.Empty, TeamsCompose.ImageProblem(new TeamsImage(Array.Empty<byte>(), "a.png", 1, 1)));
        Assert.Equal(TeamsImageProblem.NotAnImage, TeamsCompose.ImageProblem(new TeamsImage(new byte[] { 1, 2, 3, 4 }, "a.txt", 1, 1)));

        var oversize = new byte[TeamsCompose.MaxImageBytes + 1];
        oversize[0] = 0x89;
        Assert.Equal(TeamsImageProblem.TooLarge, TeamsCompose.ImageProblem(new TeamsImage(oversize, "big.png", 1, 1)));
    }

    [Fact]
    public void DisplaySize_ScalesLongestSideTo1000_KeepingRatio()
    {
        Assert.Equal((1000, 500), TeamsCompose.DisplaySize(2000, 1000));
        Assert.Equal((500, 1000), TeamsCompose.DisplaySize(1000, 2000));
        Assert.Equal((300, 200), TeamsCompose.DisplaySize(300, 200));
        Assert.Equal((1000, 1), TeamsCompose.DisplaySize(5000, 1));
    }

    private static string Img(string objectId, int width, int height)
    {
        return $"<img itemtype=\"http://schema.skype.com/AMSImage\" src=\"https://as-api.asm.skype.com/v1/objects/{objectId}/views/imgo\" width=\"{width}\" height=\"{height}\" id=\"{objectId}\" itemscope=\"\" alt=\"image\">";
    }

    [Fact]
    public void BuildBody_KeepsAllowedFormatting_AndStripsStylesScriptsAndUnknownTags()
    {
        var body = Build("<p style=\"color:red\" onclick=\"x()\">a <strong>b</strong> <em>c</em></p><script>alert(1)</script><h1>head</h1><div>d</div>"
            + "<a href=\"https://x.io/y?a=1&amp;b=2\" target=\"_blank\">link</a><ul><li>one</li></ul><br>");

        Assert.Equal("<p>a <strong>b</strong> <em>c</em></p>head<p>d</p>"
            + "<a href=\"https://x.io/y?a=1&amp;b=2\" title=\"https://x.io/y?a=1&amp;b=2\">link</a><ul><li>one</li></ul><br>", body.Html);
        Assert.Empty(body.AmsReferences);
        Assert.Null(body.MentionsJson);
    }

    [Fact]
    public void BuildBody_EscapesText_AndClosesUnbalancedTags()
    {
        var body = Build("<p>a &lt; b <b>bold");

        Assert.Equal("<p>a &lt; b <b>bold</b></p>", body.Html);
    }

    [Fact]
    public void BuildBody_TurnsMentionMarkersIntoTeamsMentions_InOrder()
    {
        var body = Build(
            "<p>Hi <span data-mention=\"1\">Lan</span> and <span data-mention=\"0\">Minh</span> <span data-mention=\"9\">x</span> "
            + "<span data-mention=\"2\">Bot</span> <span data-mention=\"0\">Minh</span></p>",
            new TeamsMention("Minh", MinhMri, "Minh Dinh"),
            new TeamsMention("Lan", LanMri, "Lan Tran"),
            new TeamsMention("Bot", "28:bot", "Bot"));

        Assert.Equal($"<p>Hi {Span(0, "Lan")} and {Span(1, "Minh")} x Bot {Span(2, "Minh")}</p>", body.Html);
        var mentions = JsonDocument.Parse(body.MentionsJson!).RootElement;
        Assert.Equal(new[] { LanMri, MinhMri, MinhMri }, mentions.EnumerateArray().Select(je => je.GetProperty("mri").GetString()));
        Assert.Equal("1", mentions[1].GetProperty("itemid").GetString());
        Assert.Equal(MentionType, mentions[1].GetProperty("@type").GetString());
        Assert.Equal("person", mentions[1].GetProperty("mentionType").GetString());
        Assert.Equal("Minh Dinh", mentions[1].GetProperty("displayName").GetString());
    }

    [Fact]
    public void BuildBody_PlacesUploadedImagesAtTheirMarkers_AndAppendsTheRest()
    {
        var uploaded = new List<(string ObjectId, TeamsImage Image)>
        {
            ("0-jhb-d6-aaaa", new TeamsImage(new byte[] { 1 }, "a.png", 2000, 1000)),
            ("0-jhb-d6-bbbb", new TeamsImage(new byte[] { 1 }, "b.png", 30, 20))
        };

        var body = TeamsCompose.BuildBody("<p>a <img data-image=\"1\"> b</p><img data-image=\"1\"><img data-image=\"7\">",
            new List<TeamsMention>(), uploaded, NoKeptAmsIds);

        Assert.Equal("<p>a " + Img("0-jhb-d6-bbbb", 30, 20) + " b</p><p>" + Img("0-jhb-d6-aaaa", 1000, 500) + "</p>", body.Html);
        Assert.Equal(new[] { "0-jhb-d6-bbbb", "0-jhb-d6-aaaa" }, body.AmsReferences);
    }

    [Fact]
    public void BuildBody_KeepsOnlyAmsIdsOfTheEditedMessage()
    {
        var kept = new HashSet<string> { "keep-1" };

        var body = TeamsCompose.BuildBody(
            "<p><img data-ams=\"keep-1\" width=\"300\" height=\"2000\"><img data-ams=\"other-2\" width=\"10\" height=\"10\"><img data-ams=\"keep-1\" width=\"5\" height=\"5\"></p>",
            new List<TeamsMention>(), NoImages, kept);

        Assert.Equal("<p>" + Img("keep-1", 300, 1000) + Img("keep-1", 5, 5) + "</p>", body.Html);
        Assert.Equal(new[] { "keep-1" }, body.AmsReferences);
    }

    [Fact]
    public void BuildBody_DropsJavascriptLinks()
    {
        var body = Build("<p><a href=\"javascript:alert(1)\">click</a> <a href=\"  https://ok.io \">ok</a> <a>none</a></p>");

        Assert.Equal("<p>click <a href=\"https://ok.io\" title=\"https://ok.io\">ok</a> none</p>", body.Html);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("<p><br></p>", true)]
    [InlineData("<p>&nbsp; </p><p></p>", true)]
    [InlineData("<p>x</p>", false)]
    [InlineData("<p><img data-image=\"0\"></p>", false)]
    [InlineData("<img data-ams=\"a\">", false)]
    public void IsEmptyHtml_TrueForBlankParagraphs(string html, bool expected)
    {
        Assert.Equal(expected, TeamsCompose.IsEmptyHtml(html));
    }

    private const string CodeItemId = "codeBlockEditor-6fa364d2-1f6e-449d-a1fe-9ec2fd6c5ad0";

    [Fact]
    public void BuildBody_KeepsTeamsCodeBlock_WithLanguageItemIdAndHljsSpans()
    {
        var html = "<pre class=\"language-sql language-was-manually-selected\" itemid=\"" + CodeItemId + "\" style=\"x\"><code>&nbsp;<span class=\"hljs-keyword\">UPDATE</span>&nbsp;a<br><span class=\"hljs-title function_\">f</span></code></pre>";

        var body = Build(html);

        Assert.Equal(TeamsCompose.CodeBlockMarker(CodeItemId) + "<pre class=\"language-sql language-was-manually-selected\" itemid=\"" + CodeItemId + "\"><code>&nbsp;<span class=\"hljs-keyword\">UPDATE</span>&nbsp;a<br><span class=\"hljs-title function_\">f</span></code></pre>", body.Html);
    }

    [Fact]
    public void BuildBody_AddsItemId_WhenCodeBlockHasNone()
    {
        var body = Build("<pre class=\"language-sql\" itemid=\"bad\"><code>x</code></pre>");

        Assert.Matches("^<p itemtype=\"http://schema.skype.com/CodeBlockEditor\" id=\"x_(codeBlockEditor-[0-9a-f-]{36})\">&nbsp;</p><pre class=\"language-sql\" itemid=\"\\1\"><code>x</code></pre>$", body.Html);
    }

    [Fact]
    public void BuildBody_WritesTeamsMarkerOnlyBeforeACodeBlockWithAnItemId()
    {
        Assert.Equal("<pre><code>x</code></pre>", Build("<pre><code>x</code></pre>").Html);
        Assert.Equal("<p>a</p>" + TeamsCompose.CodeBlockMarker(CodeItemId) + "<pre class=\"language-sql\" itemid=\"" + CodeItemId + "\"><code>x</code></pre>",
            Build("<p>a</p><pre class=\"language-sql\" itemid=\"" + CodeItemId + "\"><code>x</code></pre>").Html);
    }

    [Fact]
    public void BuildBody_GivesADuplicateCodeBlockItemIdANewOne()
    {
        const string itemId = "codeBlockEditor-6fa364d2-1f6e-449d-a1fe-9ec2fd6c5ad0";
        var block = $"<pre class=\"language-sql\" itemid=\"{itemId}\"><code>x</code></pre>";

        var body = Build(block + block);

        var itemIds = Regex.Matches(body.Html, "itemid=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(2, itemIds.Count);
        Assert.Equal(itemId, itemIds[0]);
        Assert.NotEqual(itemId, itemIds[1]);
        Assert.Matches("^codeBlockEditor-[0-9a-f-]{36}$", itemIds[1]);
    }

    [Fact]
    public void BuildBody_DropsTrailingEmptyParagraphs()
    {
        Assert.Equal("<p>a</p><pre><code>x</code></pre>", Build("<p>a</p><pre><code>x</code></pre><p><br></p><p></p>").Html);
        Assert.Equal("<p><br></p><p>a</p>", Build("<p><br></p><p>a</p>").Html);
    }

    [Fact]
    public void BuildBody_DropsBadPreClassAndNonHljsSpanClass()
    {
        Assert.Equal("<pre><code>a</code></pre>", Build("<pre class=\"language-was-manually-selected\"><code>a</code></pre>").Html);
        Assert.Equal("<pre><code>a</code></pre>", Build("<pre class=\"language-sql language-js\"><code>a</code></pre>").Html);
        Assert.Equal("<pre><code>a</code></pre>", Build("<pre class=\"evil language-sql\"><code>a</code></pre>").Html);
        Assert.Equal("<pre><code>a b c</code></pre>", Build("<pre><code><span class=\"foo\">a</span> <span>b</span> <span class=\"hljs-x evil!\">c</span></code></pre>").Html);
        Assert.Equal("<p>a b</p>", Build("<p><span class=\"hljs-keyword\">a</span> b</p>").Html);
    }

    [Fact]
    public void BuildBody_WritesNbspInsideCodeBlock()
    {
        Assert.Equal("<pre><code>a&nbsp;b</code></pre><p>c\u00A0d</p>", Build("<pre><code>a\u00A0b</code></pre><p>c&nbsp;d</p>").Html);
    }
}
