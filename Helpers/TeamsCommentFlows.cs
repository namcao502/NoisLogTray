using System.Net.Http;

namespace NoisLogTray;

// The "check cmt" flow: a lead asks to check PR comments, the prompt goes to the ticket's Claude, and the flow is done when no PR thread is open.
internal static class TeamsCommentFlows
{
    internal const int MaxPromptChars = 500;

    internal static bool IsSafePrompt(string prompt)
    {
        var trimmed = prompt.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxPromptChars)
        {
            return false;
        }

        foreach (var character in trimmed)
        {
            if (character == '\'' || character == '"' || character == '`' || character == '$' || character == '\\' || char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }

    internal static TeamsCommentFlow Next(TeamsCommentFlow flow, ClaudeHookEvent hookEvent)
    {
        if (flow.Stage == TeamsCommentStage.HandedOff && hookEvent.EventName == "UserPromptSubmit" && hookEvent.AgentId == null)
        {
            return flow with { PromptSubmitted = true };
        }

        return flow;
    }

    internal static TeamsCommentFlow AfterTurn(TeamsCommentFlow flow, int? openThreadCount)
    {
        if (openThreadCount == null || openThreadCount.Value == 0)
        {
            return flow with { Stage = TeamsCommentStage.Done, PromptSubmitted = false, OpenThreadCount = 0 };
        }

        return flow with { Stage = TeamsCommentStage.HandedOff, PromptSubmitted = false, OpenThreadCount = openThreadCount.Value };
    }

    internal static TeamsCommentFlow? OnAsk(TeamsCommentFlow? current, TeamsCommentFlow? asked, bool lastIsMine)
    {
        if (asked != null)
        {
            if (current == null || (current.Stage == TeamsCommentStage.Asked && current.AskMessageId != asked.AskMessageId))
            {
                return asked;
            }

            return current;
        }

        if (current != null && lastIsMine && (current.Stage == TeamsCommentStage.Asked || current.Stage == TeamsCommentStage.Done))
        {
            return null;
        }

        return current;
    }

    internal static async Task<int?> CountOpenThreadsAsync(IAzureDevOpsClient? ado, IReadOnlyList<TicketPr> prs, CancellationToken cancellationToken)
    {
        if (ado == null)
        {
            return null;
        }

        var openThreadCount = 0;
        try
        {
            foreach (var pr in prs.Where(tp => tp.Status != "abandoned"))
            {
                var threads = await ado.GetThreadsAsync(pr.Repository, pr.PullRequestId, cancellationToken);
                openThreadCount += threads.Count(pt => pt.IsOpen);
            }
        }
        catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is OperationCanceledException)
        {
            return null;
        }

        return openThreadCount;
    }
}
