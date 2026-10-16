using System.Net;
using System.Text.Json;

namespace NoisLogTray.Tests;

public class AzureDevOpsClientTests
{
    [Fact]
    public void IsNetworkFailure_ReturnsTrue_WhenRequestGotNoResponse()
    {
        var exception = new HttpRequestException("No such host is known.");

        Assert.True(AzureDevOpsClient.IsNetworkFailure(exception));
    }

    [Fact]
    public void IsNetworkFailure_ReturnsTrue_WhenRequestTimedOut()
    {
        Assert.True(AzureDevOpsClient.IsNetworkFailure(new TaskCanceledException()));
    }

    [Fact]
    public void IsNetworkFailure_ReturnsFalse_WhenServerReturnedErrorStatus()
    {
        var exception = new HttpRequestException("503", null, HttpStatusCode.ServiceUnavailable);

        Assert.False(AzureDevOpsClient.IsNetworkFailure(exception));
    }

    [Fact]
    public void IsNetworkFailure_ReturnsFalse_WhenResponseParsingFailed()
    {
        Assert.False(AzureDevOpsClient.IsNetworkFailure(new InvalidOperationException()));
    }

    [Fact]
    public async Task GetTicketPrsAsync_ReturnsEmpty_WhenPatIsEmpty()
    {
        var lookup = await new AzureDevOpsClient(string.Empty).GetTicketPrsAsync("MDP-1");

        Assert.Empty(lookup.Prs);
        Assert.True(lookup.IsComplete);
    }

    [Fact]
    public void PullRequestUrl_PointsAtTheRepoPr()
    {
        Assert.Equal(
            "https://dev.azure.com/TeeShirtCentral/MDP/_git/Portal-Angular/pullrequest/42",
            AzureDevOpsClient.PullRequestUrl("Portal-Angular", 42));
    }

    [Fact]
    public void ParseThreads_ReadsTheEndLine_AndDropsOneBeforeTheStart()
    {
        using var doc = JsonDocument.Parse("""
            { "value": [
              { "id": 1, "status": "active",
                "threadContext": { "filePath": "/a.cs", "rightFileStart": { "line": 33, "offset": 1 }, "rightFileEnd": { "line": 36, "offset": 5 } },
                "comments": [ { "id": 1, "commentType": "text", "author": { "displayName": "Minh" }, "content": "x", "publishedDate": "2026-10-01T07:00:00Z" } ] },
              { "id": 2, "status": "active",
                "threadContext": { "filePath": "/a.cs", "leftFileStart": { "line": 10, "offset": 1 }, "leftFileEnd": { "line": 12, "offset": 1 } },
                "comments": [ { "id": 1, "commentType": "text", "author": { "displayName": "Minh" }, "content": "y", "publishedDate": "2026-10-01T07:00:00Z" } ] },
              { "id": 3, "status": "active",
                "threadContext": { "filePath": "/a.cs", "rightFileStart": { "line": 20, "offset": 1 }, "rightFileEnd": { "line": 19, "offset": 1 } },
                "comments": [ { "id": 1, "commentType": "text", "author": { "displayName": "Minh" }, "content": "z", "publishedDate": "2026-10-01T07:00:00Z" } ] },
              { "id": 4, "status": "active",
                "threadContext": { "filePath": "/a.cs", "rightFileStart": { "line": 5, "offset": 1 } },
                "comments": [ { "id": 1, "commentType": "text", "author": { "displayName": "Minh" }, "content": "w", "publishedDate": "2026-10-01T07:00:00Z" } ] }
            ] }
            """);

        var threads = AzureDevOpsClient.ParseThreads(doc.RootElement);

        Assert.Equal(36, threads[0].EndLine);
        Assert.Equal(12, threads[1].EndLine);
        Assert.True(threads[1].IsOnOldSide);
        Assert.Null(threads[2].EndLine);
        Assert.Null(threads[3].EndLine);
    }

