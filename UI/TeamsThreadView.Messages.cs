using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NoisLogTray;

// What the host and the page send each other: the thread or the draft, the people, the state and status lines, the labels and the theme.
internal sealed partial class TeamsThreadView
{
    internal const int MaxDraftHtmlChars = 2 * 1024 * 1024;

    private static readonly Regex ParenthesizedPart = new(@"\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex ImageDataUrlPrefix = new(@"^data:image/(?<type>png|jpeg|gif);base64,", RegexOptions.Compiled);

    // Sent again in this order when the page (re)loads, after the theme and the labels.
    private static readonly string[] KeptMessageOrder = { "people", "conversations", "thread", "draft", "state", "analysis", "commentFlow", "sendState", "postState" };

    // The newest message of each kind that still holds; a kind that no longer does is forgotten.
    private readonly Dictionary<string, JsonObject> _kept = new(StringComparer.Ordinal);
    private bool _ready;
    private string? _stateKind;
    private string? _stateError;
    private bool _stateInline;
    private string? _sendStateKind;
    private string? _sendStateError;
    private string? _postStateKind;
    private string? _postStateError;

    internal JsonObject? LatestMessage(string type)
    {
        return _kept.GetValueOrDefault(type);
    }

    internal static JsonObject BuildThreadMessage(TeamsThreadResult result)
    {
        var thread = result.Thread;
        var isChat = thread != null && thread.IsChat;
        string? deepLink = null;
        var needsReply = false;
        var messages = new JsonArray();
        if (thread != null)
        {
            if (isChat)
            {
                deepLink = thread.Messages.Count == 0 ? null : TeamsChat.ChatDeepLink(thread.ChannelId, thread.Messages[thread.Messages.Count - 1].Id);
            }
            else
            {
                deepLink = TeamsChat.DeepLink(thread.ChannelId, thread.RootId);
            }
            needsReply = TeamsChat.NeedsReply(thread.Messages, result.MyId);
            // Reactors are named from the thread's senders, then the people seen elsewhere; an unknown one only counts.
            var namesByMri = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var message in thread.Messages)
            {
                namesByMri.TryAdd(message.SenderId, message.SenderName);
            }
            foreach (var person in result.People ?? Array.Empty<TeamsPerson>())
            {
                namesByMri.TryAdd(person.Mri, person.DisplayName);
            }
            foreach (var message in thread.Messages)
            {
                var nameWords = ParenthesizedPart.Replace(message.SenderName, " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var isMine = string.Equals(message.SenderId, result.MyId, StringComparison.Ordinal);
                var isEditable = isMine && TeamsChat.IsEditable(message);
                var messageDeepLink = isChat ? TeamsChat.ChatDeepLink(thread.ChannelId, message.Id) : deepLink!;
                var item = new JsonObject
                {
                    ["id"] = message.Id,
                    ["senderName"] = message.SenderName,
                    ["initials"] = string.Concat(nameWords.Take(2).Select(s => char.ToUpperInvariant(s[0]))),
                    // Not string.GetHashCode, which changes on every run: a person keeps one color.
                    ["colorIndex"] = message.SenderId.Sum(c => (int)c) % 8,
                    ["timeText"] = Hcm.ToHcm(message.SentAt).ToString("dd/MM HH:mm", CultureInfo.InvariantCulture),
                    ["html"] = TeamsChat.SafeHtml(message, messageDeepLink, false),
                    ["isPlain"] = message.IsPlainText,
                    ["mine"] = isMine,
                    ["edited"] = message.EditedAt != null,
                    ["editable"] = isEditable,
                    ["subject"] = message.Subject,
                };
                if (TeamsCompose.IsPersonMri(message.SenderId))
                {
                    item["senderMri"] = message.SenderId;
                }
                if (message.Reactions != null)
                {
                    var reactions = new JsonArray();
                    foreach (var reaction in message.Reactions)
                    {
                        var names = reaction.UserMris.Select(um => namesByMri.GetValueOrDefault(um)).Where(rn => !string.IsNullOrEmpty(rn));
                        reactions.Add(new JsonObject
                        {
                            ["key"] = reaction.Key,
                            ["count"] = reaction.UserMris.Count,
                            ["mine"] = reaction.UserMris.Contains(result.MyId, StringComparer.OrdinalIgnoreCase),
                            ["names"] = string.Join(", ", names),
                            ["url"] = TeamsChat.EmojiUrl(reaction.Key),
                        });
                    }
                    item["reactions"] = reactions;
                }
                if (isEditable)
                {
                    // A plain-text message has no markup to keep, so its lines become the composer's paragraphs.
                    item["editHtml"] = message.IsPlainText
                        ? TeamsCompose.ParagraphsFromText(message.Content)
                        : TeamsChat.SafeHtml(message, messageDeepLink, true);
                }
                messages.Add(item);
            }
        }

        var threadMessage = new JsonObject
        {
            ["type"] = "thread",
            ["deepLink"] = deepLink,
            ["isChat"] = isChat,
            ["needsReply"] = needsReply,
            ["stillSearching"] = result.StillSearching,
            ["messages"] = messages,
        };
        AddChannels(threadMessage, result, result.ChannelId ?? thread?.ChannelId);
        return threadMessage;
    }

    // The draft's first line is the post's subject; the rest is markdown, rendered on the page.
    internal static JsonObject BuildDraftMessage(string ticketKey, TeamsThreadResult result, string draft, DateTimeOffset? postedAt, bool reload)
    {
        var (subject, markdown) = TeamsPost.SplitSubject(draft);
        var draftMessage = new JsonObject
        {
            ["type"] = "draft",
            ["subject"] = subject,
            ["markdown"] = markdown,
            ["postedAt"] = postedAt == null
                ? null
                : Lang.T($"Already posted on {PostedWhen(postedAt.Value)}.", $"Đã đăng lúc {PostedWhen(postedAt.Value)}."),
            ["source"] = Lang.T($@"This ticket has no post in the selected channel yet. The draft is docs\{ticketKey}\teams-post.md, or the ticket's PRs when that file is missing.",
                $@"Chưa có bài nào của ticket này trong kênh đã chọn. Nháp lấy từ docs\{ticketKey}\teams-post.md, hoặc từ các PR của ticket khi chưa có file đó."),
            ["stillSearching"] = result.StillSearching,
            ["reload"] = reload,
        };
        AddChannels(draftMessage, result, result.ChannelId);
        return draftMessage;
    }

    internal static JsonObject BuildCommentFlowMessage(TeamsCommentFlow flow)
    {
        return new JsonObject
        {
            ["type"] = "commentFlow",
            ["conversationKey"] = flow.ConversationKey,
            ["stage"] = flow.Stage switch
            {
                TeamsCommentStage.Asked => "asked",
                TeamsCommentStage.HandedOff => "handedOff",
                _ => "done",
            },
            ["sender"] = flow.AskSenderName,
            ["prompt"] = TeamsReply.CommentPrompt(flow.TicketKey),
            ["openThreads"] = flow.OpenThreadCount,
            ["reply"] = TeamsReply.DoneReply,
            ["replyHtml"] = TeamsCompose.ParagraphsFromText(TeamsReply.DoneReply),
        };
    }

    // The page's conversation dropdown: the channel post first, then each chat; a null id is the post.
    internal static JsonObject BuildConversationsMessage(IReadOnlyList<TeamsGroupChat> chats, string? selectedChatId)
    {
        var items = new JsonArray();
        items.Add(new JsonObject { ["id"] = null, ["label"] = Lang.T("Post on the channel", "Post kênh") });
        foreach (var chat in chats)
        {
            items.Add(new JsonObject { ["id"] = chat.Id, ["label"] = Lang.T($"Chat: {chat.Topic}", $"Chat: {chat.Topic}") });
        }
        return new JsonObject
        {
            ["type"] = "conversations",
            ["items"] = items,
            ["selected"] = string.IsNullOrEmpty(selectedChatId) ? null : selectedChatId,
        };
    }

    private static JsonObject BuildPeopleMessage(IReadOnlyList<TeamsPerson> people)
    {
        var items = new JsonArray();
        foreach (var person in people)
        {
            items.Add(new JsonObject
            {
                ["mri"] = person.Mri,
                ["name"] = person.DisplayName,
                ["shortName"] = TeamsCompose.ShortName(person.DisplayName),
            });
        }
        return new JsonObject { ["type"] = "people", ["people"] = items };
    }

    // The finished analysis; each reply carries the composer's HTML, so a suggestion button keeps its lines and links.
    internal static JsonObject BuildAnalysisMessage(TeamsAnalysis analysis)
    {
        var issues = new JsonArray();
        foreach (var issue in analysis.Issues)
        {
            issues.Add(new JsonObject { ["text"] = issue.Text, ["status"] = issue.Status });
        }

        var items = new JsonArray();
        foreach (var reply in analysis.Replies)
        {
            items.Add(new JsonObject { ["text"] = reply, ["html"] = TeamsCompose.ParagraphsFromText(reply) });
        }

        return new JsonObject
        {
            ["type"] = "analysis",
            ["kind"] = "done",
            ["issues"] = issues,
            ["diagnosis"] = analysis.Diagnosis,
            ["nextStep"] = analysis.NextStep,
            ["items"] = items,
        };
    }

    // The page's channel dropdown: TeamsChat.ChannelGroups, the recent group's channels named with their team.
    private static void AddChannels(JsonObject message, TeamsThreadResult result, string? channelId)
    {
        var recentChannelIds = result.RecentChannelIds ?? Array.Empty<string>();
        var groups = TeamsChat.ChannelGroups(result.Channels, recentChannelIds);
        var hasRecentGroup = recentChannelIds.Any(ri => result.Channels.Any(tc => tc.Id == ri));
        var groupItems = new JsonArray();
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var isRecent = hasRecentGroup && groupIndex == 0;
            var channelItems = new JsonArray();
            foreach (var channel in groups[groupIndex].Channels)
            {
                channelItems.Add(new JsonObject
                {
                    ["id"] = channel.Id,
                    ["name"] = isRecent ? $"{channel.TeamName} / {channel.ChannelName}" : channel.ChannelName,
                });
            }
            groupItems.Add(new JsonObject
            {
                ["title"] = isRecent ? Lang.T("Recent", "Gần đây") : groups[groupIndex].Title,
                ["recent"] = isRecent,
                ["channels"] = channelItems,
            });
        }

        // A saved channel that is no longer listed counts as none.
        var selectedChannel = result.Channels.FirstOrDefault(tc => tc.Id == channelId);
        message["channelId"] = selectedChannel?.Id;
        message["channelName"] = selectedChannel == null ? null : $"{selectedChannel.TeamName} / {selectedChannel.ChannelName}";
        message["channelGroups"] = groupItems;
    }

