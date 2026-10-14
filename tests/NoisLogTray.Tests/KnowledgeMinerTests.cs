namespace NoisLogTray.Tests;

public class KnowledgeMinerTests
{
    [Fact]
    public void ParseResult_ReadsTheResultText()
    {
        var (succeeded, text) = KnowledgeMiner.ParseResult("""{"type":"result","is_error":false,"result":"No finding worth keeping."}""");

        Assert.True(succeeded);
        Assert.Equal("No finding worth keeping.", text);
    }

    [Fact]
    public void ParseResult_FailsWhenTheRunWasAnError()
    {
        var (succeeded, text) = KnowledgeMiner.ParseResult("""{"type":"result","is_error":true,"result":"API error"}""");

        Assert.False(succeeded);
        Assert.Equal("API error", text);
    }

    [Fact]
    public void ParseResult_FailsOnOutputThatIsNotJson()
    {
        var (succeeded, _) = KnowledgeMiner.ParseResult("Error: not logged in");

        Assert.False(succeeded);
    }

    [Fact]
    public void WithoutRetryEntry_DropsOnlyThatTicket()
    {
        var retryFile = "\uFEFFMDP-1 2\r\nMDP-22 1\r\nmdp-3 1\r\n";

        Assert.Equal("MDP-1 2\r\nMDP-22 1", KnowledgeMiner.WithoutRetryEntry(retryFile, "MDP-3"));
        Assert.Equal("", KnowledgeMiner.WithoutRetryEntry("MDP-3 1\r\n", "MDP-3"));
    }

    [Fact]
    public void MinedTickets_SeesTheAppsLogLinesTheWayItSeesTheScans()
    {
        var scanLog = "2026-10-14 12:00:00 Mining MDP-1 ... (NoisLogTray)\r\n"
            + "2026-10-14 12:00:00   No finding worth keeping.\r\n"
            + "2026-10-14 12:05:00 mine-knowledge MDP-2 from NoisLogTray FAILED (exit 1): Error\r\n";

        var minedKeys = WorktreeCleanup.MinedTickets(scanLog, "");

        Assert.Contains("MDP-1", minedKeys);
        Assert.DoesNotContain("MDP-2", minedKeys);
    }
}
