using System.Text.Json.Nodes;

namespace NoisLogTray;

// What the user sends from the page: a reply, the ticket's post, an edit or a delete of their own message.
internal sealed partial class TeamsThreadView
{
    private bool _sending;
    private bool _posting;
    private bool _changingMessage; // an edit or delete is on its way
    private bool _handingOff; // a double click must not hand the same message over twice
    private bool _handingComments;
    private string? _postWarnedContent; // the subject and body the last "posting again" warning was about
    private bool _postUncertain;

    private async Task SendAsync(TeamsDraft draft)
    {
        if (_sending || IsDisposed)
        {
            return; // the page disables Send until this one answers
        }
        var found = _found;
        if (found == null || found.Thread == null)
        {
            ShowSendState("failed", ThreadNotLoadedText());
            return;
        }

        _sending = true;
        ShowSendState("sending", null);
        TeamsReplyResult reply;
        try
        {
            reply = await _sendReply(found.Thread, draft, _changeCancel.Token);
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
            return; // the tab closed while sending
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[teams] replying on {_ticketKey} failed: {ex.Message}");
            reply = new TeamsReplyResult(TeamsReplyState.Failed, null, ex.Message);
        }
        finally
        {
            _sending = false;
        }

        if (IsDisposed)
        {
            return;
        }
        if (reply.State == TeamsReplyState.Uncertain)
        {
            ShowSendState("uncertain", null);
            return;
        }
        if (reply.State != TeamsReplyState.Sent)
        {
            ShowSendState("failed", reply.Error);
            return;
        }
        ShowSendState("sent", null);
        ShowAfter(reply.After);
    }

    // Already posted, or a post that may have gone up: the first Post only warns, the same content posted again goes out.
    private async Task PostToTeamsAsync(string subject, TeamsDraft draft)
    {
        if (_posting || IsDisposed)
        {
            return;
        }
        var trimmedSubject = subject.Trim();
        if (trimmedSubject.Length == 0)
        {
            ShowPostState("failed", Lang.T("Add the title first.", "Thêm tiêu đề trước."));
            return;
        }
        if (TeamsCompose.IsEmptyHtml(draft.Html) && draft.Images.Count == 0)
        {
            ShowPostState("failed", Lang.T("Write the post below the title.", "Viết nội dung bên dưới tiêu đề."));
            return;
        }
        var notPosted = _notPosted;
        var hasChannel = notPosted != null && notPosted.Channels.Any(tc => tc.Id == notPosted.ChannelId);
        if (!hasChannel)
        {
            ShowPostState("failed", Lang.T("Pick the channel to post in first.", "Chọn kênh đăng trước."));
            return;
        }

        var content = trimmedSubject + "\n" + draft.Html;
        var postedAt = _loadDraft().PostedAt;
        if ((postedAt != null || _postUncertain) && _postWarnedContent != content)
        {
            _postWarnedContent = content;
            ShowPostState("warning", postedAt != null
                ? Lang.T($"{_ticketKey} was already posted on {PostedWhen(postedAt.Value)}. Post again to post it again.",
                    $"{_ticketKey} đã đăng lúc {PostedWhen(postedAt.Value)}. Bấm Đăng lần nữa để đăng lại.")
                : Lang.T("The last post may already be in Teams. Check there, then Post again to post anyway.",
                    "Bài vừa rồi có thể đã lên Teams. Xem trong Teams trước, rồi bấm Đăng lần nữa nếu vẫn muốn đăng."));
            return;
        }

        _postWarnedContent = null;
        _postUncertain = false;
        _posting = true;
        ShowPostState("posting", null);
        TeamsReplyResult result;
        try
        {
            result = await _post(trimmedSubject, draft, _changeCancel.Token);
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
            return; // the tab closed while posting
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[teams] posting {_ticketKey} failed: {ex.Message}");
            result = new TeamsReplyResult(TeamsReplyState.Failed, null, ex.Message);
        }
        finally
        {
            _posting = false;
        }

        if (IsDisposed)
        {
            return;
        }
        if (result.State == TeamsReplyState.Uncertain)
        {
            _postUncertain = true;
            ShowPostState("uncertain", null);
            _ = ReadAsync(); // a post it finds opens the thread
            return;
        }
        if (result.State != TeamsReplyState.Sent)
        {
            ShowPostState("failed", result.Error);
            return;
        }
        ShowPostState("sent", null);
        Posted?.Invoke(draft);
        _ = WaitForPostAsync();
    }

