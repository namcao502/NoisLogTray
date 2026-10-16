using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NoisLogTray;

// Parses the Teams chat service's JSON and does the pure operations on a ticket's post and thread.
internal static class TeamsChat
{
    private static readonly Regex KnownHtmlTag = new(
        @"<(p|div|span|br|a|img|pre|code|b|strong|i|em|u|s|ul|ol|li|blockquote|table|h[1-6])\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static readonly Regex TagPattern = new(
        @"<(?<close>/?)(?<name>[a-zA-Z][a-zA-Z0-9]*)(?<attributes>(?:""[^""]*""|'[^']*'|[^>""'])*)>",
        RegexOptions.Compiled);

    private static readonly Regex AttributePattern = new(
        @"(?<name>[a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s""'>]+))",
        RegexOptions.Compiled);

    // A reaction key goes into the emoji CDN's path, so only Teams' own key shape gets through.
    private static readonly Regex ReactionKey = new(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.Compiled);

    internal const string EmojiCdnOrigin = "https://statics.teams.cdn.office.net";

    private static readonly Regex AmsImageUrl = new(
        @"^https://[a-z0-9-]+\.asm\.skype\.com/v1/objects/[A-Za-z0-9-]+/views/imgo\z",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AmsObjectIdInUrl = new(@"/v1/objects/(?<id>[A-Za-z0-9-]+)/views/imgo\z", RegexOptions.Compiled);
    private static readonly Regex TicketSubjectStart = new(@"^\s*MDP-\d+", RegexOptions.Compiled);
    private static readonly Regex SpacesBeforeNewLine = new(@"[ \t]+\n", RegexOptions.Compiled);
    private static readonly Regex BlankLineRun = new(@"\n{3,}", RegexOptions.Compiled);

    private static readonly HashSet<string> AllowedTags = new(StringComparer.Ordinal)
    {
        "p", "div", "b", "strong", "i", "em", "u", "s", "strike", "ul", "ol", "li", "pre", "code", "blockquote",
        "table", "thead", "tbody", "tr", "th", "td", "h1", "h2", "h3"
    };

    internal static readonly HashSet<string> DroppedWithContentTags = new(StringComparer.Ordinal)
    {
        "script", "style", "iframe", "object", "embed", "svg", "math", "template"
    };

    internal static List<TeamsChannel> ParseChannels(JsonElement root)
    {
        var channels = new List<TeamsChannel>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("teams", out var teams) || teams.ValueKind != JsonValueKind.Array)
        {
            return channels;
        }

        foreach (var team in teams.EnumerateArray())
        {
            var teamName = GetString(team, "displayName") ?? "";
            if (team.ValueKind != JsonValueKind.Object || !team.TryGetProperty("channels", out var teamChannels) || teamChannels.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var channel in teamChannels.EnumerateArray())
            {
                var channelId = GetString(channel, "id");
                if (string.IsNullOrEmpty(channelId))
                {
                    continue;
                }

                channels.Add(new TeamsChannel(channelId, teamName, GetString(channel, "displayName") ?? ""));
            }
        }

        return channels;
    }

    // Group chats only: a "@thread.v2" id that is not a meeting. A chat without a topic cannot be matched to a ticket.
    internal static (List<TeamsGroupChat> Chats, string? BackwardLink) ParseChats(JsonElement root)
    {
        var chats = new List<TeamsGroupChat>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            return (chats, null);
        }

        if (root.TryGetProperty("conversations", out var conversations) && conversations.ValueKind == JsonValueKind.Array)
        {
            foreach (var conversation in conversations.EnumerateArray())
            {
                var id = GetString(conversation, "id");
                var isGroupChat = id != null && id.EndsWith("@thread.v2", StringComparison.Ordinal) && !id.StartsWith("19:meeting_", StringComparison.Ordinal);
                if (!isGroupChat || !conversation.TryGetProperty("threadProperties", out var threadProperties))
                {
                    continue;
                }

                var topic = (GetString(threadProperties, "topic") ?? "").Trim();
                if (topic.Length != 0)
                {
                    chats.Add(new TeamsGroupChat(id!, topic));
                }
            }
        }

