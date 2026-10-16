namespace NoisLogTray.Tests;

public class ChangesViewTests
{
    // Language-neutral: other tests flip Lang in parallel.
    [Fact]
    public void StatusText_NamesEveryThreadStatus_Distinctly()
    {
        var statusTexts = Enum.GetValues<PrThreadStatus>().Select(ChangesView.StatusText).ToList();

        Assert.All(statusTexts, statusText => Assert.False(string.IsNullOrWhiteSpace(statusText)));
        Assert.Equal(statusTexts.Count, statusTexts.Distinct().Count());
    }
}
