using NoisLogTray;

namespace NoisLogTray.Tests;

public class NoticeToastTests
{
    [Fact]
    public void OpenTarget_RunsTheNoticeTarget_AndHides()
    {
        using var toast = new NoticeToast { Width = 300 };
        var openCount = 0;
        toast.ShowNotice("MDP-1: Claude is done - your turn.", NoticeKind.Info, open: () => openCount++);

        toast.OpenTarget();

        Assert.Equal(1, openCount);
        Assert.False(toast.Visible);
    }

    [Fact]
    public void OpenTarget_OnlyHides_WhenTheNoticeHasNoTarget()
    {
        using var toast = new NoticeToast { Width = 300 };
        var openCount = 0;
        toast.ShowNotice("first", NoticeKind.Info, open: () => openCount++);
        toast.ShowNotice("Settings saved.", NoticeKind.Info);

        toast.OpenTarget();

        Assert.Equal(0, openCount);
        Assert.False(toast.Visible);
    }
}
