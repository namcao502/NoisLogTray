using System.Drawing;
using NoisLogTray;

namespace NoisLogTray.Tests;

public class MacButtonTests
{
    private static readonly Size Button = new(120, 30);

    [Fact]
    public void ContentBounds_CentersAGlyphAlone()
    {
        var (_, glyph) = MacButton.ContentBounds(Button, textWidth: 0, glyphWidth: 16, gap: 6, glyphAfterText: false);

        Assert.Equal(Button.Width / 2, glyph.Left + glyph.Width / 2);
        Assert.Equal(Button.Height, glyph.Height);
    }

    [Fact]
    public void ContentBounds_PutsATrailingGlyphAfterTheText_AndCentersThePair()
    {
        var (text, glyph) = MacButton.ContentBounds(Button, textWidth: 60, glyphWidth: 16, gap: 6, glyphAfterText: true);

        Assert.Equal(text.Right + 6, glyph.Left);
        Assert.Equal(Button.Width - glyph.Right, text.Left);
    }

    [Fact]
    public void FocusRing_ShowsOnlyWhileWindowsShowsKeyboardFocusCues()
    {
        using var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000) };
        var button = MacButton.Primary("Open");
        form.Controls.Add(button);
        form.Show();
        button.Focus();

        // What a mouse click leaves: the button keeps focus, Windows hides the cues.
        SendMessage(form.Handle, WmChangeUiState, (IntPtr)(UisSet | (UisfHideFocus << 16)), IntPtr.Zero);
        Assert.True(button.Focused);
        Assert.False(button.FocusRingVisible);

        // What Tab does.
        SendMessage(form.Handle, WmChangeUiState, (IntPtr)(UisClear | (UisfHideFocus << 16)), IntPtr.Zero);
        Assert.True(button.FocusRingVisible);
        form.Close();
    }

    private const int WmChangeUiState = 0x0127;
    private const int UisSet = 1;
    private const int UisClear = 2;
    private const int UisfHideFocus = 0x1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [Fact]
    public void ContentBounds_PutsALeadingGlyphBeforeTheText()
    {
        var (text, glyph) = MacButton.ContentBounds(Button, textWidth: 60, glyphWidth: 16, gap: 6, glyphAfterText: false);

        Assert.Equal(glyph.Right + 6, text.Left);
    }

    [Fact]
    public void ContentBounds_CutsATextTooWideForTheButton_SoTheGlyphStaysInside()
    {
        var (text, glyph) = MacButton.ContentBounds(Button, textWidth: 400, glyphWidth: 16, gap: 6, glyphAfterText: true);

        Assert.True(text.Left >= 0);
        Assert.True(glyph.Right <= Button.Width);
        Assert.Equal(text.Right + 6, glyph.Left);
    }
}
