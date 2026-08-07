using NoisLogTray;

namespace NoisLogTray.Tests;

// Mirrors __tests__/hrm-timesheet-args.test.ts.
public class TimesheetTests
{
    private static string Wrap(string json) => json;

    [Fact]
    public void TreatsBusinessSuccessFalseAsFailureWithCodeAndMessage()
    {
        var env = Timesheet.ParseToolEnvelope(false,
            "{\"success\":false,\"data\":\"0\",\"error\":{\"code\":\"FUTURE_STOP\",\"message\":\"no future\"}}");
        Assert.False(env.Success);
        Assert.Equal("FUTURE_STOP: no future", env.Error);
        Assert.Equal("FUTURE_STOP", env.Code);
    }

    [Fact]
    public void SurfacesTheCodeSoOverlapCanBeHandledAsASkip()
    {
        var env = Timesheet.ParseToolEnvelope(false,
            "{\"success\":false,\"data\":\"0\",\"error\":{\"code\":\"LOGTIME_OVERLAP\",\"message\":\"trung\"}}");
        Assert.Equal("LOGTIME_OVERLAP", env.Code);
    }

    [Fact]
    public void TreatsSuccessTrueAsSuccess()
    {
        var env = Timesheet.ParseToolEnvelope(false, "{\"success\":true,\"data\":\"id-1\",\"error\":null}");
        Assert.True(env.Success);
        Assert.Null(env.Error);
        Assert.Null(env.Code);
    }

    [Fact]
    public void TreatsAnMcpLevelIsErrorAsAFailure()
    {
        var env = Timesheet.ParseToolEnvelope(true, "boom");
        Assert.False(env.Success);
        Assert.Equal("boom", env.Error);
    }

    [Fact]
    public void MapsTicketDateAndHhMmSsTimes()
    {
        var args = Timesheet.BuildArgs("P1", "MDP-7706", "2026-07-01", new TimeSlot("09:00", "12:00"));
        Assert.Equal("P1", args["projectId"]);
        Assert.Equal("MDP-7706", args["taskId"]);
        Assert.Equal("2026-07-01", args["workDate"]);
        Assert.Equal("09:00:00", args["startTime"]);
        Assert.Equal("12:00:00", args["stopTime"]);
    }

    [Fact]
    public void LeavesAutoFilledFieldsNullAndMarksBillable()
    {
        var args = Timesheet.BuildArgs("P1", "MDP-7706", "2026-07-01", new TimeSlot("09:00", "12:00"));
        Assert.Null(args["description"]);
        Assert.Null(args["issueType"]);
        Assert.Null(args["comment"]);
        Assert.Null(args["projectStageId"]);
        Assert.Equal(true, args["isBillable"]);
    }

    [Fact]
    public void SendsANullIdempotencyKey()
    {
        var args = Timesheet.BuildArgs("P1", "MDP-7706", "2026-07-01", new TimeSlot("09:00", "12:00"));
        Assert.True(args.ContainsKey("idempotencyKey"));
        Assert.Null(args["idempotencyKey"]);
    }

    [Fact]
    public void ParseDayHoursReadsTotalHours()
    {
        var h = Timesheet.ParseDayHours(false, "{\"success\":true,\"data\":{\"totalHours\":8.0,\"totalSeconds\":28800}}");
        Assert.Equal(8.0, h);
    }

    [Fact]
    public void ParseDayHoursReadsRealGetMyDayLogsShape()
    {
        // The actual get_my_day_logs envelope (workDate + projectId=null).
        var text = "{\"success\":true,\"data\":{\"workDate\":\"2025-07-01\",\"totalSeconds\":28800,\"totalHours\":8," +
                   "\"intervals\":[{\"taskId\":\"MDP-4243\",\"start\":\"08:30:00\",\"stop\":\"12:00:00\",\"durationSeconds\":12600}]}}";
        Assert.Equal(8.0, Timesheet.ParseDayHours(false, text));
    }

    [Fact]
    public void ParseDayHoursFallsBackToTotalSeconds()
    {
        var h = Timesheet.ParseDayHours(false, "{\"success\":true,\"data\":{\"totalSeconds\":18000}}");
        Assert.Equal(5.0, h);
    }

    [Fact]
    public void ParseDayHoursIsCaseInsensitive()
    {
        var h = Timesheet.ParseDayHours(false, "{\"success\":true,\"data\":{\"TotalHours\":7.5}}");
        Assert.Equal(7.5, h);
    }

    [Theory]
    [InlineData(true, "{\"success\":true,\"data\":{\"totalHours\":8}}")] // MCP-level error
    [InlineData(false, "{\"success\":false,\"error\":{\"code\":\"X\"}}")] // business failure
    [InlineData(false, "{\"success\":true,\"data\":{}}")]                 // no hours field
    [InlineData(false, "not json")]                                       // unparseable
    [InlineData(false, "")]                                               // empty
    public void ParseDayHoursReturnsNullWhenUnavailable(bool isError, string text)
    {
        Assert.Null(Timesheet.ParseDayHours(isError, text));
    }
}
