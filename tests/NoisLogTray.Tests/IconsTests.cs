using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using NoisLogTray;

namespace NoisLogTray.Tests;

public class IconsTests
{
    [Fact]
    public void Fonts_LoadTheBundledIconSet()
    {
        Assert.Equal("FluentSystemIcons-Regular", Icons.Medium.FontFamily.Name);
        Assert.Equal("FluentSystemIcons-Filled", Icons.MediumFilled.FontFamily.Name);
        Assert.True(Icons.Medium.Size > Icons.Small.Size);
    }

    [Fact]
    public void EveryGlyph_IsInItsFont()
    {
        var glyphs = typeof(Icons).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(fi => fi.IsLiteral && fi.FieldType == typeof(string))
            .Select(fi => (fi.Name, Glyph: (string)fi.GetRawConstantValue()!))
            .ToList();

        Assert.NotEmpty(glyphs);
        foreach (var (name, glyph) in glyphs)
        {
            var font = name.EndsWith("Filled") ? Icons.MediumFilled : Icons.Medium;
            Assert.True(glyph.Length == 1 && glyph[0] >= '\uE000' && glyph[0] <= '\uF8FF', $"{name} is not one private-use character");
            Assert.True(HasGlyph(font, glyph), $"{name} is not in {font.FontFamily.Name}");
        }
    }

    private static bool HasGlyph(Font font, string glyph)
    {
        using var bitmap = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(bitmap);
        var hdc = graphics.GetHdc();
        var hfont = font.ToHfont();
        try
        {
            var previous = SelectObject(hdc, hfont);
            var indices = new ushort[glyph.Length];
            GetGlyphIndicesW(hdc, glyph, glyph.Length, indices, 1 /* GGI_MARK_NONEXISTING_GLYPHS */);
            SelectObject(hdc, previous);
            return indices.All(index => index != 0xFFFF);
        }
        finally
        {
            DeleteObject(hfont);
            graphics.ReleaseHdc(hdc);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetGlyphIndicesW(IntPtr hdc, string text, int count, [Out] ushort[] indices, uint flags);
}
