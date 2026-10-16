using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NoisLogTray;

// Builds the body of a Teams message from the composer's HTML: formatting, @mentions, images, the people to tag.
internal static class TeamsCompose
{
    internal const int MaxImages = 6;
    internal const int MaxImageBytes = 10 * 1024 * 1024;
    internal const int MaxTotalImageBytes = 20 * 1024 * 1024;
    internal const int MaxImageSide = 1000;
    internal const string AmsObjectsUrl = "https://as-api.asm.skype.com/v1/objects";

    private const string MentionItemType = "http://schema.skype.com/Mention";
    private const string AmsImageItemType = "http://schema.skype.com/AMSImage";

    private static readonly HashSet<string> KeptTags = new(StringComparer.Ordinal)
    {
        "p", "strong", "b", "em", "i", "u", "s", "strike", "code", "pre", "blockquote", "ul", "ol", "li"
    };

    // Everything BuildBody turns into something Teams keeps; a message with any other tag is not editable here.
    internal static readonly HashSet<string> EditableTags = new(KeptTags, StringComparer.Ordinal) { "div", "br", "a" };

    private static readonly Regex TrailingParenthesis = new(@"^(.*?)\s*\([^()]*\)\s*$", RegexOptions.Compiled);
    private static readonly Regex PersonMri = new(@"^8:orgid:[0-9a-f-]{36}\z", RegexOptions.Compiled);
    private static readonly Regex CodeLanguageToken = new(@"^language-(?<id>[a-z0-9+#_-]{1,24})\z", RegexOptions.Compiled);
    private static readonly Regex CodeBlockItemIdPattern = new(@"^codeBlockEditor-[0-9a-f-]{36}\z", RegexOptions.Compiled);
    private static readonly Regex HighlightFirstToken = new(@"^hljs-[a-z_-]{1,30}\z", RegexOptions.Compiled);
    private static readonly Regex HighlightToken = new(@"^[a-z_-]{1,30}\z", RegexOptions.Compiled);
    private const string ManuallySelected = "was-manually-selected";

    internal static string ShortName(string displayName)
    {
        var match = TrailingParenthesis.Match(displayName);
        if (!match.Success)
        {
            return displayName;
        }

        var shortName = match.Groups[1].Value.Trim();
        return shortName.Length == 0 ? displayName : shortName;
    }

    internal static List<TeamsPerson> People(IEnumerable<TeamsMessage> messages, string myId)
    {
        return messages
            .Where(tm => tm.SenderName.Length != 0 && IsPersonMri(tm.SenderId) && !string.Equals(tm.SenderId, myId, StringComparison.Ordinal))
            .GroupBy(tm => tm.SenderId, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(tm => tm.SentAt).First())
            .OrderByDescending(tm => tm.SentAt)
            .Select(tm => new TeamsPerson(tm.SenderId, tm.SenderName))
            .ToList();
    }

    // The pre's class when it is one language token plus at most the manual-pick token, else null.
    internal static string? CodeBlockClass(string attributes)
    {
        var tokens = WebUtility.HtmlDecode(TeamsChat.GetAttribute(attributes, "class") ?? "")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || tokens.Length > 2)
        {
            return null;
        }

        var languageTokens = tokens.Where(t => t != "language-" + ManuallySelected).ToList();
        var manualCount = tokens.Length - languageTokens.Count;
        if (languageTokens.Count != 1 || manualCount > 1)
        {
            return null;
        }

        var match = CodeLanguageToken.Match(languageTokens[0]);
        if (!match.Success || match.Groups["id"].Value == ManuallySelected)
        {
            return null;
        }

