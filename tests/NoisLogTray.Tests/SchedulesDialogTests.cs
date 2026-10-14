using NoisLogTray;

namespace NoisLogTray.Tests;

public class SchedulesDialogTests
{
    [Fact]
    public void Dialog_HostsThePage_AndClosesOnEsc()
    {
        var page = new SchedulesPage(() => "", () => Array.Empty<AppJobStatus>());
        using var dialog = new SchedulesDialog(page);

        Assert.Same(page, dialog.Page);
        Assert.True(dialog.Contains(page));
        Assert.Same(dialog.CloseButton, dialog.CancelButton);
        Assert.Equal(DialogResult.Cancel, dialog.CloseButton.DialogResult);
        Assert.True(dialog.ClientSize.Width >= page.MinimumSize.Width);
    }
}
