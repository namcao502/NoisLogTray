namespace NoisLogTray;

// Pure edits of the per-ticket unseen PR comment alerts; every method returns a new dictionary.
internal static class PrCommentAlerts
{
    // Adds to the ticket's unseen count; the latest PR url wins, so the key opens the most recently commented PR.
    internal static Dictionary<string, PrCommentAlert> Add(
        IReadOnlyDictionary<string, PrCommentAlert> alerts, string ticketKey, int newCommentCount, string prUrl, DateTimeOffset atHcm)
    {
        var result = new Dictionary<string, PrCommentAlert>(alerts);
        if (newCommentCount <= 0) return result;

        var previousCount = 0;
        if (alerts.TryGetValue(ticketKey, out var previous)) previousCount = previous.NewCommentCount;
        result[ticketKey] = new PrCommentAlert(ticketKey, previousCount + newCommentCount, prUrl, atHcm);
        return result;
    }

    internal static Dictionary<string, PrCommentAlert> Remove(IReadOnlyDictionary<string, PrCommentAlert> alerts, string ticketKey)
    {
        var result = new Dictionary<string, PrCommentAlert>(alerts);
        result.Remove(ticketKey);
        return result;
    }

    internal static Dictionary<string, PrCommentAlert> KeepOnly(IReadOnlyDictionary<string, PrCommentAlert> alerts, ISet<string> boardKeys)
    {
        return alerts.Where(kv => boardKeys.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    internal static bool AreSame(IReadOnlyDictionary<string, PrCommentAlert> left, IReadOnlyDictionary<string, PrCommentAlert> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var (ticketKey, alert) in left)
        {
            if (!right.TryGetValue(ticketKey, out var other) || other != alert) return false;
        }
        return true;
    }
}
