namespace NoisLogTray;

// Optional overrides for resolving/targeting the workbook (mirror the TSC_GRAPH_*
// env vars). Null = use defaults / the /shares resolve.
internal sealed record GraphTscOptions(
    string? DriveId = null,
    string? ItemId = null,
    string? ShareUrl = null,
    string? Worksheet = null);
