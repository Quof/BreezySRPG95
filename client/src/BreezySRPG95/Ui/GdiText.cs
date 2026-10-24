using System.Runtime.InteropServices;
using BreezySRPG95.Config;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace BreezySRPG95.Ui;

/// <summary>
/// Text rendered exactly like the original runtime: Windows GDI, MS Gothic 18 px bold
/// (lfHeight -18, FW_BOLD), no anti-aliasing. Verified pixel-identical against a screenshot. Each distinct
/// string becomes a white-on-transparent texture, tinted at draw time. The restyled skins use a smooth face instead
/// (the Font setting, <see cref="Ui.Font"/>), rendered at the frame's render scale (<see cref="Ui.RenderScale"/>); layout
/// still measures at 640 × 480. The Windows renderer (<see cref="TextFonts"/>); <see cref="FreeTypeText"/> is the other.
/// </summary>
public sealed class GdiText : ITextFont
{
    readonly GraphicsDevice _device;
    readonly IntPtr _dc, _font;
    /// <summary>Bold text (the original's weight 700): the smooth faces' heavier cut.</summary>
    readonly bool _strong;
    IntPtr _selected;
    /// <summary>The smooth faces made so far, by font and render scale.</summary>
    readonly Dictionary<(UiFont Font, int Scale), Smooth> _smooth = new();
    /// <summary>
    /// Rendered strings, all in one mode: the bitmap font, or one restyled face at one scale (64 MB of them at most). The
    /// look is part of the mode: the original skin's font and a pixel font are both unsmoothed at scale 1.
    /// </summary>
    readonly TextureCache<string> _cache = new(64L << 20);
    (bool Restyled, bool Smooth, int Scale, UiFont Font) _cacheMode = (false, false, 1, default);
    public string FaceName { get; }
    public int Height { get; }

    /// <summary>A smooth face at one render scale, with what it takes to sit where the reference face does (<see cref="TextImage"/>).</summary>
    sealed record Smooth(IntPtr Font, string Face, float Lift, float RefHeight);

    public GdiText(GraphicsDevice device, int height = 18, int weight = 700, string envOverride = "BREEZY_FONT")
    {
        PrivateFonts.Load();
        _device = device;
        // calibration hook: e.g. BREEZY_FONT="height,weight"
        if (Environment.GetEnvironmentVariable(envOverride) is { } env && env.Split(',') is [var hs, var ws])
            (height, weight) = (int.Parse(hs), int.Parse(ws));
        Height = height;
        _strong = weight >= 700;
        _dc = CreateCompatibleDC(IntPtr.Zero);
        // MS Gothic is what the original used; the others ship with every Windows 10/11.
        foreach (var face in new[] { "ＭＳ ゴシック", "MS Gothic", "Yu Gothic", "Meiryo" })
        {
            var f = CreateFontW(-height, 0, 0, 0, weight, 0, 0, 0, 128 /*SHIFTJIS_CHARSET*/, 0, 0,
                3 /*NONANTIALIASED_QUALITY*/, 1 /*FIXED_PITCH*/, face);
            if (f == IntPtr.Zero) continue;
            var before = SelectObject(_dc, f);
            if (FaceOf() == face || face == "Meiryo")
            {
                _font = _selected = f;
                FaceName = FaceOf();
                return;
            }
            SelectObject(_dc, before);
            DeleteObject(f);
        }
        throw new InvalidOperationException("No Japanese GDI font available.");
    }

    string FaceOf()
    {
        var sb = new System.Text.StringBuilder(64);
        GetTextFaceW(_dc, sb.Capacity, sb);
        return sb.ToString();
    }

    void Select(IntPtr font)
    {
        if (font == _selected) return;
        SelectObject(_dc, font);
        _selected = font;
    }

