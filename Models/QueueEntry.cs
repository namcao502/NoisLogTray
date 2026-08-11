namespace NoisLogTray;

// One queued day. Minutes, when set, is aligned 1:1 with Tickets and gives each
// ticket's HRM duration; null means the default even split (see TimeSlots.EvenSplit).
internal sealed record QueueEntry(string Date, IReadOnlyList<string> Tickets,
    IReadOnlyList<int>? Minutes = null);
