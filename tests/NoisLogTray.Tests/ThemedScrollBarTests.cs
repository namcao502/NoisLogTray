using System.Drawing;
using NoisLogTray;

namespace NoisLogTray.Tests;

public class ThemedScrollBarTests
{
    [Fact]
    public void Thumb_IsNone_WhenEverythingFits()
    {
        Assert.Null(ThemedScrollBar.Thumb(track: 200, minimum: 0, maximum: 99, page: 100, position: 0, minLength: 20));
    }

    [Fact]
    public void Thumb_ShowsThePageShare_AndRunsFromTopToBottom()
    {
        var top = ThemedScrollBar.Thumb(track: 200, minimum: 0, maximum: 399, page: 100, position: 0, minLength: 20)!.Value;
        var bottom = ThemedScrollBar.Thumb(track: 200, minimum: 0, maximum: 399, page: 100, position: 300, minLength: 20)!.Value;

        Assert.Equal((0, 50), top);
        Assert.Equal((150, 50), bottom);
    }

    [Fact]
    public void Thumb_NeverShrinksBelowItsMinimum()
    {
        var thumb = ThemedScrollBar.Thumb(track: 200, minimum: 0, maximum: 99_999, page: 100, position: 0, minLength: 20)!.Value;

        Assert.Equal(20, thumb.Length);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(75, 150)]
    [InlineData(150, 300)]
    [InlineData(-30, 0)]
    [InlineData(500, 300)]
    public void PositionAt_MapsTheThumbBack_AndClamps(int thumbStart, int expected)
    {
        Assert.Equal(expected, ThemedScrollBar.PositionAt(track: 200, thumbLength: 50, minimum: 0, maximum: 399, page: 100, thumbStart: thumbStart));
    }

    [Fact]
    public void Attach_HidesTheNativeBar_AndShowsItsOwn_OnlyWhileTheListOverflows()
    {
        using var form = new Form { ClientSize = new Size(300, 300), ShowInTaskbar = false };
        var host = new Panel { Bounds = new Rectangle(10, 10, 200, 200) };
        var list = new FlowLayoutPanel { Bounds = new Rectangle(0, 0, 200, 200), AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        host.Controls.Add(list);
        form.Controls.Add(host);
        var bar = ThemedScrollBar.Attach(list);
        form.Show();
        for (var rowIndex = 0; rowIndex < 20; rowIndex++) list.Controls.Add(new Panel { Size = new Size(150, 40) });
        Application.DoEvents();
        bar.Sync();

        Assert.Same(host, bar.Parent);
        Assert.True(bar.Visible);
        Assert.NotNull(list.Region);
        Assert.Equal(list.Right, bar.Right);

        list.Controls.Clear();
        Application.DoEvents();
        bar.Sync();

        Assert.False(bar.Visible);
        Assert.Null(list.Region);
        form.Close();
    }
}