    private async Task EditAsync(string messageId, string? subject, TeamsDraft draft)
    {
        if (IsDisposed)
        {
            return;
        }
        var found = _found;
        if (found == null || found.Thread == null)
        {
            PostChangeState("editState", messageId, "failed", ThreadNotLoadedText());
            return;
        }
        if (_changingMessage)
        {
            PostChangeState("editState", messageId, "failed", ChangeRunningText());
            return;
        }

        _changingMessage = true;
        PostChangeState("editState", messageId, "saving", null);
        TeamsReplyResult result;
        try
        {
            result = await _edit(found.Thread, messageId, subject, draft, _changeCancel.Token);
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
            return; // the tab closed while saving
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[teams] editing a message on {_ticketKey} failed: {ex.Message}");
            result = new TeamsReplyResult(TeamsReplyState.Failed, null, ex.Message);
        }
        finally
        {
            _changingMessage = false;
        }

        if (IsDisposed)
        {
            return;
        }
        ApplyChangeResult("editState", "saved", messageId, result, Lang.T("Could not save.", "Không lưu được."));
    }

    private void ConfirmDelete(string messageId)
    {
        if (IsDisposed)
        {
            return;
        }
        var isRoot = _found != null && _found.Thread != null && _found.Thread.RootId == messageId;
        var isConfirmed = ConfirmDialog.Ask(FindForm(),
            isRoot ? Lang.T("Delete the post?", "Xoá bài đăng?") : Lang.T("Delete the reply?", "Xoá trả lời?"),
            Lang.T("It is deleted in Teams for everyone in the channel. This cannot be undone.",
                "Tin sẽ bị xoá trên Teams với mọi người trong kênh. Không hoàn tác được."),
            Lang.T("Delete", "Xoá"), destructive: true);
        if (!isConfirmed)
        {
            PostChangeState("deleteState", messageId, "cancelled", null);
            return;
        }
        _ = DeleteAsync(messageId);
    }

    private async Task DeleteAsync(string messageId)
    {
        if (IsDisposed)
        {
            return;
        }
        var found = _found;
        if (found == null || found.Thread == null)
        {
            PostChangeState("deleteState", messageId, "failed", ThreadNotLoadedText());
            return;
        }
        if (_changingMessage)
        {
            PostChangeState("deleteState", messageId, "failed", ChangeRunningText());
            return;
        }

        _changingMessage = true;
        PostChangeState("deleteState", messageId, "deleting", null);
        TeamsReplyResult result;
        try
        {
            result = await _delete(found.Thread, messageId, _changeCancel.Token);
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
            return; // the tab closed while deleting
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[teams] deleting a message on {_ticketKey} failed: {ex.Message}");
            result = new TeamsReplyResult(TeamsReplyState.Failed, null, ex.Message);
        }
        finally
        {
            _changingMessage = false;
        }

        if (IsDisposed)
        {
            return;
        }
        ApplyChangeResult("deleteState", "deleted", messageId, result, Lang.T("Could not delete.", "Không xoá được."));
    }

