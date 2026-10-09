namespace NoisLogTray;

internal static class TicketSort
{
    // Earliest due first, undated last, then by key, so the order is stable across reloads.
    internal static List<T> ByDue<T>(IEnumerable<T> items, Func<T, JiraTicket> ticketOf)
    {
        return items
            .OrderBy(item => ticketOf(item).DueDate == null)
            .ThenBy(item => ticketOf(item).DueDate, StringComparer.Ordinal)
            .ThenBy(item => ticketOf(item).Key, StringComparer.Ordinal)
            .ToList();
    }
}
