namespace NoisLogTray;

// One weekday's logging coverage, read back from the two destinations for the weekly
// check window. HrmHours is the total hours logged that day (null = unknown/not read);
// TscTicket is the ticket text in the shared workbook (null/empty = nothing logged).
// IsOff = approved full-day leave, where zero hours is the correct answer.
internal sealed record DayCoverage(DateOnly Date, double? HrmHours, string? TscTicket, bool IsOff = false);
