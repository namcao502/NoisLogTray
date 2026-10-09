using System.Text.RegularExpressions;

namespace NoisLogTray;

// Normalizes free-text ticket input into canonical MDP-xxxx keys. Accepts
// shorthand digits ("1234"), full keys ("MDP-1234", any case), separated by
// commas/spaces/semicolons. Order-preserving dedupe; unrecognized tokens are
// returned separately so the UI can flag them.
internal static class TicketParser
{
    private static readonly Regex Digits = new(@"^\d+$", RegexOptions.Compiled);
    private static readonly Regex Full = new(@"^MDP-(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex KeyInText = new(@"\bMDP-(\d+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly char[] Separators = { ',', ' ', ';', '\t', '\r', '\n' };

    internal static (IReadOnlyList<string> Tickets, IReadOnlyList<string> Invalid) Parse(string? raw)
    {
        var tickets = new List<string>();
        var invalid = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return (tickets, invalid);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? normalized = null;
            if (Digits.IsMatch(token))
            {
                normalized = $"MDP-{token}";
            }
            else
            {
                var m = Full.Match(token);
                if (m.Success) normalized = $"MDP-{m.Groups[1].Value}";
            }

            if (normalized is null) invalid.Add(token);
            else if (seen.Add(normalized)) tickets.Add(normalized);
        }

        return (tickets, invalid);
    }

    // One key from "1234", "MDP-1234" or a pasted Jira link.
    internal static bool TryParseKey(string? input, out string ticketKey)
    {
        var trimmed = input?.Trim() ?? "";
        var match = KeyInText.Match(trimmed);
        ticketKey = "";
        if (Digits.IsMatch(trimmed)) ticketKey = $"MDP-{trimmed}";
        else if (match.Success) ticketKey = $"MDP-{match.Groups[1].Value}";
        return ticketKey.Length != 0;
    }
}
