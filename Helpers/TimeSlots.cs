namespace NoisLogTray;

// Lays tickets out across the 9:00-18:00 workday (lunch 12:00-13:00 excluded).
// Durations can be even (default) or explicit per-ticket minutes; a segment that
// straddles lunch splits into two rows. Times round to 5-minute boundaries.
internal static class TimeSlots
{
    private const int MorningStart = 9 * 60;    // 540
    private const int MorningEnd = 12 * 60;     // 720
    private const int AfternoonStart = 13 * 60; // 780
    private const int AfternoonEnd = 18 * 60;   // 1080
    private const int MorningMinutes = MorningEnd - MorningStart; // 180 work minutes before lunch
    internal const int TotalWorkMinutes = MorningMinutes + (AfternoonEnd - AfternoonStart); // 480

    private static string MinutesToTime(int minutes) => $"{minutes / 60:D2}:{minutes % 60:D2}";

    // JS Math.round rounds .5 toward +Infinity; all inputs here are positive, so
    // MidpointRounding.AwayFromZero matches.
    private static int RoundTo5(int minutes) => (int)Math.Round(minutes / 5.0, MidpointRounding.AwayFromZero) * 5;

    // Even split across N tickets: uniform minutes, with the last ticket absorbing the
    // remainder so the day always ends exactly at 18:00 (matches the original behavior).
    internal static IReadOnlyList<int> EvenSplit(int ticketCount)
    {
        var per = TotalWorkMinutes / ticketCount;
        var minutes = new int[ticketCount];
        for (var i = 0; i < ticketCount; i++) minutes[i] = per;
        minutes[ticketCount - 1] = TotalWorkMinutes - per * (ticketCount - 1);
        return minutes;
    }

    // The slots for one ticket in an even split of N (unchanged public behavior).
    internal static IReadOnlyList<TimeSlot> Get(int ticketCount, int ticketIndex)
        => Get(EvenSplit(ticketCount), ticketIndex);

    // The slots for one ticket given every ticket's duration in minutes, laid out
    // sequentially from 09:00 skipping lunch. Durations need not fill the day (partial
    // day allowed) - time left after the last ticket is simply not emitted.
    internal static IReadOnlyList<TimeSlot> Get(IReadOnlyList<int> minutesPerTicket, int ticketIndex)
    {
        var startOffset = 0;
        for (var i = 0; i < ticketIndex; i++) startOffset += minutesPerTicket[i];
        var endOffset = startOffset + minutesPerTicket[ticketIndex];
        return SegmentsFor(startOffset, endOffset);
    }

    // Map a [startOffset, endOffset) window of work minutes (0 = 09:00, 180 = the lunch
    // boundary, 480 = 18:00) to clock slots. The boundary at 180 reads as 12:00 when it
    // is an end but 13:00 when it is a start, so a segment never includes the lunch hour.
    private static IReadOnlyList<TimeSlot> SegmentsFor(int startOffset, int endOffset)
    {
        if (endOffset <= MorningMinutes) // entirely morning (end at the boundary -> 12:00)
            return new[] { Slot(MorningStart + startOffset, MorningStart + endOffset) };

        if (startOffset >= MorningMinutes) // entirely afternoon (start at the boundary -> 13:00)
            return new[] { Slot(AfternoonStart + (startOffset - MorningMinutes), AfternoonStart + (endOffset - MorningMinutes)) };

        // Straddles lunch: morning tail + afternoon head.
        return new[]
        {
            Slot(MorningStart + startOffset, MorningEnd),
            Slot(AfternoonStart, AfternoonStart + (endOffset - MorningMinutes)),
        };
    }

    private static TimeSlot Slot(int startClock, int endClock)
        => new(MinutesToTime(RoundTo5(startClock)), MinutesToTime(RoundTo5(endClock)));
}
