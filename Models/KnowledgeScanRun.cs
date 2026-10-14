namespace NoisLogTray;

// One run of docs\local-config\daily-knowledge-scan.ps1 read back from its log. EndedAt is its last logged line, so a
// killed run ends where its log stops; FirstLineNumber is 1-based in the whole log file.
internal sealed record KnowledgeScanRun(
    DateTime StartedAt,
    DateTime EndedAt,
    bool IsComplete,
    int FoundTicketCount,
    int MinedTicketCount,
    int RefreshedMapCount,
    IReadOnlyList<string> ProblemLines,
    IReadOnlyList<string> Lines,
    int FirstLineNumber);