    // The page checks the images first; this checks them again, since these bytes go to Teams as they are.
    internal static (string? Subject, TeamsDraft? Draft, string? Error) ParseDraft(JsonElement message)
    {
        var html = GetString(message, "html") ?? "";
        if (html.Length > MaxDraftHtmlChars)
        {
            return (null, null, Lang.T("The message is too long.", "Tin quá dài."));
        }

        // The html points at mentions by index, so a bad one keeps its slot; a blank mri posts it as plain text.
        var mentions = new List<TeamsMention>();
        if (message.TryGetProperty("mentions", out var rawMentions) && rawMentions.ValueKind == JsonValueKind.Array)
        {
            foreach (var rawMention in rawMentions.EnumerateArray())
            {
                var mentionText = GetString(rawMention, "text") ?? "";
                var mri = GetString(rawMention, "mri") ?? "";
                var displayName = GetString(rawMention, "displayName");
                mentions.Add(new TeamsMention(mentionText, TeamsCompose.IsPersonMri(mri) ? mri : "",
                    string.IsNullOrWhiteSpace(displayName) ? mentionText : displayName));
            }
        }

        var images = new List<TeamsImage>();
        if (message.TryGetProperty("images", out var rawImages) && rawImages.ValueKind == JsonValueKind.Array)
        {
            if (rawImages.GetArrayLength() > TeamsCompose.MaxImages)
            {
                return (null, null, TooManyImagesText());
            }

            long totalImageBytes = 0;
            foreach (var rawImage in rawImages.EnumerateArray())
            {
                var dataUrl = GetString(rawImage, "dataUrl") ?? "";
                var prefix = ImageDataUrlPrefix.Match(dataUrl);
                var fileName = ImageFileName(GetString(rawImage, "name"), prefix.Success ? prefix.Groups["type"].Value : "png");
                if (!prefix.Success)
                {
                    return (null, null, string.Format(ImageNotSupportedText(), fileName));
                }

                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(dataUrl.Substring(prefix.Length));
                }
                catch (FormatException)
                {
                    return (null, null, string.Format(ImageNotSupportedText(), fileName));
                }

                var image = new TeamsImage(bytes, fileName, Math.Max(1, GetInt(rawImage, "width")), Math.Max(1, GetInt(rawImage, "height")));
                var problem = TeamsCompose.ImageProblem(image);
                if (problem == TeamsImageProblem.TooLarge)
                {
                    return (null, null, string.Format(ImageTooLargeText(), fileName));
                }
                if (problem != null)
                {
                    return (null, null, string.Format(ImageNotSupportedText(), fileName));
                }

                totalImageBytes += bytes.Length;
                if (totalImageBytes > TeamsCompose.MaxTotalImageBytes)
                {
                    return (null, null, ImagesTooLargeInAllText());
                }
                images.Add(image);
            }
        }