        return manualCount == 1 ? languageTokens[0] + " language-" + ManuallySelected : languageTokens[0];
    }

    // A highlight.js class (first token hljs-*, hljs 11 may add more such as function_), else null.
    internal static string? CodeSpanClass(string attributes)
    {
        var tokens = WebUtility.HtmlDecode(TeamsChat.GetAttribute(attributes, "class") ?? "")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || !HighlightFirstToken.IsMatch(tokens[0]) || !tokens.All(t => HighlightToken.IsMatch(t)))
        {
            return null;
        }

        return string.Join(' ', tokens);
    }

    private static readonly Regex TrailingEmptyParagraphs = new(@"(?:<p>(?:<br>|\s|&nbsp;)*</p>)+\z", RegexOptions.Compiled);

    internal const string CodeBlockMarkerItemType = "http://schema.skype.com/CodeBlockEditor";

    // Teams draws a block's language header and copy button only when this paragraph precedes it (read from Teams' own posts).
    internal static string CodeBlockMarker(string itemId)
    {
        return "<p itemtype=\"" + CodeBlockMarkerItemType + "\" id=\"x_" + TeamsChat.Escape(itemId) + "\">&nbsp;</p>";
    }

    // usedItemIds (sending only): a block pasted twice keeps its id once, the copy gets a new one.
    // generateItemId (sending only) also writes Teams' marker paragraph before a block that has an id.
    internal static void AppendCodeBlockOpen(StringBuilder output, string attributes, bool generateItemId, ISet<string>? usedItemIds = null)
    {
        var codeClass = CodeBlockClass(attributes);
        var itemId = WebUtility.HtmlDecode(TeamsChat.GetAttribute(attributes, "itemid") ?? "").Trim();
        var hasItemId = CodeBlockItemIdPattern.IsMatch(itemId) && (usedItemIds == null || usedItemIds.Add(itemId));
        string? writtenItemId = null;
        if (hasItemId)
        {
            writtenItemId = itemId;
        }
        else if (generateItemId && codeClass != null)
        {
            writtenItemId = "codeBlockEditor-" + Guid.NewGuid().ToString("D");
        }

        if (generateItemId && writtenItemId != null)
        {
            output.Append(CodeBlockMarker(writtenItemId));
        }

        output.Append("<pre");
        if (codeClass != null)
        {
            output.Append(" class=\"").Append(TeamsChat.Escape(codeClass)).Append('"');
        }

        if (writtenItemId != null)
        {
            output.Append(" itemid=\"").Append(TeamsChat.Escape(writtenItemId)).Append('"');
        }

        output.Append('>');
    }

    // Inside a code block a non-breaking space is written as &nbsp; the way Teams stores it.
    private static void AppendText(StringBuilder output, List<(string Name, string? EmittedName)> openTags, string rawText)
    {
        var text = TeamsChat.Escape(WebUtility.HtmlDecode(rawText));
        if (openTags.Exists(ot => ot.Name == "pre"))
        {
            text = text.Replace("\u00A0", "&nbsp;");
        }

        output.Append(text);
    }

    internal static TeamsImageProblem? ImageProblem(TeamsImage image)
    {
        var bytes = image.Bytes;
        if (bytes.Length == 0)
        {
            return TeamsImageProblem.Empty;
        }

        if (bytes.Length > MaxImageBytes)
        {
            return TeamsImageProblem.TooLarge;
        }

        var isPng = bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
        var isJpeg = bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
        var isGif = bytes.Length >= 4 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38;
        if (!isPng && !isJpeg && !isGif)
        {
            return TeamsImageProblem.NotAnImage;
        }

        return null;
    }

    internal static string ImageProblemText(TeamsImageProblem problem, string fileName)
    {
        switch (problem)
        {
            case TeamsImageProblem.Empty:
                return $"{fileName} is empty.";
            case TeamsImageProblem.TooLarge:
                return $"{fileName} is larger than {MaxImageBytes / (1024 * 1024)} MB.";
            default:
                return $"{fileName} is not a PNG, JPEG or GIF image.";
        }
    }

    internal static (int Width, int Height) DisplaySize(int width, int height)
    {
        var longestSide = Math.Max(width, height);
        if (longestSide <= MaxImageSide)
        {
            return (Math.Max(1, width), Math.Max(1, height));
        }

        var scale = (double)MaxImageSide / longestSide;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    internal static bool IsPersonMri(string mri)
    {
        return PersonMri.IsMatch(mri);
    }

    // Null when missing or not a number: the attribute is then left out, so Teams sizes the image itself instead of 1x1.
    internal static int? ClampImageSide(string? value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var side))
        {
            return null;
        }

        return Math.Clamp(side, 1, MaxImageSide);
    }

    // One <p> per line, links made clickable: plain text (a suggestion, a plain-text message to edit) as the composer's HTML.
    internal static string ParagraphsFromText(string text)
    {
        var html = new StringBuilder();
        foreach (var line in text.Trim().ReplaceLineEndings("\n").Split('\n'))
        {
            html.Append(line.Length == 0 ? "<p><br></p>" : "<p>" + TeamsPost.ToHtml(line) + "</p>");
        }

        return html.ToString();
    }

    internal static bool IsEmptyHtml(string html)
    {
        foreach (Match tag in TeamsChat.TagPattern.Matches(html))
        {
            var isImage = tag.Groups["close"].Length == 0 && tag.Groups["name"].Value.Equals("img", StringComparison.OrdinalIgnoreCase);
            var attributes = tag.Groups["attributes"].Value;
            if (isImage && (TeamsChat.GetAttribute(attributes, "data-image") != null || TeamsChat.GetAttribute(attributes, "data-ams") != null))
            {
                return false;
            }
        }

        var text = WebUtility.HtmlDecode(TeamsChat.TagPattern.Replace(html, ""));
        return text.Trim().Length == 0;
    }

    // Turns the composer's HTML into a Teams message body, keeping only the formatting Teams reads back unchanged.
    internal static TeamsMessageBody BuildBody(string html, IReadOnlyList<TeamsMention> mentions,
        IReadOnlyList<(string ObjectId, TeamsImage Image)> uploaded, IReadOnlySet<string> keptAmsIds)
    {
        var output = new StringBuilder();
        var openTags = new List<(string Name, string? EmittedName)>();
        var postedMentions = new List<Dictionary<string, string>>();
        var placedUploadIndexes = new HashSet<int>();
        var amsReferences = new List<string>();
        var usedCodeBlockItemIds = new HashSet<string>(StringComparer.Ordinal);
        var cursor = 0;
        foreach (Match tag in TeamsChat.TagPattern.Matches(html))
        {
            if (tag.Index < cursor)
            {
                continue;
            }

            AppendText(output, openTags, html.Substring(cursor, tag.Index - cursor));
            cursor = tag.Index + tag.Length;

            var name = tag.Groups["name"].Value.ToLowerInvariant();
            var attributes = tag.Groups["attributes"].Value;
            if (tag.Groups["close"].Length != 0)
            {
                CloseTag(output, openTags, name);
            }
            else if (TeamsChat.DroppedWithContentTags.Contains(name))
            {
                if (!attributes.TrimEnd().EndsWith('/'))
                {
                    cursor = TeamsChat.IndexAfterClosingTag(html, cursor, name);
                }
            }
            else if (name == "br")
            {
                output.Append("<br>");
            }
            else if (name == "img")
            {
                AppendMarkedImage(output, attributes, uploaded, keptAmsIds, placedUploadIndexes, amsReferences);
            }
            else if (name == "span")
            {
                var highlightClass = openTags.Exists(ot => ot.Name == "pre") ? CodeSpanClass(attributes) : null;
                if (highlightClass != null)
                {
                    output.Append("<span class=\"").Append(TeamsChat.Escape(highlightClass)).Append("\">");
                    openTags.Add(("span", "span"));
                }
                else
                {
                    cursor = AppendMentionSpan(output, openTags, postedMentions, html, cursor, attributes, mentions);
                }
            }
            else if (name == "pre")
            {
                AppendCodeBlockOpen(output, attributes, true, usedCodeBlockItemIds);
                openTags.Add(("pre", "pre"));
            }
            else if (name == "a")
            {
                var href = WebUtility.HtmlDecode(TeamsChat.GetAttribute(attributes, "href") ?? "").Trim();
                var isWebLink = href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                if (isWebLink)
                {
                    var escapedHref = TeamsChat.Escape(href);
                    output.Append("<a href=\"").Append(escapedHref).Append("\" title=\"").Append(escapedHref).Append("\">");
                }

                openTags.Add(("a", isWebLink ? "a" : null));
            }
            else if (name == "div")
            {
                output.Append("<p>");
                openTags.Add(("div", "p"));
            }
            else if (KeptTags.Contains(name))
            {
                output.Append('<').Append(name).Append('>');
                openTags.Add((name, name));
            }
        }

        if (cursor < html.Length)
        {
            AppendText(output, openTags, html.Substring(cursor));
        }

        CloseFrom(output, openTags, 0);

        // The composer keeps an empty line after a code block to type into; one left empty is not sent.
        var trailingEmpty = TrailingEmptyParagraphs.Match(output.ToString());
        if (trailingEmpty.Success)
        {
            output.Length = trailingEmpty.Index;
        }

        for (var uploadIndex = 0; uploadIndex < uploaded.Count; uploadIndex++)
        {
            if (!placedUploadIndexes.Contains(uploadIndex))
            {
                var (width, height) = DisplaySize(uploaded[uploadIndex].Image.Width, uploaded[uploadIndex].Image.Height);
                output.Append("<p>");
                AppendAmsImage(output, uploaded[uploadIndex].ObjectId, width, height);
                output.Append("</p>");
                amsReferences.Add(uploaded[uploadIndex].ObjectId);
            }
        }

        var mentionsJson = postedMentions.Count == 0 ? null : JsonSerializer.Serialize(postedMentions);
        return new TeamsMessageBody(output.ToString(), mentionsJson, amsReferences);
    }

    // A valid marker becomes a Teams mention (its text is the span's own); an invalid one keeps only its text. Returns the new cursor.
    private static int AppendMentionSpan(StringBuilder output, List<(string Name, string? EmittedName)> openTags,
        List<Dictionary<string, string>> postedMentions, string html, int cursor, string attributes, IReadOnlyList<TeamsMention> mentions)
    {
        var hasMarkerIndex = int.TryParse(TeamsChat.GetAttribute(attributes, "data-mention"), NumberStyles.None, CultureInfo.InvariantCulture, out var markerIndex);
        if (!hasMarkerIndex || markerIndex >= mentions.Count || !IsPersonMri(mentions[markerIndex].Mri))
        {
            openTags.Add(("span", null));
            return cursor;
        }

        var mention = mentions[markerIndex];
        var innerEnd = html.IndexOf("</span", cursor, StringComparison.OrdinalIgnoreCase);
        var innerHtml = innerEnd == -1 ? html.Substring(cursor) : html.Substring(cursor, innerEnd - cursor);
        var label = WebUtility.HtmlDecode(TeamsChat.TagPattern.Replace(innerHtml, "")).Trim();
        var itemId = postedMentions.Count.ToString(CultureInfo.InvariantCulture);
        output.Append($"<span itemtype=\"{MentionItemType}\" itemscope itemid=\"{itemId}\">{TeamsChat.Escape(label.Length == 0 ? mention.Text : label)}</span>");
        postedMentions.Add(new Dictionary<string, string>
        {
            ["@type"] = MentionItemType,
            ["itemid"] = itemId,
            ["mri"] = mention.Mri,
            ["mentionType"] = "person",
            ["displayName"] = mention.DisplayName
        });
        return innerEnd == -1 ? html.Length : TeamsChat.IndexAfterClosingTag(html, cursor, "span");
    }

    // data-image="j" places the j-th uploaded image here; data-ams="id" keeps an image the edited message already had.
    private static void AppendMarkedImage(StringBuilder output, string attributes, IReadOnlyList<(string ObjectId, TeamsImage Image)> uploaded,
        IReadOnlySet<string> keptAmsIds, HashSet<int> placedUploadIndexes, List<string> amsReferences)
    {
        var imageMarker = TeamsChat.GetAttribute(attributes, "data-image");
        if (imageMarker != null)
        {
            var hasMarkerIndex = int.TryParse(imageMarker, NumberStyles.None, CultureInfo.InvariantCulture, out var markerIndex);
            if (hasMarkerIndex && markerIndex < uploaded.Count && placedUploadIndexes.Add(markerIndex))
            {
                var (width, height) = DisplaySize(uploaded[markerIndex].Image.Width, uploaded[markerIndex].Image.Height);
                AppendAmsImage(output, uploaded[markerIndex].ObjectId, width, height);
                amsReferences.Add(uploaded[markerIndex].ObjectId);
            }

            return;
        }

        var keptObjectId = WebUtility.HtmlDecode(TeamsChat.GetAttribute(attributes, "data-ams") ?? "").Trim();
        if (keptObjectId.Length != 0 && keptAmsIds.Contains(keptObjectId))
        {
            AppendAmsImage(output, keptObjectId, ClampImageSide(TeamsChat.GetAttribute(attributes, "width")),
                ClampImageSide(TeamsChat.GetAttribute(attributes, "height")));
            if (!amsReferences.Contains(keptObjectId))
            {
                amsReferences.Add(keptObjectId);
            }
        }
    }

    private static void AppendAmsImage(StringBuilder output, string objectId, int? width, int? height)
    {
        var escapedObjectId = TeamsChat.Escape(objectId);
        var size = width != null && height != null ? $" width=\"{width}\" height=\"{height}\"" : "";
        output.Append($"<img itemtype=\"{AmsImageItemType}\" src=\"{AmsObjectsUrl}/{escapedObjectId}/views/imgo\"{size} id=\"{escapedObjectId}\" itemscope=\"\" alt=\"image\">");
    }

    private static void CloseTag(StringBuilder output, List<(string Name, string? EmittedName)> openTags, string name)
    {
        var matchingIndex = openTags.FindLastIndex(ot => ot.Name == name);
        if (matchingIndex != -1)
        {
            CloseFrom(output, openTags, matchingIndex);
        }
    }

    private static void CloseFrom(StringBuilder output, List<(string Name, string? EmittedName)> openTags, int stackIndex)
    {
        for (var openTagIndex = openTags.Count - 1; openTagIndex >= stackIndex; openTagIndex--)
        {
            if (openTags[openTagIndex].EmittedName != null)
            {
                output.Append("</").Append(openTags[openTagIndex].EmittedName).Append('>');
            }
        }

        openTags.RemoveRange(stackIndex, openTags.Count - stackIndex);
    }
}
