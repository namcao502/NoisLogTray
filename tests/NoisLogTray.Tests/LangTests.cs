using NoisLogTray;

namespace NoisLogTray.Tests;

// Lang is static UI state; each test restores the default (English) so test order cannot leak it.
public class LangTests : IDisposable
{
    public void Dispose() => Lang.Vietnamese = false;

    [Fact]
    public void TPicksTheCurrentLanguage()
    {
        Lang.Vietnamese = false;
        Assert.Equal("Save", Lang.T("Save", "Lưu"));
        Lang.Vietnamese = true;
        Assert.Equal("Lưu", Lang.T("Save", "Lưu"));
    }

    [Fact]
    public void DatesFollowTheLanguage()
    {
        var date = new DateTime(2026, 10, 1);

        Lang.Vietnamese = false;
        Assert.Equal("Thursday, October 1, 2026", Lang.LongDate(date));
        Assert.Equal("Oct 1", Lang.ShortDate(date));

        Lang.Vietnamese = true;
        Assert.Equal("Thứ Năm, 01/10/2026", Lang.LongDate(date));
        Assert.Equal("01/10", Lang.ShortDate(date));
    }
}
