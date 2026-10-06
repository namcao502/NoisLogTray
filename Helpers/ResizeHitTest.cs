using System.Drawing;

namespace NoisLogTray;

// Maps a client point of the borderless main window to a WM_NCHITTEST resize code.
// Corners get a zone as long as the window's corner radius along each edge, since the
// rounded region clips the exact corner pixels away.
internal static class ResizeHitTest
{
    internal const int None = 0;
    internal const int Left = 10;
    internal const int Right = 11;
    internal const int Top = 12;
    internal const int TopLeft = 13;
    internal const int TopRight = 14;
    internal const int Bottom = 15;
    internal const int BottomLeft = 16;
    internal const int BottomRight = 17;

    internal static int At(Point point, Size size, int grip, int cornerZone)
    {
        var onLeft = point.X < grip;
        var onRight = point.X >= size.Width - grip;
        var onTop = point.Y < grip;
        var onBottom = point.Y >= size.Height - grip;
        var nearLeft = point.X < cornerZone;
        var nearRight = point.X >= size.Width - cornerZone;
        var nearTop = point.Y < cornerZone;
        var nearBottom = point.Y >= size.Height - cornerZone;

        if ((onTop && nearLeft) || (onLeft && nearTop)) return TopLeft;
        if ((onTop && nearRight) || (onRight && nearTop)) return TopRight;
        if ((onBottom && nearLeft) || (onLeft && nearBottom)) return BottomLeft;
        if ((onBottom && nearRight) || (onRight && nearBottom)) return BottomRight;
        if (onLeft) return Left;
        if (onRight) return Right;
        if (onTop) return Top;
        if (onBottom) return Bottom;
        return None;
    }
}