        return (GetString(message, "subject"), new TeamsDraft(html, mentions, images), null);
    }

    // The sentences for an image the page or ParseDraft refuses; {0} is the file name.
    internal static string ImageNotSupportedText()
    {
        return Lang.T("{0} is not a PNG, JPEG or GIF image.", "{0} không phải ảnh PNG, JPEG hay GIF.");
    }

    internal static string ImageTooLargeText()
    {
        var megabytes = TeamsCompose.MaxImageBytes / (1024 * 1024);
        return Lang.T($"{{0}} is over {megabytes} MB.", $"{{0}} lớn hơn {megabytes} MB.");
    }

    internal static string TooManyImagesText()
    {
        return Lang.T($"At most {TeamsCompose.MaxImages} images per message.", $"Tối đa {TeamsCompose.MaxImages} ảnh mỗi tin.");
    }

    internal static string ImagesTooLargeInAllText()
    {
        var megabytes = TeamsCompose.MaxTotalImageBytes / (1024 * 1024);
        return Lang.T($"The images are over {megabytes} MB in all.", $"Tổng các ảnh vượt {megabytes} MB.");
    }

    // A plain file name for Teams: no folders, no characters Windows refuses; a pasted image may come with none.
    private static string ImageFileName(string? name, string imageType)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var fileName = string.Concat(Path.GetFileName(name ?? "").Where(c => !invalidCharacters.Contains(c))).Trim();
        if (fileName.Length != 0)
        {
            return fileName;
        }
        return "image." + (imageType == "jpeg" ? "jpg" : imageType);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }
        return null;
    }

    private static int GetInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }
        return 0;
    }

    // The texts follow the language, so a switch relabels the page and words the shown messages again.
    private void OnLanguageChanged()
    {
        PostLabels();
        PostState();
        PostSendState();
        PostPostState();
        if (_lastChats != null && _lastChats.Count != 0)
        {
            Keep(BuildConversationsMessage(_lastChats, _chatId));
        }
        if (_found != null)
        {
            Keep(BuildThreadMessage(_found));
        }
        PostDraft(false);
    }

    // Inline: a line above the draft instead of a panel over the page.
    private void ShowState(string kind, string? error, bool inline)
    {
        _stateKind = kind;
        _stateError = error;
        _stateInline = inline;
        PostState();
    }

    private void ClearState()
    {
        _stateKind = null;
        _stateError = null;
        Forget("state");
    }

    private void PostState()
    {
        if (_stateKind == null)
        {
            return;
        }
        var text = _stateKind switch
        {
            "loading" => Lang.T("Reading Teams...", "Đang đọc Teams..."),
            "waitingPost" => Lang.T("Waiting for the post to show up in Teams...", "Đang chờ bài đăng hiện trên Teams..."),
            "signedOut" => Lang.T("Not signed in to Teams (NOIS account).", "Chưa đăng nhập Teams (tài khoản NOIS)."),
            _ => Lang.T("Could not read Teams.", "Không đọc được Teams."),
        };
        if (_stateError != null)
        {
            text += (_stateInline ? " " : "\n") + _stateError;
        }
        Keep(new JsonObject { ["type"] = "state", ["kind"] = _stateKind, ["inline"] = _stateInline, ["text"] = text });
    }

    private void ShowSendState(string kind, string? error)
    {
        _sendStateKind = kind;
        _sendStateError = error;
        PostSendState();
    }

    private void PostSendState()
    {
        if (_sendStateKind == null)
        {
            return;
        }
        var text = _sendStateKind switch
        {
            "sending" => Lang.T("Sending...", "Đang gửi..."),
            "sent" => Lang.T("Sent.", "Đã gửi."),
            "uncertain" => Lang.T("Not sure it was sent - press Refresh to check before sending again.",
                "Không chắc đã gửi - bấm Làm mới để xem trước khi gửi lại."),
            _ => Lang.T("Could not send.", "Không gửi được."),
        };
        if (_sendStateError != null)
        {
            text += " " + _sendStateError;
        }
        Keep(new JsonObject { ["type"] = "sendState", ["kind"] = _sendStateKind, ["text"] = text });
    }

    // A warning carries its whole sentence as the error.
    private void ShowPostState(string kind, string? error)
    {
        _postStateKind = kind;
        _postStateError = error;
        PostPostState();
    }

    private void PostPostState()
    {
        if (_postStateKind == null)
        {
            return;
        }
        var text = _postStateKind switch
        {
            "posting" => Lang.T("Posting...", "Đang đăng..."),
            "sent" => Lang.T("Posted.", "Đã đăng."),
            "uncertain" => Lang.T("Not sure it was posted - checking Teams. If the thread does not open, look in Teams before posting again.",
                "Không chắc đã đăng - đang kiểm lại trên Teams. Nếu thread không tự mở, xem trong Teams trước khi đăng lại."),
            "warning" => "",
            _ => Lang.T("Could not post.", "Đăng không được."),
        };
        if (_postStateError != null)
        {
            text = text.Length == 0 ? _postStateError : text + " " + _postStateError;
        }
        Keep(new JsonObject { ["type"] = "postState", ["kind"] = _postStateKind, ["text"] = text });
    }

    private void PostLabels()
    {
        var labels = JsonSerializer.SerializeToNode(new
        {
            type = "labels",
            openInTeams = Lang.T("Open in Teams", "Mở trong Teams"),
            refresh = Lang.T("Refresh", "Làm mới"),
            channel = Lang.T("Channel: {0}", "Kênh: {0}"),
            pickChannel = Lang.T("Pick a channel", "Chọn kênh"),
            noChannels = Lang.T("No channels yet - Teams has not been read", "Chưa có kênh - chưa đọc được Teams"),
            signIn = Lang.T("Sign in to Teams", "Đăng nhập Teams"),
            retry = Lang.T("Retry", "Thử lại"),
            image = Lang.T("Image - open in Teams", "Ảnh - mở trong Teams"),
            edited = Lang.T("(edited)", "(đã sửa)"),
            edit = Lang.T("Edit", "Sửa"),
            delete = Lang.T("Delete", "Xoá"),
            handToClaude = Lang.T("Hand to Claude", "Giao cho Claude"),
            handedToClaude = Lang.T("Handed to Claude - see the Claude view.", "Đã giao cho Claude - xem tab Claude."),
            commentAsk = Lang.T("{0} asked to check the PR comments", "{0} nhờ check comment PR"),
            commentHandOff = Lang.T("Hand to Claude", "Giao cho Claude"),
            commentWaiting = Lang.T("Handed to Claude, waiting for it to finish...", "Đã giao cho Claude, chờ Claude xong..."),
            commentOpenThreads = Lang.T("{0} comments still open", "Còn {0} comment mở"),
            commentDone = Lang.T("Claude is done - the reply is in the suggestions.", "Claude đã xong - câu trả lời có trong gợi ý."),
            commentMarkDone = Lang.T("Done", "Đã xong"),
            dismiss = Lang.T("Dismiss", "Bỏ qua"),
            deleting = Lang.T("Deleting...", "Đang xoá..."),
            saving = Lang.T("Saving...", "Đang lưu..."),
            analysisTitle = Lang.T("Claude's analysis", "Phân tích của Claude"),
            analysisRunning = Lang.T("Claude is analyzing...", "Claude đang phân tích..."),
            analysisSummary = Lang.T("Claude: {0} issues, {1} open", "Claude: {0} vấn đề, {1} còn mở"),
            noAnalysis = Lang.T("Claude gave no analysis.", "Claude không đưa ra phân tích nào."),
            noAnalysisHint = Lang.T("No analysis yet. Use Analyze above the reply box.", "Chưa có phân tích. Bấm Phân tích phía trên khung soạn."),
            issuesTitle = Lang.T("Problems raised", "Các vấn đề được nêu"),
            statusOpen = Lang.T("Open", "Chưa xử lý"),
            statusAnswered = Lang.T("Answered", "Đã trả lời"),
            statusFixed = Lang.T("Fixed", "Đã sửa"),
            diagnosisTitle = Lang.T("Diagnosis", "Chẩn đoán"),
            nextStepTitle = Lang.T("Next step", "Bước tiếp theo"),
            draftingReplies = Lang.T("Claude is drafting replies...", "Claude đang soạn gợi ý..."),
            regenerate = Lang.T("Analyze again", "Phân tích lại"),
            mainPointPlaceholder = Lang.T("Main point of the reply (optional)", "Ý chính muốn trả lời (tuỳ chọn)"),
            waitingLine = Lang.T("{0} is waiting since {1}", "{0} đang chờ từ {1}"),
            suggest = Lang.T("Analyze", "Phân tích"),
            newPostTitle = Lang.T($"New post for {_ticketKey}", $"Bài đăng mới cho {_ticketKey}"),
            subjectPlaceholder = Lang.T("Add a title", "Thêm tiêu đề"),
            reloadDraft = Lang.T("Reload draft", "Tải lại nháp"),
            reloadDraftTip = Lang.T("Reload the draft (drops your edits)", "Tải lại bản nháp (bỏ phần đã sửa)"),
            postButton = Lang.T("Post", "Đăng"),
            stillSearching = Lang.T("Looking for an older post in the channel...", "Đang tìm post cũ trong kênh..."),
            bold = Lang.T("Bold (Ctrl+B)", "Đậm (Ctrl+B)"),
            italic = Lang.T("Italic (Ctrl+I)", "Nghiêng (Ctrl+I)"),
            underline = Lang.T("Underline (Ctrl+U)", "Gạch dưới (Ctrl+U)"),
            strikethrough = Lang.T("Strikethrough", "Gạch ngang"),
            bulletList = Lang.T("Bulleted list", "Danh sách"),
            numberList = Lang.T("Numbered list", "Danh sách số"),
            quote = Lang.T("Quote", "Trích dẫn"),
            link = Lang.T("Link", "Link"),
            code = Lang.T("Insert code block", "Chèn khối code"),
            codeLanguage = Lang.T("Language", "Ngôn ngữ"),
            deleteCodeBlock = Lang.T("Delete block", "Xoá khối"),
            mention = Lang.T("Tag someone", "Tag người"),
            addImage = Lang.T("Add an image", "Thêm ảnh"),
            send = Lang.T("Send", "Gửi"),
            save = Lang.T("Save", "Lưu"),
            cancel = Lang.T("Cancel", "Huỷ"),
            replyHint = Lang.T("Enter sends, Shift+Enter adds a line", "Enter gửi, Shift+Enter xuống dòng"),
            postHint = Lang.T("Ctrl+Enter posts", "Ctrl+Enter để đăng"),
            editHint = Lang.T("Ctrl+Enter saves, Esc cancels", "Ctrl+Enter lưu, Esc huỷ"),
            replyPlaceholder = Lang.T("Write a reply...", "Viết câu trả lời..."),
            chatPlaceholder = Lang.T("Message the chat", "Nhắn vào chat"),
            postPlaceholder = Lang.T("Write the post...", "Viết nội dung bài..."),
            linkPrompt = Lang.T("Enter a link (http/https)", "Nhập link (http/https)"),
            linkAdd = Lang.T("Add", "Thêm"),
            linkInvalid = Lang.T("Only http and https links.", "Chỉ nhận link http hoặc https."),
            removeImage = Lang.T("Remove image", "Bỏ ảnh"),
            noOneMatches = Lang.T("No one matches", "Không có người phù hợp"),
            emptyReply = Lang.T("Write the reply first.", "Hãy viết câu trả lời trước."),
            emptyMessage = Lang.T("The message is empty.", "Tin đang trống."),
            imageNotSupported = ImageNotSupportedText(),
            imageTooLarge = ImageTooLargeText(),
            tooManyImages = TooManyImagesText(),
            imagesTooLargeInAll = ImagesTooLargeInAllText(),
        })!.AsObject();
        Post(labels);
    }

    // Kept messages are sent again on "ready"; the others (an image, an edit's progress) only matter to the page that asked.
    private void Keep(JsonObject message)
    {
        _kept[message["type"]!.GetValue<string>()] = message;
        Post(message);
    }

    private void Forget(string type)
    {
        _kept.Remove(type);
    }

    private void PostKeptMessages()
    {
        foreach (var type in KeptMessageOrder)
        {
            if (_kept.TryGetValue(type, out var message))
            {
                Post(message);
            }
        }
    }

    // Skipped until the page is up; "ready" sends the labels, the theme and the kept messages.
    private void Post(JsonObject message)
    {
        if (!_ready || IsDisposed || _webView.CoreWebView2 == null)
        {
            return;
        }
        _webView.CoreWebView2.PostWebMessageAsString(message.ToJsonString());
    }

    private void ApplyTheme()
    {
        BackColor = Theme.WindowBg;
        _status.ForeColor = Theme.TextSecondary;
        Post(new JsonObject { ["type"] = "theme", ["dark"] = Theme.Dark, ["colors"] = JsonSerializer.SerializeToNode(ThemeColors()) });
    }

    // Also put on the page's URL, so its first paint already has these colors.
    private static object ThemeColors()
    {
        return new
        {
            bg = ColorTranslator.ToHtml(Theme.WindowBg),
            surface = ColorTranslator.ToHtml(Theme.CardSurface),
            border = ColorTranslator.ToHtml(Theme.CardBorder),
            fg = ColorTranslator.ToHtml(Theme.TextPrimary),
            muted = ColorTranslator.ToHtml(Theme.TextSecondary),
            line = ColorTranslator.ToHtml(Theme.Divider),
            selection = Theme.Dark ? "#3a5a8c" : "#b4d2ff",
            accent = ColorTranslator.ToHtml(Theme.Accent),
            input = ColorTranslator.ToHtml(Theme.InputBg),
            inputBorder = ColorTranslator.ToHtml(Theme.InputBorder),
            btn = ColorTranslator.ToHtml(Theme.SecondaryBtn),
            btnHover = ColorTranslator.ToHtml(Theme.SecondaryBtnHover),
            btnText = ColorTranslator.ToHtml(Theme.SecondaryBtnText),
            hover = ColorTranslator.ToHtml(Theme.Hover),
        };
    }
}