        string? backwardLink = null;
        if (root.TryGetProperty("_metadata", out var metadata))
        {
            backwardLink = GetString(metadata, "backwardLink");
        }

        return (chats, backwardLink);
    }

    internal static List<TeamsGroupChat> FindTicketChats(IEnumerable<TeamsGroupChat> chats, string ticketKey)
    {
        var found = new List<TeamsGroupChat>();
        foreach (var chat in chats)
        {
            var topic = chat.Topic.TrimStart();
            var startsWithKey = topic.StartsWith(ticketKey, StringComparison.OrdinalIgnoreCase);
            var nextIsDigit = topic.Length > ticketKey.Length && char.IsAsciiDigit(topic[ticketKey.Length]);
            if (startsWithKey && !nextIsDigit && !found.Exists(tgc => tgc.Id == chat.Id))
            {
                found.Add(chat);
            }
        }

        return found;
    }

    internal static string ChatDeepLink(string chatId, string messageId)
    {
        return $"https://teams.microsoft.com/l/message/{Uri.EscapeDataString(chatId)}/{messageId}?context=%7B%22contextType%22%3A%22chat%22%7D";
    }

    // The key of the parse cache and of the seen / notified marks.
    internal static string ConversationKey(TeamsThread thread)
    {
        return thread.IsChat ? thread.ChannelId : thread.ChannelId + ";messageid=" + thread.RootId;
    }

    // mentions may spell the mri in another case than the sniffed id.
    internal static bool MentionsMe(TeamsMessage message, string myId)
    {
        return message.MentionMris != null && message.MentionMris.Values.Any(mri => string.Equals(mri, myId, StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<string> ImageUrls(TeamsMessage message)
    {
        var urls = new List<string>();
        foreach (Match tag in TagPattern.Matches(message.Content))
        {
            var isImage = tag.Groups["close"].Length == 0 && tag.Groups["name"].Value.Equals("img", StringComparison.OrdinalIgnoreCase);
            if (isImage && IsAmsImage(tag.Groups["attributes"].Value, out var imageUrl) && !urls.Contains(imageUrl))
            {
                urls.Add(imageUrl);
            }
        }

        return urls;
    }

    internal static TeamsMessagePage ParseMessages(JsonElement root)
    {
        var messages = new List<TeamsMessage>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("messages", out var rawMessages) && rawMessages.ValueKind == JsonValueKind.Array)
        {
            foreach (var rawMessage in rawMessages.EnumerateArray())
            {
                var message = ParseMessage(rawMessage);
                if (message != null)
                {
                    messages.Add(message);
                }
            }
        }

        string? backwardLink = null;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("_metadata", out var metadata))
        {
            backwardLink = GetString(metadata, "backwardLink");
        }

        return new TeamsMessagePage(messages.OrderBy(tm => tm.SentAt).ToList(), backwardLink);
    }

    internal static TeamsMessage? NewestOwnTicketPost(IEnumerable<TeamsMessage> messages, string myId)
    {
        return messages
            .Where(tm => string.Equals(tm.SenderId, myId, StringComparison.Ordinal) && tm.Subject != null && TicketSubjectStart.IsMatch(tm.Subject))
            .OrderByDescending(tm => tm.SentAt)
            .FirstOrDefault();
    }

    internal static bool NeedsReply(IReadOnlyList<TeamsMessage> thread, string myId)
    {
        if (thread.Count == 0)
        {
            return false;
        }

        return !string.Equals(thread[thread.Count - 1].SenderId, myId, StringComparison.Ordinal);
    }

    // For the Claude prompt, not for display, so it only needs to read well.
    internal static string ToPlainText(TeamsMessage message)
    {
        if (message.IsPlainText)
        {
            return message.Content.Trim();
        }

        var text = TagPattern.Replace(message.Content, tag =>
        {
            var name = tag.Groups["name"].Value.ToLowerInvariant();
            var isClosing = tag.Groups["close"].Length != 0;
            if (name == "br" || (isClosing && (name == "p" || name == "div" || name == "li")))
            {
                return "\n";
            }

            if (name == "img" && !isClosing)
            {
                return IsEmoji(tag.Groups["attributes"].Value) ? GetAttribute(tag.Groups["attributes"].Value, "alt") ?? "" : "[image]";
            }

            return "";
        });

        text = WebUtility.HtmlDecode(text);
        text = SpacesBeforeNewLine.Replace(text, "\n");
        return BlankLineRun.Replace(text, "\n\n").Trim();
    }

    // The only HTML the page ever renders: built from tags this method writes and escaped text, never copied from the message.
    // forEditor keeps what the composer needs to send the message back: a mention's mri, an image's object id and size.
    internal static string SafeHtml(TeamsMessage message, string deepLink, bool forEditor)
    {
        if (message.IsPlainText)
        {
            return Escape(message.Content);
        }

        var content = message.Content;
        var html = new StringBuilder();
        var openTags = new List<(string Name, bool Emitted)>();
        var cursor = 0;
        foreach (Match tag in TagPattern.Matches(content))
        {
            if (tag.Index < cursor)
            {
                continue;
            }

            html.Append(Escape(WebUtility.HtmlDecode(content.Substring(cursor, tag.Index - cursor))));
            cursor = tag.Index + tag.Length;

            var name = tag.Groups["name"].Value.ToLowerInvariant();
            var attributes = tag.Groups["attributes"].Value;
            if (tag.Groups["close"].Length != 0)
            {
                CloseTag(html, openTags, name);
            }
            else if (DroppedWithContentTags.Contains(name))
            {
                if (!attributes.TrimEnd().EndsWith('/'))
                {
                    cursor = IndexAfterClosingTag(content, cursor, name);
                }
            }
            else if (name == "p" && (GetAttribute(attributes, "itemtype") ?? "").Contains(TeamsCompose.CodeBlockMarkerItemType, StringComparison.OrdinalIgnoreCase))
            {
                // Teams' code-block marker paragraph: only an empty line on screen, and BuildBody writes it again on send.
                cursor = IndexAfterClosingTag(content, cursor, name);
            }
            else
            {
                AppendOpenTag(html, openTags, name, attributes, deepLink, forEditor ? message.MentionMris : null, forEditor);
            }
        }

        if (cursor < content.Length)
        {
            html.Append(Escape(WebUtility.HtmlDecode(content.Substring(cursor))));
        }

        CloseFrom(html, openTags, 0);
        return html.ToString();
    }

    // True when the composer can send this message back without losing anything: no files, only tags BuildBody keeps, only person mentions.
    internal static bool IsEditable(TeamsMessage message)
    {
        if (message.HasFiles)
        {
            return false;
        }

        if (message.IsPlainText)
        {
            return true;
        }

        var openCodeBlocks = 0;
        foreach (Match tag in TagPattern.Matches(message.Content))
        {
            var name = tag.Groups["name"].Value.ToLowerInvariant();
            if (tag.Groups["close"].Length != 0)
            {
                if (name == "pre" && openCodeBlocks != 0)
                {
                    openCodeBlocks--;
                }

                continue;
            }

            var attributes = tag.Groups["attributes"].Value;
            if (name == "pre")
            {
                openCodeBlocks++;
            }

            if (name == "span")
            {
                var itemType = GetAttribute(attributes, "itemtype") ?? "";
                var isMention = itemType.Contains("schema.skype.com/Mention", StringComparison.OrdinalIgnoreCase);
                var hasMri = message.MentionMris != null && message.MentionMris.ContainsKey(GetAttribute(attributes, "itemid") ?? "");
                var isHighlight = openCodeBlocks != 0 && TeamsCompose.CodeSpanClass(attributes) != null;
                if (!isHighlight && (!isMention || !hasMri))
                {
                    return false;
                }
            }
            else if (name == "img")
            {
                if (!IsAmsImage(attributes, out _))
                {
                    return false;
                }
            }
            else if (!TeamsCompose.EditableTags.Contains(name))
            {
                return false;
            }
        }

        return true;
    }

    internal static IReadOnlySet<string> AmsObjectIds(string content)
    {
        var objectIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match tag in TagPattern.Matches(content))
        {
            var isImage = tag.Groups["close"].Length == 0 && tag.Groups["name"].Value.Equals("img", StringComparison.OrdinalIgnoreCase);
            if (isImage && IsAmsImage(tag.Groups["attributes"].Value, out var imageUrl))
            {
                objectIds.Add(AmsObjectId(imageUrl));
            }
        }

        return objectIds;
    }

    // "Recent" first (at most three, in the given order), then one group per team in the order the teams appear.
    internal static IReadOnlyList<(string Title, IReadOnlyList<TeamsChannel> Channels)> ChannelGroups(
        IReadOnlyList<TeamsChannel> channels, IReadOnlyList<string> recentIds)
    {
        var groups = new List<(string Title, IReadOnlyList<TeamsChannel> Channels)>();
        var recentChannels = recentIds
            .Distinct()
            .Select(ri => channels.FirstOrDefault(tc => tc.Id == ri))
            .OfType<TeamsChannel>()
            .Take(TeamsThreadService.MaxRecentChannels)
            .ToList();
        if (recentChannels.Count != 0)
        {
            groups.Add(("Recent", recentChannels));
        }

        foreach (var teamChannels in channels.GroupBy(tc => tc.TeamName))
        {
            groups.Add((teamChannels.Key, teamChannels.ToList()));
        }

        return groups;
    }

    private static string AmsObjectId(string imageUrl)
    {
        return AmsObjectIdInUrl.Match(imageUrl).Groups["id"].Value;
    }

    internal static string EmojiUrl(string key)
    {
        return $"{EmojiCdnOrigin}/evergreen-assets/personal-expressions/v2/assets/emoticons/{key}/default/30_f.png";
    }

    internal static string DeepLink(string channelId, string rootId)
    {
        return $"https://teams.microsoft.com/l/message/{Uri.EscapeDataString(channelId)}/{rootId}?parentMessageId={rootId}";
    }

    private static TeamsMessage? ParseMessage(JsonElement raw)
    {
        var messageType = GetString(raw, "messagetype");
        if (messageType != "RichText/Html" && messageType != "Text")
        {
            return null;
        }

        var id = GetString(raw, "id");
        var content = GetString(raw, "content");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(content))
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(GetString(raw, "composetime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var sentAt))
        {
            return null;
        }

        string? subject = null;
        DateTimeOffset? editedAt = null;
        IReadOnlyDictionary<string, string>? mentionMris = null;
        IReadOnlyList<TeamsReaction>? reactions = null;
        var hasFiles = false;
        if (raw.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            if (properties.TryGetProperty("deletetime", out var deleteTime)
                && deleteTime.ValueKind != JsonValueKind.Null
                && !(deleteTime.ValueKind == JsonValueKind.String && deleteTime.GetString() == ""))
            {
                return null;
            }

            var rawSubject = GetString(properties, "subject");
            subject = string.IsNullOrEmpty(rawSubject) ? null : rawSubject;
            editedAt = ParseEditTime(properties);
            mentionMris = ParseMentionMris(properties);
            hasFiles = HasFilesProperty(properties);
            reactions = ParseReactions(properties);
        }

        var from = GetString(raw, "from") ?? "";
        var senderId = from.Substring(from.LastIndexOf('/') + 1).ToLowerInvariant();
        var senderName = GetString(raw, "imdisplayname") ?? "";

        return new TeamsMessage(id, senderId, senderName, sentAt, content, !KnownHtmlTag.IsMatch(content), subject, editedAt, mentionMris, hasFiles, reactions,
            GetString(raw, "rootMessageId"));
    }

    // properties.emotions: [{key, users: [{mri, time, value}]}], an array or a JSON string holding one. Null when there is none.
    private static IReadOnlyList<TeamsReaction>? ParseReactions(JsonElement properties)
    {
        if (!properties.TryGetProperty("emotions", out var emotions))
        {
            return null;
        }

        if (emotions.ValueKind == JsonValueKind.Array)
        {
            return ReactionsFrom(emotions);
        }

        if (emotions.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(emotions.GetString()))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(emotions.GetString()!);
            return document.RootElement.ValueKind == JsonValueKind.Array ? ReactionsFrom(document.RootElement) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<TeamsReaction>? ReactionsFrom(JsonElement emotions)
    {
        var reactions = new List<TeamsReaction>();
        foreach (var emotion in emotions.EnumerateArray())
        {
            var key = GetString(emotion, "key");
            if (key == null || !ReactionKey.IsMatch(key) || !emotion.TryGetProperty("users", out var users) || users.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var userMris = new List<string>();
            foreach (var user in users.EnumerateArray())
            {
                var mri = GetString(user, "mri")?.ToLowerInvariant();
                if (!string.IsNullOrEmpty(mri) && !userMris.Contains(mri))
                {
                    userMris.Add(mri);
                }
            }

            if (userMris.Count != 0)
            {
                reactions.Add(new TeamsReaction(key, userMris));
            }
        }

        return reactions.Count != 0 ? reactions : null;
    }

    private static DateTimeOffset? ParseEditTime(JsonElement properties)
    {
        if (!properties.TryGetProperty("edittime", out var editTime))
        {
            return null;
        }

        long epochMilliseconds;
        if (editTime.ValueKind == JsonValueKind.Number)
        {
            if (!editTime.TryGetInt64(out epochMilliseconds))
            {
                return null;
            }
        }
        else if (editTime.ValueKind != JsonValueKind.String
            || !long.TryParse(editTime.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out epochMilliseconds))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(epochMilliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    // Only people: a channel or team tag has no person mri, and the composer cannot send one back.
    private static IReadOnlyDictionary<string, string>? ParseMentionMris(JsonElement properties)
    {
        var rawMentions = GetString(properties, "mentions");
        if (string.IsNullOrEmpty(rawMentions))
        {
            return null;
        }

        var mentionMris = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(rawMentions);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var mention in document.RootElement.EnumerateArray())
            {
                var mentionType = GetString(mention, "mentionType");
                var itemId = GetString(mention, "itemid");
                var mri = GetString(mention, "mri");
                var isPerson = mentionType == null || mentionType == "person";
                if (isPerson && !string.IsNullOrEmpty(itemId) && !string.IsNullOrEmpty(mri))
                {
                    mentionMris[itemId] = mri;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return mentionMris.Count == 0 ? null : mentionMris;
    }

    private static bool HasFilesProperty(JsonElement properties)
    {
        if (!properties.TryGetProperty("files", out var files))
        {
            return false;
        }

        if (files.ValueKind == JsonValueKind.Array)
        {
            return files.GetArrayLength() != 0;
        }

        if (files.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(files.GetString()))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(files.GetString()!);
            return document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() != 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // A dropped span or link still goes on the stack unemitted, so its closing tag cannot close an outer one.
    private static void AppendOpenTag(StringBuilder html, List<(string Name, bool Emitted)> openTags, string name, string attributes, string deepLink,
        IReadOnlyDictionary<string, string>? mentionMris, bool forEditor)
    {
        if (name == "br")
        {
            html.Append("<br>");
        }
        else if (name == "pre")
        {
            TeamsCompose.AppendCodeBlockOpen(html, attributes, false);
            openTags.Add(("pre", true));
        }
        else if (AllowedTags.Contains(name))
        {
            html.Append('<').Append(name).Append('>');
            openTags.Add((name, true));
        }
        else if (name == "a")
        {
            var href = WebUtility.HtmlDecode(GetAttribute(attributes, "href") ?? "").Trim();
            var isWebLink = href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (isWebLink)
            {
                html.Append("<a href=\"").Append(Escape(href)).Append("\">");
            }
            openTags.Add(("a", isWebLink));
        }
        else if (name == "span")
        {
            var itemType = GetAttribute(attributes, "itemtype") ?? "";
            var isMention = itemType.Contains("schema.skype.com/Mention", StringComparison.OrdinalIgnoreCase);
            string? mri = null;
            var isEmitted = isMention;
            if (isMention && forEditor)
            {
                var hasMri = mentionMris != null && mentionMris.TryGetValue(GetAttribute(attributes, "itemid") ?? "", out mri);
                isEmitted = hasMri && !string.IsNullOrEmpty(mri);
            }

            var highlightClass = !isMention && openTags.Exists(ot => ot.Name == "pre") ? TeamsCompose.CodeSpanClass(attributes) : null;
            if (highlightClass != null)
            {
                html.Append("<span class=\"").Append(Escape(highlightClass)).Append("\">");
                isEmitted = true;
            }
            else if (isEmitted)
            {
                html.Append(forEditor ? $"<span class=\"mention\" data-mri=\"{Escape(mri ?? "")}\">" : "<span class=\"mention\">");
            }
            openTags.Add(("span", isEmitted));
        }
        else if (name == "img")
        {
            if (IsEmoji(attributes))
            {
                html.Append(Escape(WebUtility.HtmlDecode(GetAttribute(attributes, "alt") ?? "")));
            }
            else if (IsAmsImage(attributes, out var imageUrl))
            {
                if (forEditor)
                {
                    var width = TeamsCompose.ClampImageSide(GetAttribute(attributes, "width"));
                    var height = TeamsCompose.ClampImageSide(GetAttribute(attributes, "height"));
                    html.Append("<img class=\"ams\" data-src=\"").Append(Escape(imageUrl)).Append("\" data-ams=\"").Append(Escape(AmsObjectId(imageUrl))).Append('"');
                    if (width != null && height != null)
                    {
                        html.Append(" width=\"").Append(width).Append("\" height=\"").Append(height).Append('"');
                    }
                    html.Append('>');
                }
                else
                {
                    html.Append("<img class=\"ams\" data-src=\"").Append(Escape(imageUrl)).Append("\" alt=\"image\">");
                }
            }
            else
            {
                html.Append("<a class=\"image\" href=\"").Append(Escape(deepLink)).Append("\">[image]</a>");
            }
        }
    }

    private static void CloseTag(StringBuilder html, List<(string Name, bool Emitted)> openTags, string name)
    {
        var matchingIndex = openTags.FindLastIndex(ot => ot.Name == name);
        if (matchingIndex == -1)
        {
            return;
        }

        CloseFrom(html, openTags, matchingIndex);
    }

    // Closes every open tag from the top of the stack down to stackIndex; unemitted ones write nothing.
    private static void CloseFrom(StringBuilder html, List<(string Name, bool Emitted)> openTags, int stackIndex)
    {
        for (var openTagIndex = openTags.Count - 1; openTagIndex >= stackIndex; openTagIndex--)
        {
            if (openTags[openTagIndex].Emitted)
            {
                html.Append("</").Append(openTags[openTagIndex].Name).Append('>');
            }
        }

        openTags.RemoveRange(stackIndex, openTags.Count - stackIndex);
    }

    internal static int IndexAfterClosingTag(string content, int from, string name)
    {
        var closeStart = content.IndexOf("</" + name, from, StringComparison.OrdinalIgnoreCase);
        if (closeStart == -1)
        {
            return content.Length;
        }

        var closeEnd = content.IndexOf('>', closeStart);
        return closeEnd == -1 ? content.Length : closeEnd + 1;
    }

    internal static bool IsAmsImageUrl(string url)
    {
        return AmsImageUrl.IsMatch(url);
    }

    internal static bool IsAmsImage(string attributes, out string imageUrl)
    {
        var itemType = GetAttribute(attributes, "itemtype") ?? "";
        imageUrl = WebUtility.HtmlDecode(GetAttribute(attributes, "src") ?? "").Trim();
        return itemType.Contains("schema.skype.com/AMSImage", StringComparison.OrdinalIgnoreCase) && IsAmsImageUrl(imageUrl);
    }

    private static bool IsEmoji(string attributes)
    {
        var itemType = GetAttribute(attributes, "itemtype") ?? "";
        return itemType.Contains("schema.skype.com/Emoji", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? GetAttribute(string attributes, string name)
    {
        foreach (Match attribute in AttributePattern.Matches(attributes))
        {
            if (string.Equals(attribute.Groups["name"].Value, name, StringComparison.OrdinalIgnoreCase))
            {
                return attribute.Groups["value"].Value;
            }
        }

        return null;
    }

    internal static string Escape(string text)
    {
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }
}
