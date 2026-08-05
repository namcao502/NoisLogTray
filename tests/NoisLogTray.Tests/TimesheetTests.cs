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
}
