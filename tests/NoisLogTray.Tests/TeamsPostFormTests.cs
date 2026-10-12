namespace NoisLogTray.Tests;

public class TeamsPostFormTests
{
    private sealed class SendRecorder
    {
        private readonly bool _result;

        internal SendRecorder(bool result)
        {
            _result = result;
        }

        internal List<string> Texts { get; } = new();

        internal Task<bool> SendAsync(string text, CancellationToken cancellationToken)
        {
            Texts.Add(text);
            return Task.FromResult(_result);
        }
    }

    [Fact]
    public async Task SendAsync_ShowsAnError_AndDoesNotSend_WhenThePostIsBlank()
    {
        var recorder = new SendRecorder(true);
        using var form = new TeamsPostForm("MDP-1", "", null, recorder.SendAsync);
        form.PostText = "  \r\n ";

        await form.SendAsync();

        Assert.Empty(recorder.Texts);
        Assert.NotEqual("", form.ErrorText);
        Assert.Equal(DialogResult.None, form.DialogResult);
    }

    [Fact]
    public async Task SendAsync_ShowsAnError_AndDoesNotSend_WhenOnlyTheTitleIsWritten()
    {
        var recorder = new SendRecorder(true);
        using var form = new TeamsPostForm("MDP-1", "", null, recorder.SendAsync);
        form.PostText = "MDP-1 x\r\n\r\n";

        await form.SendAsync();

        Assert.Empty(recorder.Texts);
        Assert.NotEqual("", form.ErrorText);
        Assert.Equal(DialogResult.None, form.DialogResult);
    }

    [Fact]
    public async Task SendAsync_SendsTheTrimmedText_AndClosesOk_WhenSent()
    {
        var recorder = new SendRecorder(true);
        using var form = new TeamsPostForm("MDP-1", "", null, recorder.SendAsync);
        form.PostText = "  MDP-1 x\r\n\r\nmô tả \r\n";

        await form.SendAsync();

        Assert.Equal(new[] { "MDP-1 x\r\n\r\nmô tả" }, recorder.Texts);
        Assert.Equal(DialogResult.OK, form.DialogResult);
    }

    [Fact]
    public async Task SendAsync_KeepsTheText_AndShowsAnError_WhenSendingFails()
    {
        var recorder = new SendRecorder(false);
        using var form = new TeamsPostForm("MDP-1", "", null, recorder.SendAsync);
        form.PostText = "MDP-1 x\r\nmô tả";

        await form.SendAsync();

        Assert.Equal(new[] { "MDP-1 x\r\nmô tả" }, recorder.Texts);
        Assert.Equal(DialogResult.None, form.DialogResult);
        Assert.Equal("MDP-1 x\r\nmô tả", form.PostText);
        Assert.NotEqual("", form.ErrorText);
    }
}
