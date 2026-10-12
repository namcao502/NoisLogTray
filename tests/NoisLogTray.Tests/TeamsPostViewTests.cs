namespace NoisLogTray.Tests;

public class TeamsPostViewTests
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

    private static TeamsPostView NewView(SendRecorder recorder, DateTimeOffset? postedAt = null, Func<string>? loadDraft = null) =>
        new("MDP-1", loadDraft ?? (() => ""), () => postedAt, recorder.SendAsync);

    [Fact]
    public async Task SendAsync_ShowsAnError_AndDoesNotSend_WhenThePostIsBlank()
    {
        var recorder = new SendRecorder(true);
        using var view = NewView(recorder);
        view.PostText = "  \r\n ";

        await view.SendAsync();

        Assert.Empty(recorder.Texts);
        Assert.NotEqual("", view.StatusText);
    }

    [Fact]
    public async Task SendAsync_ShowsAnError_AndDoesNotSend_WhenOnlyTheTitleIsWritten()
    {
        var recorder = new SendRecorder(true);
        using var view = NewView(recorder);
        view.PostText = "MDP-1 x\r\n\r\n";

        await view.SendAsync();

        Assert.Empty(recorder.Texts);
        Assert.NotEqual("", view.StatusText);
    }

    [Fact]
    public async Task SendAsync_SendsTheTrimmedText_AndRaisesPosted_WhenSent()
    {
        var recorder = new SendRecorder(true);
        using var view = NewView(recorder);
        var posted = 0;
        view.Posted += () => posted++;
        view.PostText = "  MDP-1 x\r\n\r\nmô tả \r\n";

        await view.SendAsync();

        Assert.Equal(new[] { "MDP-1 x\r\n\r\nmô tả" }, recorder.Texts);
        Assert.Equal(1, posted);
    }

    [Fact]
    public async Task SendAsync_KeepsTheText_AndShowsAnError_WhenSendingFails()
    {
        var recorder = new SendRecorder(false);
        using var view = NewView(recorder);
        var posted = 0;
        view.Posted += () => posted++;
        view.PostText = "MDP-1 x\r\nmô tả";

        await view.SendAsync();

        Assert.Equal(new[] { "MDP-1 x\r\nmô tả" }, recorder.Texts);
        Assert.Equal(0, posted);
        Assert.Equal("MDP-1 x\r\nmô tả", view.PostText);
        Assert.NotEqual("", view.StatusText);
    }

    [Fact]
    public async Task SendAsync_OnlyWarnsFirst_WhenAlreadyPosted()
    {
        var recorder = new SendRecorder(true);
        using var view = NewView(recorder, postedAt: new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.FromHours(7)));
        view.PostText = "MDP-1 x\r\nmô tả";

        await view.SendAsync();
        Assert.Empty(recorder.Texts);
        Assert.NotEqual("", view.StatusText);

        await view.SendAsync();
        Assert.Single(recorder.Texts);
    }

    [Fact]
    public void Activate_RereadsAnUntouchedDraft_AndKeepsAnEditedOne()
    {
        var draft = "MDP-1 x\r\nv1";
        using var view = NewView(new SendRecorder(true), loadDraft: () => draft);

        draft = "MDP-1 x\r\nv2";
        view.Activate();
        Assert.Equal("MDP-1 x\r\nv2", view.PostText);

        view.PostText = "MDP-1 x\r\nmine";
        draft = "MDP-1 x\r\nv3";
        view.Activate();
        Assert.Equal("MDP-1 x\r\nmine", view.PostText);
    }
}
