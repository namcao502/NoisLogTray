using System.Net;

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
}
