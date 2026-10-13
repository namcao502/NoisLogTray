using NoisLogTray;

namespace NoisLogTray.Tests;

public class ConfirmDialogTests
{
    [Fact]
    public void Enter_PicksCancel_UnlessConfirmIsTheDefault_AndEscAlwaysCancels()
    {
        using var cautious = new ConfirmDialog("Quit", "Claude is still running.", "Quit", confirmByDefault: false, destructive: true);
        using var eager = new ConfirmDialog("Couldn't verify", "Save anyway?", "Save anyway", confirmByDefault: true, destructive: false);

        Assert.Same(cautious.CancelChoice, cautious.AcceptButton);
        Assert.Same(eager.ConfirmChoice, eager.AcceptButton);
        Assert.Same(cautious.CancelChoice, cautious.CancelButton);
        Assert.Same(eager.CancelChoice, eager.CancelButton);
    }

    [Fact]
    public void Buttons_NameTheAction_AndCloseWithTheirAnswer()
    {
        using var dialog = new ConfirmDialog("Close tab", "Claude is still running in MDP-1.", "Close tab", confirmByDefault: false, destructive: true);

        Assert.Equal("Close tab", dialog.ConfirmChoice.Text);
        Assert.Equal(DialogResult.OK, dialog.ConfirmChoice.DialogResult);
        Assert.Equal(DialogResult.Cancel, dialog.CancelChoice.DialogResult);
    }

    [Fact]
    public void Height_GrowsWithALongMessage()
    {
        using var shortMessage = new ConfirmDialog("Title", "One line.", "OK", confirmByDefault: true, destructive: false);
        using var longMessage = new ConfirmDialog("Title", string.Join(" ", Enumerable.Repeat("word", 150)), "OK", confirmByDefault: true, destructive: false);

        Assert.True(longMessage.ClientSize.Height > shortMessage.ClientSize.Height);
        Assert.Equal(shortMessage.ClientSize.Width, longMessage.ClientSize.Width);
    }
}
