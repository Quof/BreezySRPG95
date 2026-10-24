using System.Runtime.InteropServices;
using System.Text;
using BreezySRPG95.Config;
using FreeTypeSharp;
using Microsoft.Xna.Framework.Graphics;
using static FreeTypeSharp.FT;

namespace BreezySRPG95.Ui;

/// <summary>
/// The interface's text through FreeType: on Linux and macOS (on Windows only with BREEZY_TEXT=freetype, GdiText being the
/// default there). The same styles, sizes and layout as <see cref="GdiText"/>, from the fonts inside the executable alone:
/// IPAGothic in the place of the original's MS Gothic (Windows' own, not to be carried), Noto Sans JP and Noto Serif JP at
/// the weights GDI picks by name (their variable fonts' named instances), the pixel fonts dot for dot; BIZ UDPGothic, Yu
/// Gothic UI and MS Gothic, Windows' fonts, aren't there and fall back to Noto Sans JP. GDI's own numbers are followed:
/// a face's line is round(winAscent × px / em) + round(winDescent × px / em) (as GDI gives for every font carried, at
/// 2–120 px), a string's width its glyphs' rounded advances, GDI's simulated bold (the original's 700 weight) one pixel
/// wider a character, drawn twice a pixel apart; the restyled skins centre on Yu Gothic UI as GDI measures it
/// (<see cref="RefMetrics"/>). Against GDI with the same font files the pixel fonts come out identical, IPAGothic within a
/// few pixels a line and Noto with the same widths for Japanese (a pixel or two over a line of Latin letters).
/// </summary>
public sealed class FreeTypeText : ITextFont
{
    readonly GraphicsDevice _device;
    /// <summary>Bold text (the original's weight 700): the restyled faces' heavier cut, the original skin's simulated bold.</summary>
    readonly bool _strong;
    /// <summary>Rendered strings, all in one mode (the look, the Font option, the scale): 64 MB of them at most.</summary>
    readonly TextureCache<string> _cache = new(64L << 20);
    (bool Restyled, UiFont Font, int Scale) _cacheMode = (false, default, 1);
    readonly Dictionary<(bool Restyled, UiFont Font, int Scale), Style> _styles = new();
    public string FaceName { get; }
    public int Height { get; }

    /// <summary>
    /// How strings are drawn now: the font file (a resource) at a weight (a named instance; 0 for a static font) and pixel
    /// size, unsmoothed or not, with GDI's simulated bold or not, at a render scale, placed like GDI's (lift, reference
    /// line) in a cell of GDI's ascent and height.
    /// </summary>
    readonly record struct Style(string File, int Weight, int Px, bool Mono, bool Bold, int Scale, float Lift, float RefHeight, int Ascent, int CellHeight);

    public FreeTypeText(GraphicsDevice device, int height = 18, int weight = 700, string envOverride = "BREEZY_FONT")
    {
        _device = device;
        // calibration hook, as GdiText's: e.g. BREEZY_FONT="height,weight"
        if (Environment.GetEnvironmentVariable(envOverride) is { } env && env.Split(',') is [var hs, var ws])
            (height, weight) = (int.Parse(hs), int.Parse(ws));
        Height = height;
        _strong = weight >= 700;
        FreeTypeFonts.Init();   // fails here, not at the first string, if FreeType can't be loaded
        FaceName = FreeTypeFonts.Has(FreeTypeFonts.IpaGothic) ? "IPAGothic" : "Noto Sans JP";
    }

    // ---------------- faces, as GdiText chooses them ----------------

    /// <summary>A restyled font's file, weight and size against the original's pixel height (GdiText.Spec's carried fonts).</summary>
    (string File, int Weight, float Size) Spec(UiFont f) => f switch
    {
        UiFont.NotoSerif when FreeTypeFonts.Has(FreeTypeFonts.NotoSerif) => (FreeTypeFonts.NotoSerif, _strong ? 600 : 500, 0.9f),
        _ when FreeTypeFonts.Has(FreeTypeFonts.NotoSans) => (FreeTypeFonts.NotoSans, _strong ? 500 : 400, 0.9f),
        _ => (FreeTypeFonts.IpaGothic, 0, 1f),   // nothing else carried: IPAGothic, smoothed
    };

