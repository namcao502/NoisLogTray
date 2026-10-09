namespace NoisLogTray;

internal interface IAzureDevOpsClient
{
    Task<PrLookupResult> GetTicketPrsAsync(string ticketKey);
}
