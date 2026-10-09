using NoisLogTray;

namespace NoisLogTray.Tests;

// Lang flips in parallel tests, so these assert null vs non-null rather than a message's language.
public class SettingsValidationTests
{
    private static readonly IReadOnlyList<string> Columns = new[] { "M", "J" };

    [Fact]
    public void FormatError_RejectsNonHttpUrl()
    {
        Assert.NotNull(SettingsValidation.FormatError("ftp://x.atlassian.net", "a@b.c", "t", "k", Columns, "6:00 PM"));
    }

    [Fact]
    public void FormatError_RejectsEmailWithoutAt()
    {
        Assert.NotNull(SettingsValidation.FormatError("https://x.atlassian.net", "abc", "t", "k", Columns, "6:00 PM"));
    }

    [Fact]
    public void FormatError_RequiresTokenAndKey()
    {
        Assert.NotNull(SettingsValidation.FormatError("https://x.atlassian.net", "a@b.c", "", "k", Columns, "6:00 PM"));
        Assert.NotNull(SettingsValidation.FormatError("https://x.atlassian.net", "a@b.c", "t", "", Columns, "6:00 PM"));
    }

    [Fact]
    public void FormatError_RequiresColumns()
    {
        Assert.NotNull(SettingsValidation.FormatError("https://x.atlassian.net", "a@b.c", "t", "k", Array.Empty<string>(), "6:00 PM"));
    }

    [Fact]
    public void FormatError_RejectsBadLogTime()
    {
        Assert.NotNull(SettingsValidation.FormatError("https://x.atlassian.net", "a@b.c", "t", "k", Columns, "six"));
    }

    [Fact]
    public void FormatError_AcceptsValid()
    {
        Assert.Null(SettingsValidation.FormatError("https://x.atlassian.net", "a@b.c", "t", "k", Columns, "6:00 PM"));
        Assert.Null(SettingsValidation.FormatError("HTTP://x.atlassian.net", "a@b.c", "t", "k", Columns, "18:00"));
    }
}