    /// <summary>A pixel font's file and the size its dots were made for, for a text of <paramref name="height"/> px (GdiText.Pixel).</summary>
    static (string File, int Px)? Pixel(UiFont f, int height) => (f, height >= 16) switch
    {
        (UiFont.MsGothic, _) => (FreeTypeFonts.IpaGothic, height),   // MS Gothic's stand-in, dot for dot at the text's height
        (UiFont.PixelDot, true) => (FreeTypeFonts.DotGothic16, 16),
        (UiFont.PixelDot, false) => (FreeTypeFonts.PixelMplus12, 12),
        (UiFont.KhDot, true) => (FreeTypeFonts.KhDot16, 16),
        (UiFont.KhDot, false) => (FreeTypeFonts.KhDot12, 12),
        _ => null,
    };

    /// <summary>Whether a Font option's fonts are carried, for the options to say so (Windows' own never are).</summary>
    public static bool Available(UiFont f) => f switch
    {
        UiFont.NotoSans => FreeTypeFonts.Has(FreeTypeFonts.NotoSans),
        UiFont.NotoSerif => FreeTypeFonts.Has(FreeTypeFonts.NotoSerif),
        UiFont.MsGothic => FreeTypeFonts.Has(FreeTypeFonts.IpaGothic),
        UiFont.PixelDot or UiFont.KhDot => Pixel(f, 12) is { } small && Pixel(f, 16) is { } large
            && FreeTypeFonts.Has(small.File) && FreeTypeFonts.Has(large.File),
        _ => false,
    };

    Style Current()
    {
        bool restyled = !Ui.OriginalText;
        var font = restyled ? Ui.Font : default;
        int k = restyled && Pixel(font, Height) is null ? Math.Max(1, Ui.RenderScale) : 1;
        if (!_styles.TryGetValue((restyled, font, k), out var s)) _styles[(restyled, font, k)] = s = Make(restyled, font, k);
        return s;
    }

    /// <summary>The original skin's face, in MS Gothic's place: IPAGothic, else (a build without it) Noto Sans JP.</summary>
    (string File, int Weight) OriginalFace() => FreeTypeFonts.Has(FreeTypeFonts.IpaGothic)
        ? (FreeTypeFonts.IpaGothic, 0) : (FreeTypeFonts.NotoSans, _strong ? 500 : 400);

    Style Make(bool restyled, UiFont font, int k)
    {
        if (!restyled)
        {
            // the original skin: MS Gothic at the height asked for, unsmoothed, bold simulated
            var (file, weight) = OriginalFace();
            var (asc, h) = FreeTypeFonts.Line(file, Height);
            return new Style(file, weight, Height, Mono: true, Bold: _strong && weight == 0, 1, 0, 0, asc, h);
        }
        if (Pixel(font, Height) is { } pixel)
        {
            // GdiText.MakePixel: the dots' own size, regular weight, centred on the reference face on a whole pixel
            var (file, weight) = FreeTypeFonts.Has(pixel.File) ? (pixel.File, 0) : OriginalFace();
            var (asc, h) = FreeTypeFonts.Line(file, pixel.Px);
            var (refAsc, refH) = RefMetrics(Height);
            float lift = MathF.Round(asc - 0.38f * pixel.Px - (refAsc - 0.38f * Height));
            return new Style(file, weight, pixel.Px, Mono: true, Bold: font == UiFont.MsGothic && _strong && weight == 0, 1, lift, refH, asc, h);   // MS Gothic as bold as the original's
        }
        {
            // GdiText.MakeSmooth: the face's size at the render scale, the middle of its em box where the reference's is
            var (file, weight, size) = Spec(font);
            int px = Math.Max(1, (int)MathF.Round(Height * size * k)), refPx = Height * k;
            var (asc, h) = FreeTypeFonts.Line(file, px);
            var (refAsc, refH) = RefMetrics(refPx);
            float lift = (asc - 0.38f * px - (refAsc - 0.38f * refPx)) / k;
            return new Style(file, weight, px, Mono: false, Bold: false, k, lift, refH / (float)k, asc, h);
        }
    }