    /// <summary>
    /// A smooth face's faces to try (the first one GDI has), its weight and its size against the original's pixel height.
    /// Noto Sans JP and Noto Serif JP travel inside the executable (<see cref="PrivateFonts"/>); BIZ UDPGothic and Yu
    /// Gothic UI are Windows'. Yu Gothic UI is what the restyled skins had at first, at the full height. MS Gothic is the
    /// original's own message face (FUN_00407888, 18 px bold), Windows' too and not ours to carry.
    /// </summary>
    static (string[] Faces, int Weight, float Size) Spec(UiFont f, bool strong) => f switch
    {
        UiFont.NotoSans => strong ? (new[] { "Noto Sans JP Medium", "Noto Sans JP" }, 500, 0.9f) : (new[] { "Noto Sans JP" }, 400, 0.9f),
        UiFont.NotoSerif => strong ? (new[] { "Noto Serif JP SemiBold", "Noto Serif JP" }, 600, 0.9f)
            : (new[] { "Noto Serif JP Medium", "Noto Serif JP" }, 500, 0.9f),
        UiFont.BizUd => (new[] { "BIZ UDPGothic", "BIZ UDPゴシック" }, strong ? 700 : 400, 0.84f),
        UiFont.MsGothic => (new[] { "ＭＳ ゴシック", "MS Gothic" }, strong ? 700 : 400, 1f),
        _ => (new[] { "Yu Gothic UI" }, strong ? 600 : 400, 1f),
    };

    /// <summary>
    /// A pixel font's face for a text of <paramref name="height"/> px and the size it's drawn at — the size its dots were
    /// designed for, so each lands on one pixel: 16 for the messages and names (16 px and up), 12 for the menus and the
    /// status window's class (a 16-px face would tower over the 12-px line it shares). Its English and Japanese family
    /// names (GDI reports the one for the system's language). Null for a smooth font. DotGothic16 itself draws each dot as
    /// its own square with gaps between them, and GDI without anti-aliasing fills the gaps in dense kanji (魔's top half a
    /// solid block): it's carried rebuilt, every dot a whole pixel's square.
    /// </summary>
    static (string[] Faces, int Px)? Pixel(UiFont f, int height) => (f, height >= 16) switch
    {
        (UiFont.PixelDot, true) => (new[] { "DotGothic16 Pixel" }, 16),   // DotGothic16 rebuilt dot for dot (tools/make_pixel_font.py)
        (UiFont.PixelDot, false) => (new[] { "PixelMplus12" }, 12),
        (UiFont.KhDot, true) => (new[] { "KH Dot Kodenmachou 16", "KHドット小伝馬町16" }, 16),
        (UiFont.KhDot, false) => (new[] { "KH Dot Kodenmachou 12", "KHドット小伝馬町12" }, 12),
        _ => null,
    };

    static readonly Dictionary<UiFont, bool> Present = new();

    /// <summary>Whether one of a font's faces is there — Windows' or the executable's own — for the options to say so.</summary>
    public static bool Available(UiFont f)
    {
        if (Present.TryGetValue(f, out var known)) return known;
        PrivateFonts.Load();
        var dc = CreateCompatibleDC(IntPtr.Zero);
        try
        {
            bool Has(string[] faces) => faces.Any(face =>
            {
                var font = CreateFontW(-12, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, face);
                var old = SelectObject(dc, font);
                var got = new System.Text.StringBuilder(64);
                GetTextFaceW(dc, got.Capacity, got);
                SelectObject(dc, old);
                DeleteObject(font);
                return faces.Contains(got.ToString());
            });
            // a pixel font needs both its faces, the small text's and the large's
            return Present[f] = Pixel(f, 12) is { } small && Pixel(f, 16) is { } large ? Has(small.Faces) && Has(large.Faces) : Has(Spec(f, false).Faces);
        }
        finally { DeleteDC(dc); }
    }

    /// <summary>What a face that isn't there falls back to (Yu Gothic UI and Meiryo ship with every Windows 10/11).</summary>
    static readonly string[] Fallback = { "Yu Gothic UI", "Meiryo UI", "Meiryo" };

    /// <summary>A smooth (anti-aliased) face, the first of <paramref name="faces"/> that GDI has; with <paramref name="sharp"/>, without anti-aliasing (a pixel font).</summary>
    (IntPtr Font, string Face) Create(IEnumerable<string> faces, int weight, int px, bool sharp = false)
    {
        uint quality = sharp ? 3u /*NONANTIALIASED_QUALITY*/ : 4u /*ANTIALIASED_QUALITY*/;
        string last = "";
        foreach (var face in faces)
        {
            last = face;
            var f = CreateFontW(-px, 0, 0, 0, weight, 0, 0, 0, 1 /*DEFAULT_CHARSET*/, 0, 0, quality, 0, face);
            if (f == IntPtr.Zero) continue;
            Select(f);
            if (FaceOf() == face) return (f, face);
            Select(_font);
            DeleteObject(f);
        }
        var any = CreateFontW(-px, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, quality, 0, last);   // whatever GDI makes of it
        Select(any);
        return (any, FaceOf());
    }

