using System.Text.RegularExpressions;

namespace NoisLogTray;

// Pure TSC cell/date helpers (port of lib/tsc-cells.ts). Column M is the primary
// target; column J mirrors it. row = 2 (header rows) + dayOfYear.
internal static class TscCells
{
    internal const int HeaderRows = 2;

    // The same ticket value is written to every column here (M primary, J mirror).
    internal static readonly string[] TargetColumns = { "M", "J" };

    internal const string ExcelUrl =
        "https://tscmiami0-my.sharepoint.com/:x:/r/personal/dave_markert_tscmiami_com/_layouts/15/doc2.aspx?sourcedoc=%7B1AE62FA5-2E6F-47E6-B6B6-BFF724E1A08C%7D&file=TSC%20Development%20WIP.xlsx&action=default&mobileredirect=true";

    internal static int GetRowForDate(DateOnly date) => HeaderRows + date.DayOfYear;

    internal static string[] GetCellsForDate(DateOnly date)
    {
        var row = GetRowForDate(date);
        return TargetColumns.Select(col => $"{col}{row}").ToArray();
    }

    // One tab per year, "Daily Reports - <year>".
    internal static string GetWorksheetForDate(DateOnly date) => $"Daily Reports - {date.Year}";

    // Column B holds the date as M/D/YYYY (no leading zeros, en-US).
    internal static string GetExpectedDateLabel(DateOnly date) => $"{date.Month}/{date.Day}/{date.Year}";

    // True only when the value read from column B is M/D/YYYY for the same calendar
    // day as expected. Positional numeric compare; any other shape (empty, ISO,
    // text) returns false so the caller fails closed rather than log the wrong day.
    internal static bool DateLabelsMatch(string readValue, string expected)
    {
        var exp = ExtractNumbers(expected);
        var read = ExtractNumbers(readValue);
        if (exp.Count != 3 || read.Count != 3) return false;
        return exp[0] == read[0] && exp[1] == read[1] && exp[2] == read[2];
    }

    private static List<int> ExtractNumbers(string value)
    {
        var nums = new List<int>();
        foreach (Match m in Regex.Matches(value, @"\d+"))
            if (int.TryParse(m.Value, out var n)) nums.Add(n);
        return nums;
    }
}
