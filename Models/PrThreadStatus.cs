namespace NoisLogTray;

// An Azure DevOps PR thread's status; Unknown also covers a missing or new value.
internal enum PrThreadStatus
{
    Unknown,
    Active,
    Pending,
    Fixed,
    WontFix,
    Closed,
    ByDesign,
}