    (int Ascent, int Height) Metrics(IntPtr font)
    {
        Select(font);
        GetTextMetricsW(_dc, out var tm);
        return (tm.tmAscent, tm.tmHeight);
    }

    /// <summary>
    /// A smooth face at render scale <paramref name="k"/>: the middle of its em box (0.38 em above the baseline, for
    /// Japanese faces) put where Yu Gothic UI's would be at the full height, so a smaller or taller face stays centred
    /// on the lines and menu rows laid out for that one.
    /// </summary>
    Smooth MakeSmooth(UiFont f, int k)
    {
        if (Pixel(f, Height) is { } pixel) return MakePixel(pixel.Faces, pixel.Px);
        var (faces, weight, size) = Spec(f, _strong);
        int px = Math.Max(1, (int)MathF.Round(Height * size * k)), refPx = Height * k;
        var (font, face) = Create(faces.Concat(Fallback), weight, px);
        var (refFont, _) = Create(Fallback, Spec(UiFont.YuGothic, _strong).Weight, refPx);
        var (ascent, _) = Metrics(font);
        var (refAscent, refHeight) = Metrics(refFont);
        Select(_font);
        DeleteObject(refFont);
        float lift = (ascent - 0.38f * px - (refAscent - 0.38f * refPx)) / k;
        return new Smooth(font, face, lift, refHeight / (float)k);
    }

    /// <summary>
    /// A pixel font at the size its dots were designed for, without anti-aliasing, at the 640 × 480 screen's own size (the
    /// frame enlarges it nearest-neighbour, a dot to a square of the window's pixels); centred like a smooth face, on a
    /// whole pixel. Regular weight even for bold text: a thickened pixel font smears.
    /// </summary>
    Smooth MakePixel(string[] faces, int px)
    {
        var (font, face) = Create(faces.Concat(Fallback), 400, px, sharp: true);
        var (refFont, _) = Create(Fallback, Spec(UiFont.YuGothic, _strong).Weight, Height);
        var (ascent, _) = Metrics(font);
        var (refAscent, refHeight) = Metrics(refFont);
        Select(_font);
        DeleteObject(refFont);
        float lift = MathF.Round(ascent - 0.38f * px - (refAscent - 0.38f * Height));
        return new Smooth(font, face, lift, refHeight);
    }

    /// <summary>The font strings are rendered in now, whether anti-aliased, and at what scale.</summary>
    (IntPtr Font, bool Smooth, int Scale, Smooth? Face) Current()
    {
        if (!Ui.Restyled) return (_font, false, 1, null);
        bool pixel = Pixel(Ui.Font, Height) is not null;
        int k = pixel ? 1 : Math.Max(1, Ui.RenderScale);
        var key = (Ui.Font, k);
        if (!_smooth.TryGetValue(key, out var s))
        {
            if (_smooth.Count >= 6) DropSmooth();
            _smooth[key] = s = MakeSmooth(Ui.Font, k);
        }
        return (s.Font, !pixel, k, s);
    }

    void DropSmooth()
    {
        Select(_font);
        foreach (var s in _smooth.Values) DeleteObject(s.Font);
        _smooth.Clear();
    }

    /// <summary>The face the restyled skins' text is in now (null with the original skin).</summary>
    public string? SmoothFace => Current().Face?.Face;

    /// <summary>A string's width in 640 × 480 pixels.</summary>
    public int Measure(string s)
    {
        if (s.Length == 0) return 0;
        var (font, _, k, _) = Current();
        Select(font);
        GetTextExtentPoint32W(_dc, s, s.Length, out var size);
        return (size.cx + k - 1) / k;
    }