    // Only another person's message of the thread on screen; the host does the work and TrayApp shows the terminal.
    private async Task HandToClaudeAsync(string messageId)
    {
        var found = _found;
        if (IsDisposed || found == null || found.Thread == null)
        {
            return;
        }
        var message = found.Thread.Messages.FirstOrDefault(tm => tm.Id == messageId);
        if (message == null || string.Equals(message.SenderId, found.MyId, StringComparison.Ordinal) || _handingOff)
        {
            return;
        }

        string? error;
        _handingOff = true;
        try
        {
            error = await _handToClaude(found.Thread, messageId, _changeCancel.Token);
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
            return; // the tab closed while handing over
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[teams] handing a message of {_ticketKey} to Claude failed: {ex.Message}");
            error = Lang.T("Could not hand it to Claude (see log).", "Không giao được cho Claude (xem log).");
        }
        finally
        {
            _handingOff = false;
        }

        if (IsDisposed)
        {
            return;
        }
        Post(new JsonObject
        {
            ["type"] = "handOffState",
            ["messageId"] = messageId,
            ["state"] = error == null ? "sent" : "failed",
            ["error"] = error,
        });
    }

    private string? ShownConversationKey()
    {
        if (_found == null || _found.Thread == null)
        {
            return null;
        }
        return TeamsChat.ConversationKey(_found.Thread);
    }

    // A flow of another conversation (or none) clears the card; the page also gets a transient clear.
    internal void SetCommentFlow(TeamsCommentFlow? flow)
    {
        if (flow != null && flow.ConversationKey == ShownConversationKey())
        {
            Keep(BuildCommentFlowMessage(flow));
            return;
        }
        Forget("commentFlow");
        Post(new JsonObject { ["type"] = "commentFlow", ["conversationKey"] = "", ["stage"] = "none" });
    }

    private async Task HandCommentsToClaudeAsync(string conversationKey, string prompt)
    {
        if (IsDisposed || _handingComments)
        {
            return;
        }

        string? error;
        _handingComments = true;
        try
        {
            error = await _handCommentsToClaude(conversationKey, prompt, _changeCancel.Token);
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
            return; // the tab closed while handing over
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[teams] handing the PR comments of {_ticketKey} to Claude failed: {ex.Message}");
            error = Lang.T("Could not hand it to Claude (see log).", "Không giao được cho Claude (xem log).");
        }
        finally
        {
            _handingComments = false;
        }

        if (IsDisposed)
        {
            return;
        }
        Post(new JsonObject { ["type"] = "commentHandOffState", ["state"] = error == null ? "sent" : "failed", ["error"] = error });
    }

    private void ApplyChangeResult(string type, string doneState, string messageId, TeamsReplyResult result, string failureText)
    {
        if (result.State == TeamsReplyState.Uncertain)
        {
            PostChangeState(type, messageId, "uncertain", Lang.T("Not sure it went through - checking Teams.",
                "Không chắc đã xong - đang kiểm lại trên Teams."));
            _ = ReadAsync();
            return;
        }
        if (result.State != TeamsReplyState.Sent)
        {
            PostChangeState(type, messageId, "failed", result.Error == null ? failureText : $"{failureText} {result.Error}");
            return;
        }
        PostChangeState(type, messageId, doneState, null);
        ShowAfter(result.After);
    }

    // The service re-reads after a change; without that read, this view reads on its own.
    private void ShowAfter(TeamsThreadResult? after)
    {
        if (after == null)
        {
            _ = ReadAsync();
            return;
        }
        _lastReadAtUtc = DateTime.UtcNow;
        ApplyResult(after);
        // A read that started before the change went out would put the thread back without it.
        if (_reading)
        {
            _readAgain = true;
        }
    }

    // Not kept: an edit's or delete's progress only matters to the page that asked for it.
    private void PostChangeState(string type, string messageId, string state, string? error)
    {
        Post(new JsonObject { ["type"] = type, ["messageId"] = messageId, ["state"] = state, ["error"] = error });
    }

    private static string ThreadNotLoadedText()
    {
        return Lang.T("The thread is not loaded.", "Thread chưa được tải.");
    }

    private static string ChangeRunningText()
    {
        return Lang.T("Another change is still being sent.", "Đang gửi một thay đổi khác.");
    }

    private static string PostedWhen(DateTimeOffset postedAt)
    {
        return $"{Lang.ShortDate(postedAt.DateTime)} {postedAt:HH:mm}";
    }
}
