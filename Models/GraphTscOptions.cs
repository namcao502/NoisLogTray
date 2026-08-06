namespace NoisLogTray;

// Optional overrides for resolving/targeting the workbook (mirror the TSC_GRAPH_*
// env vars). Null = use defaults / the /shares resolve. Columns is the per-user set
// of worksheet columns to write (null/empty = the M,J default in TscCells).
internal sealed record GraphTscOptions(
    string? DriveId = null,
    string? ItemId = null,
    string? ShareUrl = null,
    string? Worksheet = null,
    IReadOnlyList<string>? Columns = null);