    /// <summary>White text on transparent background (null for an empty string).</summary>
    public TextImage? Get(string s)
    {
        if (s.Length == 0) return null;
        var (font, smooth, k, face) = Current();
        if ((Ui.Restyled, smooth, k, Ui.Font) != _cacheMode)
        {
            ClearCache();
            _cacheMode = (Ui.Restyled, smooth, k, Ui.Font);
        }
        TextImage Image(Texture2D t) => new(t, k, face?.Lift ?? 0, face?.RefHeight ?? 0);
        if (_cache.TryGet(s, out var t)) return Image(t);
        Select(font);
        GetTextExtentPoint32W(_dc, s, s.Length, out var size);
        int w = Math.Max(1, size.cx), h = Math.Max(1, size.cy);
        var bmi = new BITMAPINFO { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var bmp = CreateDIBSection(_dc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(_dc, bmp);
        SetBkColor(_dc, 0);
        SetTextColor(_dc, 0xFFFFFF);
        SetBkMode(_dc, 2 /*OPAQUE*/);
        TextOutW(_dc, 0, 0, s, s.Length);
        GdiFlush();
        var px = new byte[w * h * 4];
        Marshal.Copy(bits, px, 0, px.Length);
        SelectObject(_dc, old);
        DeleteObject(bmp);
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            byte a = smooth ? Math.Max(px[i * 4], px[i * 4 + 1]) : px[i * 4] > 127 ? (byte)255 : (byte)0;   // smooth, or 1-bit glyphs
            rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = a;
            rgba[i * 4 + 3] = a;
        }
        t = new Texture2D(_device, w, h);
        t.SetData(rgba);
        return Image(_cache.Add(s, t));
    }

    void ClearCache() => _cache.Clear();

    public void Dispose()
    {
        ClearCache();
        DeleteDC(_dc);   // first, so none of the fonts is still selected into it
        DeleteObject(_font);
        foreach (var s in _smooth.Values) DeleteObject(s.Font);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int bmiColors;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TEXTMETRICW
    {
        public int tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading, tmAveCharWidth, tmMaxCharWidth,
            tmWeight, tmOverhang, tmDigitizedAspectX, tmDigitizedAspectY;
        public char tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
        public byte tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
    }

    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline,
        uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern int GetTextFaceW(IntPtr hdc, int n, System.Text.StringBuilder face);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern bool GetTextMetricsW(IntPtr hdc, out TEXTMETRICW tm);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern bool GetTextExtentPoint32W(IntPtr hdc, string s, int n, out SIZE size);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern bool TextOutW(IntPtr hdc, int x, int y, string s, int n);
    [DllImport("gdi32.dll")] static extern uint SetTextColor(IntPtr hdc, uint c);
    [DllImport("gdi32.dll")] static extern uint SetBkColor(IntPtr hdc, uint c);
    [DllImport("gdi32.dll")] static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
    [DllImport("gdi32.dll")]
    static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
}

/// <summary>
/// The fonts BreezySRPG95 carries inside the executable — Noto Sans JP, Noto Serif JP, DotGothic16 (rebuilt) and the KH Dot font's
/// 小伝馬町 12 and 16 (SIL Open Font License 1.1, see fonts/OFL.txt) and PixelMplus12 (M+ FONT LICENSE,
/// fonts/MPLUS-LICENSE.txt) — handed to GDI for this process only, so the restyled skins look the same on every PC.
/// </summary>
static class PrivateFonts
{
    static bool _loaded;
    /// <summary>The fonts' bytes, kept for the process's life (GDI is given them where they lie).</summary>
    static readonly List<IntPtr> Memory = new();

    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        var asm = typeof(PrivateFonts).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("fonts/") && n.EndsWith(".ttf")))
        {
            using var s = asm.GetManifestResourceStream(name);
            if (s is null) continue;
            var bytes = new byte[s.Length];
            s.ReadExactly(bytes);
            var p = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, p, bytes.Length);
            uint count = 0;
            if (AddFontMemResourceEx(p, (uint)bytes.Length, IntPtr.Zero, ref count) != IntPtr.Zero) Memory.Add(p);
            else Marshal.FreeHGlobal(p);
        }
    }

    [DllImport("gdi32.dll")] static extern IntPtr AddFontMemResourceEx(IntPtr font, uint size, IntPtr reserved, ref uint fonts);
}
