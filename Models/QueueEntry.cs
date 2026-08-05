namespace NoisLogTray;

internal sealed record QueueEntry(string Date, IReadOnlyList<string> Tickets);
