using NoisLogTray;

namespace NoisLogTray.Tests;

// Covers the pure date/cell helpers ported from lib/tsc-cells.ts.
public class TscCellsTests
{
    [Fact]
    public void ComputesRowAsTwoPlusDayOfYear()
    {
        // 2026-01-01 -> dayOfYear 1 -> row 3
        Assert.Equal(3, TscCells.GetRowForDate(new DateOnly(2026, 1, 1)));
        // 2026-07-01 -> dayOfYear 182 -> row 184
        Assert.Equal(184, TscCells.GetRowForDate(new DateOnly(2026, 7, 1)));
    }

    [Fact]
    public void ProducesMAndJCells()
    {
        var cells = TscCells.GetCellsForDate(new DateOnly(2026, 7, 1));
        Assert.Equal(new[] { "M184", "J184" }, cells);
    }

    [Fact]
    public void WorksheetIsPerYear()
    {
        Assert.Equal("Daily Reports - 2026", TscCells.GetWorksheetForDate(new DateOnly(2026, 7, 1)));
        Assert.Equal("Daily Reports - 2025", TscCells.GetWorksheetForDate(new DateOnly(2025, 12, 31)));
    }

    [Fact]
    public void ExpectedDateLabelHasNoLeadingZeros()
    {
        Assert.Equal("7/1/2026", TscCells.GetExpectedDateLabel(new DateOnly(2026, 7, 1)));
        Assert.Equal("12/9/2026", TscCells.GetExpectedDateLabel(new DateOnly(2026, 12, 9)));
    }

    [Theory]
    [InlineData("7/1/2026", "7/1/2026", true)]
    [InlineData("07/01/2026", "7/1/2026", true)]  // leading zeros still match positionally
    [InlineData("7/2/2026", "7/1/2026", false)]   // wrong day
    [InlineData("", "7/1/2026", false)]           // empty -> fail closed
    [InlineData("2026-07-01", "7/1/2026", false)] // ISO order differs -> fail closed
    public void DateLabelsMatchIsPositional(string read, string expected, bool result)
    {
        Assert.Equal(result, TscCells.DateLabelsMatch(read, expected));
    }
}
