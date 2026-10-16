namespace NoisLogTray;

// Claude's reading of a Teams conversation: what people raised, what is wrong, what to do next, and replies to pick from.
internal sealed record TeamsAnalysis(IReadOnlyList<TeamsIssue> Issues, string Diagnosis, string NextStep, IReadOnlyList<string> Replies);
