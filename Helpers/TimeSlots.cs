namespace NoisLogTray;

// Divides the 9:00-18:00 workday (lunch 12:00-13:00 excluded) evenly across N
// tickets. Direct port of lib/time-slots.ts. A slot that straddles lunch splits
// into two segments (morning tail + afternoon head).
internal static class TimeSlots
{
    private const int MorningStart = 9 * 60;    // 540
    private const int MorningEnd = 12 * 60;     // 720
    private const int AfternoonStart = 13 * 60; // 780
    private const int AfternoonEnd = 18 * 60;   // 1080
    private const int TotalWorkMinutes = (MorningEnd - MorningStart) + (AfternoonEnd - AfternoonStart); // 480

    private static string MinutesToTime(int minutes) => $"{minutes / 60:D2}:{minutes % 60:D2}";

    // JS Math.round rounds .5 toward +Infinity; all inputs here are positive, so
    // MidpointRounding.AwayFromZero matches.
    private static int RoundTo5(int minutes) => (int)Math.Round(minutes / 5.0, MidpointRounding.AwayFromZero) * 5;

    private static int WorkOffsetToClock(int offset) =>
        offset <= MorningEnd - MorningStart
            ? MorningStart + offset
            : AfternoonStart + (offset - (MorningEnd - MorningStart));

    internal static IReadOnlyList<TimeSlot> Get(int ticketCount, int ticketIndex)
    {
        var perTicket = TotalWorkMinutes / ticketCount;
        var isLast = ticketIndex == ticketCount - 1;
        var startOffset = perTicket * ticketIndex;
        var endOffset = isLast ? TotalWorkMinutes : perTicket * (ticketIndex + 1);

        var clockStart = RoundTo5(WorkOffsetToClock(startOffset));
        var clockEnd = RoundTo5(WorkOffsetToClock(endOffset));

        if (clockStart < MorningEnd && clockEnd > AfternoonStart)
            return new[]
            {
                new TimeSlot(MinutesToTime(clockStart), MinutesToTime(MorningEnd)),
                new TimeSlot(MinutesToTime(AfternoonStart), MinutesToTime(clockEnd)),
            };

        if (clockStart < MorningEnd && clockEnd > MorningEnd && clockEnd <= AfternoonStart)
            return new[] { new TimeSlot(MinutesToTime(clockStart), MinutesToTime(MorningEnd)) };

        return new[] { new TimeSlot(MinutesToTime(clockStart), MinutesToTime(clockEnd)) };
    }
}