    /// <summary>
    /// Yu Gothic UI's ascent and line height at <paramref name="px"/> as GDI gives them (tmAscent, tmHeight), measured on
    /// Windows 11 for 1–120 px, the same for Regular and Semibold (GdiText's reference for normal and bold text alike): the
    /// reference face the restyled skins are laid out on, a
    /// Windows font with its own per-size table (VDMX), so not a formula. Larger sizes (the render scale reaches 6 × 18
    /// px) are scaled from its metrics (2210 / 514 of 2048).
    /// </summary>
    static (int Ascent, int Height) RefMetrics(int px)
    {
        if (px >= 1 && px <= RefTable.Length) return RefTable[px - 1];
        return (R(2210.0 * px / 2048), R(2210.0 * px / 2048) + R(514.0 * px / 2048));
    }

    static readonly (int, int)[] RefTable =
        ("2,3 2,3 3,4 4,5 5,6 6,8 8,10 9,11 10,12 10,12 11,13 12,15 14,17 15,19 16,20 17,21 18,23 20,25 20,25 23,28 24,30 24,30 " +
         "25,31 26,32 28,35 29,36 30,37 31,38 32,40 33,41 34,42 36,45 36,45 37,46 38,47 39,48 40,50 41,51 42,52 43,54 44,55 46,57 " +
         "47,59 48,60 49,61 50,62 50,62 52,65 53,66 54,67 55,68 56,70 57,71 58,72 60,74 60,74 61,76 62,77 63,78 65,81 66,82 68,84 " +
         "69,85 69,86 70,87 71,88 72,89 73,91 74,92 75,93 76,94 77,96 79,98 80,99 81,100 81,101 82,102 84,104 85,105 86,106 87,108 " +
         "88,109 89,110 90,112 91,113 92,114 94,116 94,116 95,118 97,120 97,120 99,123 100,124 101,125 102,127 103,128 105,130 " +
         "105,130 106,131 107,133 109,135 110,136 111,137 112,139 113,140 114,141 115,142 117,145 118,146 118,146 119,147 121,149 " +
         "121,150 123,152 123,152 124,153 125,155 126,156 128,158 128,159")
        .Split(' ').Select(p => p.Split(',')).Select(p => (int.Parse(p[0]), int.Parse(p[1]))).ToArray();

    static int R(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    // ---------------- strings ----------------

    public int Measure(string s)
    {
        if (s.Length == 0) return 0;
        var st = Current();
        return (FreeTypeFonts.Width(st.File, st.Weight, st.Px, st.Mono, st.Bold, s) + st.Scale - 1) / st.Scale;
    }

    public TextImage? Get(string s)
    {
        if (s.Length == 0) return null;
        var st = Current();
        var mode = (!Ui.OriginalText, !Ui.OriginalText ? Ui.Font : default(UiFont), st.Scale);
        if (mode != _cacheMode)
        {
            _cache.Clear();
            _cacheMode = mode;
        }
        TextImage Image(Texture2D t) => new(t, st.Scale, st.Lift, st.RefHeight);
        if (_cache.TryGet(s, out var t)) return Image(t);
        var (w, alpha) = FreeTypeFonts.Render(st.File, st.Weight, st.Px, st.Mono, st.Bold, s, st.Ascent, st.CellHeight);
        int h = st.CellHeight;
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = rgba[i * 4 + 3] = alpha[i];   // white, premultiplied
        t = new Texture2D(_device, w, h);
        t.SetData(rgba);
        return Image(_cache.Add(s, t));
    }

    public void Dispose() => _cache.Dispose();
}

/// <summary>
/// The fonts inside the executable, opened in FreeType once for the process, and the drawing of strings with them. The
/// faces (a font at a weight and a size) are made as they're first needed and kept; everything runs on the game's thread.
/// </summary>
static unsafe class FreeTypeFonts
{
    public const string NotoSans = "fonts/NotoSansJP-VF.ttf", NotoSerif = "fonts/NotoSerifJP-VF.ttf",
        DotGothic16 = "fonts/DotGothic16Pixel.ttf", PixelMplus12 = "fonts/PixelMplus12-Regular.ttf",
        KhDot12 = "fonts/KH-Dot-Kodenmachou-12.ttf", KhDot16 = "fonts/KH-Dot-Kodenmachou-16.ttf",
        IpaGothic = "freetype/ipag.ttf";   // not under fonts/: GDI isn't given it on Windows

