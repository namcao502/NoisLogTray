using System.Drawing;
using NoisLogTray;

namespace NoisLogTray.Tests;

// Covers the borderless window's resize border: edges, the radius-long corner zones,
// and the client area that must stay a normal click.
public class ResizeHitTestTests
{
    private static readonly Size WindowSize = new(600, 800);
    private const int Grip = 6;
    private const int CornerZone = 12;

    [Theory]
    [InlineData(2, 400, ResizeHitTest.Left)]
    [InlineData(597, 400, ResizeHitTest.Right)]
    [InlineData(300, 2, ResizeHitTest.Top)]
    [InlineData(300, 797, ResizeHitTest.Bottom)]
    public void EdgeStripResizesThatEdge(int x, int y, int expected)
    {
        Assert.Equal(expected, ResizeHitTest.At(new Point(x, y), WindowSize, Grip, CornerZone));
    }

    [Theory]
    [InlineData(10, 2, ResizeHitTest.TopLeft)]   // top strip, within the corner zone
    [InlineData(2, 10, ResizeHitTest.TopLeft)]   // left strip, within the corner zone
    [InlineData(590, 797, ResizeHitTest.BottomRight)]
    [InlineData(2, 790, ResizeHitTest.BottomLeft)]
    [InlineData(597, 10, ResizeHitTest.TopRight)]
    public void CornerZoneResizesDiagonally(int x, int y, int expected)
    {
        Assert.Equal(expected, ResizeHitTest.At(new Point(x, y), WindowSize, Grip, CornerZone));
    }

    [Fact]
    public void ClientAreaIsNotAResizeBorder()
    {
        Assert.Equal(ResizeHitTest.None, ResizeHitTest.At(new Point(300, 400), WindowSize, Grip, CornerZone));
        Assert.Equal(ResizeHitTest.None, ResizeHitTest.At(new Point(Grip, 400), WindowSize, Grip, CornerZone));
    }
}
