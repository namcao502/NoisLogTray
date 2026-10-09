namespace NoisLogTray;

// Tracking keys are "<ticket>|<branch label>|<PR id>": one entry per PR, since a branch can have several.
internal static class PrTracker
{
    // Updates tracking in place; a PR seen for the first time only records its baseline.
    internal static List<PrNotification> DetectChanges(
        Dictionary<string, TrackedPr> tracking,
        string ticketKey,
        IEnumerable<PrStatus> prs)
    {
        var notifications = new List<PrNotification>();
        foreach (var pr in prs)
        {
            var trackingKey = $"{ticketKey}|{pr.Branch}|{pr.PullRequestId}";
            var current = new TrackedPr(pr.Status, pr.CommentCount, pr.Repository);

            if (tracking.TryGetValue(trackingKey, out var previous))
            {
                var isMerged = previous.Status != "completed" && pr.Status == "completed";
                var newCommentCount = Math.Max(0, pr.CommentCount - previous.CommentCount);
                if (isMerged || newCommentCount != 0)
                {
                    notifications.Add(new PrNotification(pr, isMerged, newCommentCount));
                }
            }

            tracking[trackingKey] = current;
        }

        return notifications;
    }

    // Pre-PR-id keys ("<ticket>|<branch>") mixed several PRs into one entry; dropped so they don't feed AreAllPrsClosed.
    internal static bool RemoveLegacyKeys(Dictionary<string, TrackedPr> tracking)
    {
        var legacyKeys = tracking.Keys.Where(key => key.Split('|').Length != 3).ToList();
        foreach (var key in legacyKeys)
        {
            tracking.Remove(key);
        }

        return legacyKeys.Count != 0;
    }

    // The "<TICKET>-dev" PRs (label "dev", one per repo: BE and FE) target development. The ticket is ready for
    // the deploy/test steps once every dev PR that wasn't abandoned is merged, so a merged BE PR alone isn't enough.
    internal static bool IsDevPrMerged(IReadOnlyDictionary<string, TrackedPr> tracking, string ticketKey)
    {
        var devPrs = tracking
            .Where(entry => entry.Key.StartsWith($"{ticketKey}|dev|") && entry.Value.Status != "abandoned")
            .Select(entry => entry.Value)
            .ToList();
        return devPrs.Count != 0 && devPrs.All(tp => tp.Status == "completed");
    }

    // A ticket with no PR tracked yet is not "closed": its PR may not exist yet.
    internal static bool AreAllPrsClosed(IReadOnlyDictionary<string, TrackedPr> tracking, string ticketKey)
    {
        var ticketPrs = tracking
            .Where(entry => entry.Key.StartsWith($"{ticketKey}|"))
            .Select(entry => entry.Value)
            .ToList();

        return ticketPrs.Count != 0
            && ticketPrs.All(tp => tp.Status == "completed" || tp.Status == "abandoned");
    }
}