    /// <summary>A font file's bytes (in native memory, for the process's life: FreeType reads them where they lie) and metrics.</summary>
    sealed record FontFile(IntPtr Data, int Length, int UnitsPerEm, int WinAscent, int WinDescent, int[] InstanceWeights);

    static FT_LibraryRec_* _lib;
    static readonly Dictionary<string, FontFile?> Files = new();
    static readonly Dictionary<(string File, int Weight, int Px), IntPtr> Faces = new();

    public static void Init()
    {
        if (_lib != null) return;
        FT_LibraryRec_* lib;
        FT_Error e;
        try { e = FT_Init_FreeType(&lib); }
        catch (Exception x) when (x is DllNotFoundException or FileLoadException or BadImageFormatException)
        {
            // the library beside the program (libfreetype.so) didn't load: on Linux, most likely a C library older than it
            throw new InvalidOperationException(OperatingSystem.IsLinux()
                ? "FreeType (libfreetype.so, beside BreezySRPG95) couldn't be loaded. It needs glibc 2.33 or newer: Ubuntu 22.04, "
                  + "Debian 12, Fedora 34, Linux Mint 21, SteamOS 3 or newer. Keep it in the same folder as BreezySRPG95."
                : "FreeType couldn't be loaded.", x);
        }
        if (e != FT_Error.FT_Err_Ok) throw new InvalidOperationException($"FreeType couldn't start: {e}");
        _lib = lib;
    }

    public static bool Has(string resource) => File(resource) is not null;

    static FontFile? File(string resource)
    {
        if (Files.TryGetValue(resource, out var f)) return f;
        using var s = typeof(FreeTypeFonts).Assembly.GetManifestResourceStream(resource);
        if (s is null) return Files[resource] = null;
        var b = new byte[s.Length];
        s.ReadExactly(b);
        var t = Tables(b);
        int upm = U16(b, t["head"] + 18);
        int winA = t.TryGetValue("OS/2", out int os2) ? U16(b, os2 + 74) : (short)U16(b, t["hhea"] + 4);
        int winD = t.ContainsKey("OS/2") ? U16(b, os2 + 76) : -(short)U16(b, t["hhea"] + 6);
        var p = (IntPtr)NativeMemory.Alloc((nuint)b.Length);
        Marshal.Copy(b, 0, p, b.Length);
        return Files[resource] = new FontFile(p, b.Length, upm, winA, winD, InstanceWeights(b, t));
    }

    /// <summary>A variable font's named instances' weights, in order (instance i + 1 is the face index's upper half); none for a static font.</summary>
    static int[] InstanceWeights(byte[] b, Dictionary<string, int> t)
    {
        if (!t.TryGetValue("fvar", out int o)) return Array.Empty<int>();
        int axesOff = U16(b, o + 4), axisCount = U16(b, o + 8), axisSize = U16(b, o + 10), count = U16(b, o + 12), size = U16(b, o + 14);
        int wght = Enumerable.Range(0, axisCount).FirstOrDefault(a => Encoding.ASCII.GetString(b, o + axesOff + a * axisSize, 4) == "wght", -1);
        if (wght < 0) return Array.Empty<int>();
        int first = o + axesOff + axisCount * axisSize;
        return Enumerable.Range(0, count).Select(i => S32(b, first + i * size + 4 + 4 * wght) >> 16).ToArray();
    }

