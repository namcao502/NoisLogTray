using System.Drawing;
using NoisLogTray;

namespace NoisLogTray.Tests;

public class SingleLineLabelTests
{
    [Theory]
    [InlineData(ContentAlignment.MiddleLeft, TextFormatFlags.VerticalCenter | TextFormatFlags.Left)]
    [InlineData(ContentAlignment.MiddleRight, TextFormatFlags.VerticalCenter | TextFormatFlags.Right)]
    [InlineData(ContentAlignment.TopCenter, TextFormatFlags.Top | TextFormatFlags.HorizontalCenter)]
    [InlineData(ContentAlignment.BottomLeft, TextFormatFlags.Bottom | TextFormatFlags.Left)]
    public void TextFlags_StayOnOneLine_AlignedAsTextAlignSays(ContentAlignment textAlign, TextFormatFlags alignment)
    {
        var flags = SingleLineLabel.TextFlags(textAlign, useMnemonic: false);

        Assert.Equal(TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | alignment, flags);
        Assert.False(flags.HasFlag(TextFormatFlags.WordBreak));
    }

    [Fact]
    public void TextFlags_KeepTheMnemonic_WhenTheLabelUsesIt()
    {
        Assert.False(SingleLineLabel.TextFlags(ContentAlignment.MiddleLeft, useMnemonic: true).HasFlag(TextFormatFlags.NoPrefix));
    }
}
