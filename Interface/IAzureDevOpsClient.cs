namespace NoisLogTray;

internal interface IAzureDevOpsClient
{
    Task<PrLookupResult> GetTicketPrsAsync(string ticketKey);

    Task<IReadOnlyList<PrThread>> GetThreadsAsync(string repository, int pullRequestId, CancellationToken cancellationToken);
}
