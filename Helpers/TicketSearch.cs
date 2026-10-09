namespace NoisLogTray;

internal static class TicketSearch
{
    // Every whitespace-separated word must appear in the key or summary, so
    // "patch bins" finds a summary with those words in any order.
    public static bool Matches(JiraTicket ticket, string query)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var searchableText = $"{ticket.Key} {ticket.Summary}";
        return words.All(word => searchableText.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
}