    /// <summary>
    /// GDI's ascent and line height for a font at a size: round(winAscent × px / em), the same for the descent (a variable
    /// font's every weight shares them).
    /// </summary>
    public static (int Ascent, int Height) Line(string resource, int px)
    {
        var f = File(resource) ?? throw new FileNotFoundException("font not in the executable", resource);
        int a = R((double)f.WinAscent * px / f.UnitsPerEm), d = R((double)f.WinDescent * px / f.UnitsPerEm);
        return (a, a + d);
    }

    static int R(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>
    /// A variable font's named instance for a weight (1-based, the face index's upper half): the one with that weight (GDI's
    /// "Noto Sans JP Medium" is the 500 one), the nearest if there's none, 400 if none was asked for — never 0, the font's
    /// default, which is its lightest (Noto's is Thin). 0 for a static font.
    /// </summary>
    static int Instance(FontFile f, int weight)
    {
        if (f.InstanceWeights.Length == 0) return 0;
        int want = weight > 0 ? weight : 400;
        return Enumerable.Range(0, f.InstanceWeights.Length).MinBy(i => Math.Abs(f.InstanceWeights[i] - want)) + 1;
    }

    /// <summary>The weight a font is drawn at when <paramref name="weight"/> is asked for (0 for a static font): for the tests.</summary>
    public static int DrawnWeight(string resource, int weight)
    {
        var f = File(resource) ?? throw new FileNotFoundException("font not in the executable", resource);
        int i = Instance(f, weight);
        return i == 0 ? 0 : f.InstanceWeights[i - 1];
    }

    static FT_FaceRec_* Face(string resource, int weight, int px)
    {
        if (Faces.TryGetValue((resource, weight, px), out var have)) return (FT_FaceRec_*)have;
        Init();
        var f = File(resource) ?? throw new FileNotFoundException("font not in the executable", resource);
        int instance = Instance(f, weight);
        FT_FaceRec_* face;
        var e = FT_New_Memory_Face(_lib, (byte*)f.Data, f.Length, instance << 16, &face);
        if (e != FT_Error.FT_Err_Ok) throw new InvalidOperationException($"FreeType couldn't open {resource}: {e}");
        e = FT_Set_Pixel_Sizes(face, 0, (uint)px);
        if (e != FT_Error.FT_Err_Ok) throw new InvalidOperationException($"FreeType couldn't size {resource} at {px} px: {e}");
        Faces[(resource, weight, px)] = (IntPtr)face;
        return face;
    }

    // unsmoothed: hinted for one-bit pixels by the font's own instructions, not FreeType's automatic hinter (which would
    // move a pixel font's squares); smoothed: unhinted, as GDI draws these fonts — both checked closest to GDI
    const FT_LOAD MonoLoad = (FT_LOAD)0x20000 /* FT_LOAD_TARGET_MONO */ | FT_LOAD.FT_LOAD_NO_AUTOHINT;
    const FT_LOAD SmoothLoad = FT_LOAD.FT_LOAD_NO_HINTING | FT_LOAD.FT_LOAD_NO_BITMAP;

    /// <summary>The glyph for a character: in the face itself, else (a character it lacks) in IPAGothic at the same size.</summary>
    static (IntPtr Face, uint Glyph) Glyph(string resource, int weight, int px, int ch)
    {
        var face = Face(resource, weight, px);
        uint g = FT_Get_Char_Index(face, (UIntPtr)(uint)ch);
        if (g != 0 || resource == IpaGothic || !Has(IpaGothic)) return ((IntPtr)face, g);
        var ipa = Face(IpaGothic, 0, px);
        uint gi = FT_Get_Char_Index(ipa, (UIntPtr)(uint)ch);
        return gi != 0 ? ((IntPtr)ipa, gi) : ((IntPtr)face, 0);
    }

    /// <summary>Loads a glyph into its face's slot; false if it can't be.</summary>
    static bool Load(IntPtr face, uint glyph, bool mono) => FT_Load_Glyph((FT_FaceRec_*)face, glyph, mono ? MonoLoad : SmoothLoad) == FT_Error.FT_Err_Ok;

    static int Advance(IntPtr face) => (int)(((long)((FT_FaceRec_*)face)->glyph->advance.x + 32) >> 6);

    /// <summary>Each character's advance in a style, as <see cref="Width"/> adds them up (layout measures often).</summary>
    static readonly Dictionary<(string File, int Weight, int Px, bool Mono, bool Bold, int Ch), int> Advances = new();

    /// <summary>A string's width in pixels at its own size: the glyphs' advances, rounded each (and simulated bold's pixel).</summary>
    public static int Width(string resource, int weight, int px, bool mono, bool bold, string s)
    {
        int w = 0;
        foreach (var rune in s.EnumerateRunes())
        {
            var key = (resource, weight, px, mono, bold, rune.Value);
            if (!Advances.TryGetValue(key, out int advance))
            {
                var (face, glyph) = Glyph(resource, weight, px, rune.Value);
                Advances[key] = advance = Load(face, glyph, mono) ? Advance(face) + (bold ? 1 : 0) : 0;
            }
            w += advance;
        }
        return w;
    }

    /// <summary>
    /// A string drawn white on nothing: its width (as <see cref="Width"/>, at least 1) and coverage, a byte a pixel, in a
    /// cell <paramref name="cellHeight"/> high with the baseline <paramref name="ascent"/> down, as GDI's TextOut draws it.
    /// </summary>
    public static (int Width, byte[] Alpha) Render(string resource, int weight, int px, bool mono, bool bold, string s, int ascent, int cellHeight)
    {
        int width = Math.Max(1, Width(resource, weight, px, mono, bold, s)), h = Math.Max(1, cellHeight);
        var a = new byte[width * h];
        int pen = 0;
        foreach (var rune in s.EnumerateRunes())
        {
            var (faceP, glyph) = Glyph(resource, weight, px, rune.Value);
            if (!Load(faceP, glyph, mono)) continue;
            var face = (FT_FaceRec_*)faceP;
            var slot = face->glyph;
            int advance = Advance(faceP);
            if (slot->format != FT_Glyph_Format_.FT_GLYPH_FORMAT_BITMAP
                && FT_Render_Glyph(slot, mono ? FT_Render_Mode_.FT_RENDER_MODE_MONO : FT_Render_Mode_.FT_RENDER_MODE_NORMAL) != FT_Error.FT_Err_Ok)
            {
                pen += advance + (bold ? 1 : 0);
                continue;
            }
            var bm = slot->bitmap;
            int left = slot->bitmap_left, top = slot->bitmap_top;
            for (int y = 0; y < (int)bm.rows; y++)
            {
                byte* row = bm.buffer + y * bm.pitch;
                int Y = ascent - top + y;
                if (Y < 0 || Y >= h) continue;
                for (int x = 0; x < (int)bm.width; x++)
                {
                    byte v = bm.pixel_mode switch
                    {
                        FT_Pixel_Mode_.FT_PIXEL_MODE_MONO => (row[x >> 3] & (0x80 >> (x & 7))) != 0 ? (byte)255 : (byte)0,
                        FT_Pixel_Mode_.FT_PIXEL_MODE_GRAY => row[x],
                        _ => 0,
                    };
                    if (v == 0) continue;
                    if (mono) v = 255;
                    // GDI's simulated bold: the glyph again a pixel to the right
                    for (int dx = 0; dx <= (bold ? 1 : 0); dx++)
                    {
                        int X = pen + left + x + dx;
                        if (X >= 0 && X < width && a[Y * width + X] < v) a[Y * width + X] = v;
                    }
                }
            }
            pen += advance + (bold ? 1 : 0);
        }
        return (width, a);
    }

    // ---------------- the font files' tables (big-endian) ----------------

    static int U16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
    static int S32(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

    static Dictionary<string, int> Tables(byte[] b)
    {
        var d = new Dictionary<string, int>();
        for (int i = 0, n = U16(b, 4); i < n; i++)
            d[Encoding.ASCII.GetString(b, 12 + 16 * i, 4)] = S32(b, 12 + 16 * i + 8);
        return d;
    }
}
