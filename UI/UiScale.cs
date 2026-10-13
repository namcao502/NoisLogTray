using System.Drawing;

namespace NoisLogTray;

// App-wide zoom for the shell window and its dialogs, fixed at 125% (there is no zoom setting). Layout code is
// written in 100% ("logical") pixels and font points, and every size goes through S()/Font() so one factor scales
// the whole UI.
internal static class UiScale
{
    internal const float Factor = 1.25f;

    internal static int S(int logical) => (int)Math.Round(logical * Factor, MidpointRounding.AwayFromZero);
    internal static float S(float logical) => logical * Factor;
    internal static Size S(Size logical) => new(S(logical.Width), S(logical.Height));
    internal static Point S(Point logical) => new(S(logical.X), S(logical.Y));
    internal static Rectangle S(Rectangle logical) => new(S(logical.X), S(logical.Y), S(logical.Width), S(logical.Height));
    internal static Padding S(Padding logical) => new(S(logical.Left), S(logical.Top), S(logical.Right), S(logical.Bottom));

    // Physical pixels back to logical, for the pure ShellLayout rules that are written (and tested) at 100%.
    internal static int Logical(int physical) => (int)Math.Round(physical / Factor, MidpointRounding.AwayFromZero);

    internal static Font Font(string family, float points, FontStyle style = FontStyle.Regular) =>
        new(family, points * Factor, style);
}
