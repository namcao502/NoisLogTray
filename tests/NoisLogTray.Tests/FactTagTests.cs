using System.Drawing;
using NoisLogTray;

namespace NoisLogTray.Tests;

public class FactTagTests
{
    private static readonly Font TagFont = new("Segoe UI", 8.5F);

    [Fact]
    public void Size_FitsTheText_InsideItsPill_AndKeepsTheMinimumWidth()
    {
        using var shortTag = new FactTag("P 2", Color.Red, TagFont);
        using var longTag = new FactTag("PR & Code Review", Color.Red, TagFont);
        using var paddedTag = new FactTag("P 2", Color.Red, TagFont, minTextWidth: 200);

        Assert.True(longTag.Width > shortTag.Width, $"{longTag.Size} {shortTag.Size}");
        Assert.True(shortTag.Width > TextRenderer.MeasureText("P 2", TagFont).Width, $"{shortTag.Size}");
        Assert.True(shortTag.Height > TagFont.Height, $"{shortTag.Size} {TagFont.Height}");
        Assert.True(paddedTag.Width > 200, $"{paddedTag.Size}");
    }

    [Fact]
    public void Runs_TurnThePrStatusMarksIntoIcons_AndLeaveOtherTextAlone()
    {
        Assert.Equal(new (string, bool)[] { ("BE main", false), (Icons.CheckmarkCircle, true), ("· dev", false), (Icons.Record, true) },
            FactTag.Runs("BE main ✓ · dev ●"));
        Assert.Equal(new (string, bool)[] { ("dev", false), (Icons.DismissCircle, true), ("#12 · 2 comments", false) },
            FactTag.Runs("dev ✕ #12 · 2 comments"));
        Assert.Equal(new (string, bool)[] { ("Jira: Done -- move to Done?", false) }, FactTag.Runs("Jira: Done -- move to Done?"));
    }

    [Fact]
    public void Size_CountsAMarkAsItsIcon()
    {
        using var marked = new FactTag("BE main ✓", Color.Green, TagFont);
        using var plain = new FactTag("BE main", Color.Green, TagFont);

        Assert.True(marked.Width > plain.Width);
    }

    [Fact]
    public void Press_RunsTheAction_OnlyOnALinkTag()
    {
        var presses = 0;
        using var link = new FactTag("BE main ● #12", Theme.Accent, TagFont, onClick: () => presses++);
        using var plain = new FactTag("Due Oct 15", Theme.Accent, TagFont);

        link.Press();
        plain.Press();

        Assert.Equal(1, presses);
        Assert.True(link.TabStop);
        Assert.False(plain.TabStop);
        Assert.Equal(AccessibleRole.Link, link.AccessibleRole);
    }

    [Fact]
    public void TextColor_KeepsTheTonesHue_ButNeverTheToneItself()
    {
        var tone = Color.FromArgb(255, 140, 50);

        var text = FactTag.TextColor(tone);

        Assert.NotEqual(tone.ToArgb(), text.ToArgb());
        Assert.True(text.R > text.G && text.G > text.B);
    }
}
