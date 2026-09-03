using NoisLogTray;

namespace NoisLogTray.Tests;

public class LeaveTests
{
    // The real find_my_requests envelope nests twice: { success, data: { success, data: [...] } }.
    private static string Envelope(params string[] items)
        => "{\"success\":true,\"data\":{\"success\":true,\"data\":["
            + string.Join(",", items)
            + "],\"pagination\":{\"page\":1,\"pageSize\":50,\"totalCount\":"
            + items.Length + ",\"totalPages\":1}}}";

    private static string Request(string from, string to, string periodType, string status)
        => $"{{\"fromDate\":\"{from}T00:00:00\",\"toDate\":\"{to}T00:00:00\","
            + $"\"periodType\":\"{periodType}\",\"status\":\"{status}\",\"leaveTypeName\":\"Nghi phep\"}}";

    [Fact]
    public void ReturnsTheDateOfAnApprovedAllDayRequest()
    {
        var dates = Leave.ParseOffDates(false,
            Envelope(Request("2026-08-31", "2026-08-31", "AllDay", "Approved")));

        Assert.Equal(new[] { new DateOnly(2026, 8, 31) }, dates);
    }

    [Fact]
    public void ExpandsAMultiDayRangeInclusively()
    {
        var dates = Leave.ParseOffDates(false,
            Envelope(Request("2026-04-28", "2026-04-30", "AllDay", "Approved")));

        Assert.Equal(new[]
        {
            new DateOnly(2026, 4, 28),
            new DateOnly(2026, 4, 29),
            new DateOnly(2026, 4, 30),
        }, dates);
    }

    [Fact]
    public void SkipsHalfDayRequestsBecauseTheyAreStillHalfAWorkingDay()
    {
        var dates = Leave.ParseOffDates(false, Envelope(
            Request("2026-03-27", "2026-03-27", "SecondHalf", "Approved"),
            Request("2026-03-28", "2026-03-28", "FirstHalf", "Approved")));

        Assert.Empty(dates);
    }

    [Fact]
    public void SkipsRequestsThatAreNotApproved()
    {
        var dates = Leave.ParseOffDates(false, Envelope(
            Request("2026-09-10", "2026-09-10", "AllDay", "Pending"),
            Request("2026-09-11", "2026-09-11", "AllDay", "Rejected"),
            Request("2026-09-12", "2026-09-12", "AllDay", "Cancelled")));

        Assert.Empty(dates);
    }

    [Fact]
    public void DeduplicatesAndSortsOverlappingRequests()
    {
        var dates = Leave.ParseOffDates(false, Envelope(
            Request("2026-09-11", "2026-09-11", "AllDay", "Approved"),
            Request("2026-09-10", "2026-09-11", "AllDay", "Approved")));

        Assert.Equal(new[] { new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 11) }, dates);
    }

    [Fact]
    public void ReturnsEmptyOnAForbiddenEnvelopeSoATimesheetScopedKeyDegrades()
    {
        var dates = Leave.ParseOffDates(false,
            "{\"success\":false,\"error\":{\"code\":\"FORBIDDEN\",\"message\":\"khong co quyen\"}}");

        Assert.Empty(dates);
    }

    [Fact]
    public void ReturnsEmptyOnAnMcpLevelError()
    {
        Assert.Empty(Leave.ParseOffDates(true, Envelope(
            Request("2026-08-31", "2026-08-31", "AllDay", "Approved"))));
    }

    [Fact]
    public void ReturnsEmptyOnMalformedJson()
    {
        Assert.Empty(Leave.ParseOffDates(false, "not json"));
        Assert.Empty(Leave.ParseOffDates(false, ""));
        Assert.Empty(Leave.ParseOffDates(false, null));
    }

    [Fact]
    public void ReturnsEmptyWhenTheInnerEnvelopeIsMissing()
    {
        // The outer success is true but data is the array directly (an older/other shape).
        Assert.Empty(Leave.ParseOffDates(false,
            "{\"success\":true,\"data\":[" + Request("2026-08-31", "2026-08-31", "AllDay", "Approved") + "]}"));
    }
}
