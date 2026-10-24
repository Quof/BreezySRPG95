using BreezySRPG95.Ui;

namespace Srpg95.Data.Tests;

/// <summary>
/// The FreeType text renderer (Linux, macOS; BREEZY_TEXT=freetype on Windows) against what Windows GDI gives with the
/// same font files: line metrics and string widths measured with GDI, and the variable fonts' weights. The fonts are in
/// the executable only where the build found them (client/BUILDING.md): without them these tests have nothing to check.
/// </summary>
public class FreeTypeTextTests
{
    static bool Have(params string[] fonts) => fonts.All(FreeTypeFonts.Has);

    [Fact]
    public void A_line_is_as_high_as_GDI_makes_it()
    {
        if (!Have(FreeTypeFonts.IpaGothic, FreeTypeFonts.NotoSans, FreeTypeFonts.DotGothic16, FreeTypeFonts.PixelMplus12, FreeTypeFonts.KhDot12, FreeTypeFonts.KhDot16)) return;
        // (tmAscent, tmHeight) from GDI
        Assert.Equal((16, 20), FreeTypeFonts.Line(FreeTypeFonts.IpaGothic, 18));
        Assert.Equal((14, 17), FreeTypeFonts.Line(FreeTypeFonts.IpaGothic, 16));
        Assert.Equal((12, 15), FreeTypeFonts.Line(FreeTypeFonts.IpaGothic, 14));
        Assert.Equal((11, 13), FreeTypeFonts.Line(FreeTypeFonts.IpaGothic, 12));
        Assert.Equal((37, 46), FreeTypeFonts.Line(FreeTypeFonts.NotoSans, 32));
        Assert.Equal((19, 24), FreeTypeFonts.Line(FreeTypeFonts.NotoSans, 16));
        Assert.Equal((16, 18), FreeTypeFonts.Line(FreeTypeFonts.DotGothic16, 16));
        Assert.Equal((11, 13), FreeTypeFonts.Line(FreeTypeFonts.PixelMplus12, 12));
        Assert.Equal((10, 12), FreeTypeFonts.Line(FreeTypeFonts.KhDot12, 12));
        Assert.Equal((14, 16), FreeTypeFonts.Line(FreeTypeFonts.KhDot16, 16));
    }

    [Fact]
    public void A_string_is_as_wide_as_GDI_makes_it()
    {
        if (!Have(FreeTypeFonts.IpaGothic, FreeTypeFonts.NotoSans, FreeTypeFonts.NotoSerif, FreeTypeFonts.DotGothic16, FreeTypeFonts.PixelMplus12, FreeTypeFonts.KhDot16)) return;
        const string a = "東から来た、征伐騎士どもか。", b = "魔道士と女教皇の子供の所か", c = "ＨＰ　22／22  Lv 10 (99)";
        // the original skin's stand-in, unsmoothed; GDI's simulated bold a pixel wider a character
        Assert.Equal(252, FreeTypeFonts.Width(FreeTypeFonts.IpaGothic, 0, 18, mono: true, bold: false, a));
        Assert.Equal(266, FreeTypeFonts.Width(FreeTypeFonts.IpaGothic, 0, 18, mono: true, bold: true, a));
        Assert.Equal(238, FreeTypeFonts.Width(FreeTypeFonts.IpaGothic, 0, 16, mono: true, bold: true, a));
        Assert.Equal(168, FreeTypeFonts.Width(FreeTypeFonts.IpaGothic, 0, 12, mono: true, bold: false, a));
        // the pixel fonts
        Assert.Equal(208, FreeTypeFonts.Width(FreeTypeFonts.DotGothic16, 0, 16, mono: true, bold: false, b));
        Assert.Equal(192, FreeTypeFonts.Width(FreeTypeFonts.DotGothic16, 0, 16, mono: true, bold: false, c));
        Assert.Equal(144, FreeTypeFonts.Width(FreeTypeFonts.PixelMplus12, 0, 12, mono: true, bold: false, c));
        Assert.Equal(208, FreeTypeFonts.Width(FreeTypeFonts.KhDot16, 0, 16, mono: true, bold: false, b));
        // the smooth faces, at their weights
        Assert.Equal(448, FreeTypeFonts.Width(FreeTypeFonts.NotoSans, 400, 32, mono: false, bold: false, a));
        Assert.Equal(416, FreeTypeFonts.Width(FreeTypeFonts.NotoSans, 500, 32, mono: false, bold: false, b));
        Assert.Equal(448, FreeTypeFonts.Width(FreeTypeFonts.NotoSerif, 600, 32, mono: false, bold: false, a));
    }

    [Fact]
    public void A_variable_font_is_never_drawn_at_its_default_thin_weight()
    {
        if (!Have(FreeTypeFonts.NotoSans, FreeTypeFonts.NotoSerif, FreeTypeFonts.IpaGothic)) return;
        Assert.Equal(400, FreeTypeFonts.DrawnWeight(FreeTypeFonts.NotoSans, 0));   // its default instance is Thin (100)
        Assert.Equal(400, FreeTypeFonts.DrawnWeight(FreeTypeFonts.NotoSans, 400));
        Assert.Equal(500, FreeTypeFonts.DrawnWeight(FreeTypeFonts.NotoSans, 500));
        Assert.Equal(400, FreeTypeFonts.DrawnWeight(FreeTypeFonts.NotoSerif, 0));   // ExtraLight (200) by default
        Assert.Equal(500, FreeTypeFonts.DrawnWeight(FreeTypeFonts.NotoSerif, 500));
        Assert.Equal(600, FreeTypeFonts.DrawnWeight(FreeTypeFonts.NotoSerif, 600));
        Assert.Equal(0, FreeTypeFonts.DrawnWeight(FreeTypeFonts.IpaGothic, 700));   // a static font: drawn as it is
    }
}
