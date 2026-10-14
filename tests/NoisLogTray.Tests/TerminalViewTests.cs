using NoisLogTray;

namespace NoisLogTray.Tests;

public class TerminalViewTests
{
    [Theory]
    [InlineData(1, (int)ShellPage.Dashboard)]
    [InlineData(2, (int)ShellPage.Work)]
    [InlineData(3, (int)ShellPage.Claude)]
    [InlineData(4, (int)ShellPage.Ticket)]
    public void PageForShortcut_MatchesTheShellShortcuts(int index, int expected)
    {
        Assert.Equal((ShellPage)expected, TerminalView.PageForShortcut(index));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(6)]
    public void PageForShortcut_IgnoresOtherDigits(int index)
    {
        Assert.Null(TerminalView.PageForShortcut(index));
    }

    [Theory]
    [InlineData("UI/a.cs", null)]
    [InlineData("UI/a.cs:42", 42)]
    [InlineData("UI/a.cs:42:7", 42)]
    [InlineData(@"UI\a.cs:9", 9)]
    [InlineData("./UI/a.cs", null)]
    public void ResolvePathLink_ResolvesARelativePathAgainstTheSessionFolder(string text, int? line)
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "UI", "a.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");

        var link = TerminalView.ResolvePathLink(text, temp.Path);

        Assert.Equal((file, line), link);
    }

    [Fact]
    public void ResolvePathLink_KeepsAnAbsolutePath_AndItsLine()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "plan.md");
        File.WriteAllText(file, "x");

        Assert.Equal((file, 12), TerminalView.ResolvePathLink(file + ":12", @"C:\elsewhere"));
    }

    [Theory]
    [InlineData("UI/missing.cs")]
    [InlineData("UI")]
    [InlineData("a<b>.cs")]
    [InlineData("")]
    public void ResolvePathLink_ReturnsNull_ForWhatIsNotAnExistingFile(string text)
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "UI"));

        Assert.Null(TerminalView.ResolvePathLink(text, temp.Path));
    }

    [Fact]
    public void LinkPath_TurnsAFileHyperlinkIntoItsPath()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "docs", "open po proof.sql");
        var fileUrl = new Uri(file).AbsoluteUri; // file:///C:/.../open%20po%20proof.sql

        Assert.Equal(file, TerminalView.LinkPath(fileUrl));
        Assert.Equal(file, TerminalView.LinkPath(fileUrl + "#L12"));
    }

    [Theory]
    [InlineData("docs/MDP-9262/open-po-proof.sql")]
    [InlineData(@"docs\MDP-9262\open-po-proof.sql:12")]
    public void LinkPath_KeepsAPathAsWritten(string url)
    {
        Assert.Equal(url, TerminalView.LinkPath(url));
    }

    [Theory]
    [InlineData("https://example.com/a.sql")]
    [InlineData("javascript:alert(1)")]
    [InlineData("mailto:a@b.c")]
    [InlineData("")]
    public void LinkPath_IsNull_ForAnythingButAFileLink(string url)
    {
        Assert.Null(TerminalView.LinkPath(url));
    }

    [Fact]
    public void ResolvePathLink_ReturnsNull_ForARelativePathWithoutASessionFolder()
    {
        Assert.Null(TerminalView.ResolvePathLink("UI/a.cs", null));
    }

    [Fact]
    public void PasteFor_PastesText_EvenWithAnImageBesideIt()
    {
        // Excel puts its cells on the clipboard as text and as a picture; the text is what the user means.
        var paste = TerminalView.PasteFor("a\tb", hasImage: true, Array.Empty<string>());

        Assert.Equal(("a\tb", null), paste);
    }

    [Fact]
    public void PasteFor_SendsClaudesImagePasteKey_ForAnImage()
    {
        var paste = TerminalView.PasteFor(null, hasImage: true, Array.Empty<string>());

        Assert.Equal((null, "\u001bv"), paste);
    }

    [Fact]
    public void PasteFor_PastesQuotedPaths_ForCopiedFiles()
    {
        var paste = TerminalView.PasteFor("", hasImage: false, new[] { @"C:\shots\a b.png", @"C:\shots\c.png" });

        Assert.Equal(("\"C:\\shots\\a b.png\" \"C:\\shots\\c.png\"", null), paste);
    }

    [Fact]
    public void PasteFor_SendsNothing_ForAnEmptyClipboard()
    {
        Assert.Equal((null, null), TerminalView.PasteFor(null, hasImage: false, Array.Empty<string>()));
    }
}