    [Fact]
    public void ParseThreads_KeepsTheRealComments_OfFileAndPrLevelThreads()
    {
        using var doc = JsonDocument.Parse("""
            { "value": [
              { "id": 1, "status": "active",
                "threadContext": { "filePath": "/src/Foo.cs", "rightFileStart": { "line": 36, "offset": 1 } },
                "comments": [
                  { "id": 1, "commentType": "text", "author": { "displayName": "Minh" }, "content": "Fix **this**", "publishedDate": "2026-09-30T10:37:59.743Z" },
                  { "id": 2, "commentType": "system", "author": { "displayName": "Bot" }, "content": "voted", "publishedDate": "2026-09-30T10:40:00Z" },
                  { "id": 3, "commentType": "text", "isDeleted": true, "author": { "displayName": "Nam" }, "content": "gone", "publishedDate": "2026-09-30T10:41:00Z" }
                ] },
              { "id": 2, "status": "fixed", "threadContext": null,
                "comments": [ { "id": 1, "commentType": "text", "author": {}, "content": "General", "publishedDate": "2026-10-01T07:00:00Z" } ] }
            ] }
            """);

        var threads = AzureDevOpsClient.ParseThreads(doc.RootElement);

        Assert.Equal(2, threads.Count);
        var fileThread = threads[0];
        Assert.Equal("src/Foo.cs", fileThread.FilePath);
        Assert.Equal(36, fileThread.Line);
        Assert.False(fileThread.IsOnOldSide);
        Assert.True(fileThread.IsOpen);
        var comment = Assert.Single(fileThread.Comments);
        Assert.Equal("Minh", comment.Author);
        Assert.Equal("Fix **this**", comment.Content);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 10, 37, 59, 743, TimeSpan.Zero), comment.PublishedAt);
        var prThread = threads[1];
        Assert.Null(prThread.FilePath);
        Assert.Null(prThread.Line);
        Assert.Equal(PrThreadStatus.Fixed, prThread.Status);
        Assert.False(prThread.IsOpen);
        Assert.Equal("?", Assert.Single(prThread.Comments).Author);
    }

    [Fact]
    public void ParseThreads_DropsDeletedAndSystemOnlyThreads_ReadsTheOldSide_AndKeepsAThreadMissingItsStatusOrLine()
    {
        using var doc = JsonDocument.Parse("""
            { "value": [
              { "id": 1, "status": "active", "isDeleted": true,
                "comments": [ { "id": 1, "commentType": "text", "author": { "displayName": "A" }, "content": "x", "publishedDate": "2026-09-30T10:00:00Z" } ] },
              { "id": 2, "status": "active",
                "comments": [ { "id": 1, "commentType": "system", "author": { "displayName": "A" }, "content": "policy", "publishedDate": "2026-09-30T10:00:00Z" } ] },
              { "id": 3, "status": "pending",
                "threadContext": { "filePath": "/src/Old.cs", "leftFileStart": { "line": 7, "offset": 1 } },
                "comments": [ { "id": 1, "commentType": "text", "author": { "displayName": "A" }, "content": "why removed?", "publishedDate": "2026-09-30T10:00:00Z" } ] },
              { "id": 4, "status": "active" },
              { "id": 5,
                "comments": [ { "id": 1, "commentType": "text", "author": { "displayName": "A" }, "content": "no status", "publishedDate": "2026-09-30T10:00:00Z" } ] },
              { "id": 6, "status": "active",
                "threadContext": { "filePath": "/src/NoLine.cs", "rightFileStart": {} },
                "comments": [ { "id": 1, "commentType": "text", "author": { "displayName": "A" }, "content": "no line", "publishedDate": "2026-09-30T10:00:00Z" } ] }
            ] }
            """);

        var threads = AzureDevOpsClient.ParseThreads(doc.RootElement);

        Assert.Equal(new[] { 3, 5, 6 }, threads.Select(pt => pt.Id));
        Assert.Equal("src/NoLine.cs", threads[2].FilePath);
        Assert.Null(threads[2].Line);
        Assert.Equal(PrThreadStatus.Unknown, threads[1].Status);
        Assert.False(threads[1].IsOpen);
        var thread = threads[0];
        Assert.Equal(PrThreadStatus.Pending, thread.Status);
        Assert.Equal("src/Old.cs", thread.FilePath);
        Assert.Equal(7, thread.Line);
        Assert.True(thread.IsOnOldSide);
        Assert.True(thread.IsOpen);
    }

    [Fact]
    public void ToHcm_ShowsTheInstantAtUtcPlusSeven()
    {
        var hcm = Hcm.ToHcm(new DateTimeOffset(2026, 9, 30, 20, 0, 0, TimeSpan.Zero));

        Assert.Equal(TimeSpan.FromHours(7), hcm.Offset);
        Assert.Equal(new DateTime(2026, 10, 1, 3, 0, 0), hcm.DateTime);
    }
}
