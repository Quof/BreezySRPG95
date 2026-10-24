using BreezySRPG95.Config;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Srpg95.Data;

namespace BreezySRPG95.Ui;

/// <summary>
/// Drawing primitives for the 640x480 screen as the original runtime draws them: panels in SYSTEM.BMP's swatches
/// (FUN_0041203c), message balloons (FUN_004121ac), menu bars, the screen frame, the mouse cursor, the exe's own
/// 12-px font (KANJIFONT) and GDI text — and the same pieces in the restyled skins of the options.
/// </summary>
public sealed class Ui : IDisposable
{
    public const int LineHeight = 22;
    /// <summary>Message / window text: dark on the original's light green windows, light on the restyled dark ones.</summary>
    public static Color TextColor => Skin switch
    {
        UiSkin.Original => MessageInk, UiSkin.Gilded => new Color(250, 250, 250), _ => new Color(236, 240, 248),
    };

    /// <summary>
    /// The colours the exe reads from the game's SYSTEM.BMP when it starts (FUN_004035e4): 16 swatches on row 288, 16 px
    /// apart, and the message text's from pixel (48,32) inside the balloon. Most games keep the RTP's (below); some
    /// change them with the balloon (Kenjyutsu: white text on a navy balloon, yellow and white panel edges).
    /// </summary>
    static Color[] _swatches =
    {
        new(248, 216, 192), new(0, 0, 48), new(248, 216, 192), new(64, 0, 0), new(248, 248, 224), new(40, 40, 8),
        new(248, 248, 224), new(0, 32, 0), new(248, 248, 248), new(152, 152, 152), new(248, 248, 248), new(0, 64, 0),
        new(160, 248, 96), new(0, 32, 0), new(122, 122, 122), new(0, 32, 0),
    };
    static Color MessageInk = new(16, 16, 0);

    /// <summary>Swatch <paramref name="i"/> (0–15) of the game's SYSTEM.BMP.</summary>
    public static Color Swatch(int i) => _swatches[i];

    static void ReadSwatches(IndexedBitmap? sys)
    {
        if (sys is null || sys.Width < 256 || sys.Height < 289) return;
        Color At(int x, int y) { uint c = sys.Palette[sys.Pixels[y * sys.Width + x]]; return new Color((byte)(c >> 16), (byte)(c >> 8), (byte)c); }
        _swatches = Enumerable.Range(0, 16).Select(i => At(i * 16, 288)).ToArray();
        MessageInk = At(48, 32);
    }

    /// <summary>The interface's look (options): the original's, or a restyled one with smooth fonts and dark windows.</summary>
    public static UiSkin Skin { get; set; }
    /// <summary>The modern skin: dark translucent rounded windows, accent highlights.</summary>
    public static bool Modern => Skin == UiSkin.Modern;
    /// <summary>The gilded skin: dark translucent rectangles in fine gold frames, the speaker's name in its own box.</summary>
    public static bool Gilded => Skin == UiSkin.Gilded;
    /// <summary>Any restyled skin: smooth fonts and light text on dark windows.</summary>
    public static bool Restyled => Skin != UiSkin.Original;
    /// <summary>The original's own text: the original look in MS Gothic (its 12-px menus in the exe's font); another font picked draws its own way in that look too.</summary>
    public static bool OriginalText => !Restyled && Font == UiFont.MsGothic;
    /// <summary>The restyled skins' smooth font (options).</summary>
    public static UiFont Font { get; set; }
    /// <summary>
    /// How many render pixels the frame has to a 640 × 480 pixel (the client draws at the window's size): the restyled
    /// skins' text is rendered at that size (<see cref="TextImage"/>), everything else is scaled up whole.
    /// </summary>
    public static int RenderScale { get; set; } = 1;
    public static readonly Color ModernBase = new(22, 26, 38), Accent = new(104, 168, 255);
    public static Color ModernBorder => new Color(255, 255, 255) * 0.16f;
    public static readonly Color WindowFill = new(184, 200, 160);
    static readonly Color EdgeLight = new(241, 241, 241), EdgeDark = new(96, 96, 96);

    readonly GraphicsDevice _device;
    readonly Texture2D _pixel;
    readonly Textures _tex;
    public ITextFont Text { get; }
    /// <summary>Menu/panel text (choices, camp menu): smaller than message text.</summary>
    public ITextFont SmallText { get; }
    /// <summary>16 px bold (FUN_00407888): unit names on the battle scene's info panels and the status window.</summary>
    public ITextFont NameText { get; }
    /// <summary>The restyled skins' 14-px bold (the status window's class, which the original draws twice a pixel apart).</summary>
    public ITextFont BoldText { get; }

    public Ui(GraphicsDevice device, Textures tex)
    {
        _device = device;
        _tex = tex;
        ReadSwatches(tex.Bitmap("SYSTEM.BMP"));
        Text = TextFonts.Create(device);
        SmallText = TextFonts.Create(device, 12, 400, "BREEZY_SMALLFONT");
        NameText = TextFonts.Create(device, 16, 700, "BREEZY_FONT");
        Pad = new PadGlyphs(device);
        BoldText = TextFonts.Create(device, 14, 700, "BREEZY_BOLDFONT");
        _pixel = new Texture2D(device, 1, 1);
        _pixel.SetData(new[] { Color.White });
    }

    /// <summary>
    /// Rounded rectangle (radius 8) filled with the window colour; 1-px outline that is light
    /// on the top/left and dark on the bottom/right, blending around the corners.
    /// </summary>
    Texture2D BuildWindow(int w, int h)
    {
        const float r = 8f;
        var px = new Color[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float cx = Math.Clamp(x + 0.5f, r, w - r), cy = Math.Clamp(y + 0.5f, r, h - r);
                float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                bool inCorner = dx != 0 && dy != 0;
                float dist = inCorner ? r - d : Math.Min(Math.Min(x + 0.5f, w - x - 0.5f), Math.Min(y + 0.5f, h - y - 0.5f));
                if (dist < 0) continue;
                if (dist < 1)
                {
                    // normal direction decides light (up/left) vs dark (down/right)
                    float nx = inCorner ? dx / d : (x < w / 2 ? -1 : 1) * (Math.Abs(x + 0.5f - w / 2f) > w / 2f - 1 ? 1 : 0);
                    float ny = inCorner ? dy / d : (y < h / 2 ? -1 : 1) * (Math.Abs(y + 0.5f - h / 2f) > h / 2f - 1 ? 1 : 0);
                    float t = Math.Clamp((nx + ny) * 0.5f + 0.5f, 0, 1);
                    px[y * w + x] = Color.Lerp(EdgeLight, EdgeDark, t);
                }
                else px[y * w + x] = WindowFill;
            }
        var tex = new Texture2D(_device, w, h);
        tex.SetData(px);
        return tex;
    }

    public void Fill(SpriteBatch sb, Rectangle r, Color c) => sb.Draw(_pixel, r, c);

    /// <summary>The original's 12-px font, read from SRPGEXEC.EXE (null when it can't be).</summary>
    public ExeFont? MenuFont { get; set; }

    /// <summary>Gamepad buttons as pictures (the options' gamepad page).</summary>
    public PadGlyphs Pad { get; }

    /// <summary>
    /// The original's 12-px text (FUN_00407744) at GDI position (x, y): the exe's own font; with a restyled skin or no exe,
    /// a 12-px Windows font a pixel right and two down (where its glyphs then fall closest to the exe's).
    /// </summary>
    public void Text12(SpriteBatch sb, string s, int x, int y, Color c)
    {
        if (OriginalText && MenuFont?.Get(s) is { } t) sb.Draw(t, new Vector2(x, y), c);
        else if (SmallText.Get(s) is { } g) g.Draw(sb, new Vector2(x + 1, Line16Y(g, y)), c);
    }

    /// <summary>Where a smooth 12-px string goes to sit in the middle of the 16-px line (a menu row's bar) from <paramref name="y"/>.</summary>
    public static float Line16Y(TextImage g, int y) => OriginalText ? y + 2 : y + (16 - g.LayoutHeight) / 2;

    /// <summary>The width <see cref="Text12"/> gives a string.</summary>
    public int Measure12(string s) => OriginalText && MenuFont is not null ? ExeFont.Measure(s) : SmallText.Measure(s);

    /// <summary>SYSTEM.BMP with palette index 0 transparent (arrows, cursors, balloon pieces).</summary>
    public Texture2D? SystemBmp => _tex.Get("SYSTEM.BMP");

    /// <summary>A face graphic, FACE_nnn.BMP (null for none or a missing one).</summary>
    public Texture2D? Face(int id) => id > 0 ? _tex.Get($"FACE_{id:000}.BMP") : null;

    public void Frame(SpriteBatch sb, Rectangle r, Color c, int t = 1)
    {
        Fill(sb, new Rectangle(r.X, r.Y, r.Width, t), c);
        Fill(sb, new Rectangle(r.X, r.Bottom - t, r.Width, t), c);
        Fill(sb, new Rectangle(r.X, r.Y, t, r.Height), c);
        Fill(sb, new Rectangle(r.Right - t, r.Y, t, r.Height), c);
    }

    /// <summary>
    /// The message balloon (FUN_004121ac, its skin from FUN_00411d74): SYSTEM.BMP's 48 × 48 corner as a nine-slice — 16-px
    /// corners and edges, the fill between — 416 × 128 px from <paramref name="topLeft"/>; then the speech tail
    /// (FUN_00406f98), 32 × 16 from SYSTEM.BMP (0,48) 16 px left of the balloon for a face on the left (<paramref name="tail"/>
    /// −1) or (32,48) 16 px before its right edge for a face on the right (+1), 16 px down. Palette index 0 is transparent.
    /// </summary>
    public void MessageBalloon(SpriteBatch sb, Point topLeft, int tail)
    {
        if (Modern)
        {
            ModernBox(sb, new Rectangle(topLeft.X + 1, topLeft.Y + 1, BalloonWidth - 2, BalloonHeight - 2), ModernBase, 12, 0.92f);
            return;
        }
        if (Gilded)
        {
            GildedBox(sb, new Rectangle(topLeft.X, topLeft.Y + 6, BalloonWidth, BalloonHeight - 6), GildedNavy, 0.84f, tail, 58);
            return;
        }
        var sys = _tex.Get("SYSTEM.BMP");
        if (sys is null) return;
        for (int y = 0; y < BalloonHeight; y += 16)
            for (int x = 0; x < BalloonWidth; x += 16)
            {
                int sx = x == 0 ? 0 : x == BalloonWidth - 16 ? 32 : 16, sy = y == 0 ? 0 : y == BalloonHeight - 16 ? 32 : 16;
                sb.Draw(sys, new Vector2(topLeft.X + x, topLeft.Y + y), new Rectangle(sx, sy, 16, 16), Color.White);
            }
        if (tail < 0) sb.Draw(sys, new Vector2(topLeft.X - 16, topLeft.Y + 16), new Rectangle(0, 48, 32, 16), Color.White);
        if (tail > 0) sb.Draw(sys, new Vector2(topLeft.X + BalloonWidth - 16, topLeft.Y + 16), new Rectangle(32, 48, 32, 16), Color.White);
    }

    /// <summary>The message balloon's size (FUN_004148a4: 26 × 8 cells of 16 px).</summary>
    public const int BalloonWidth = 26 * 16, BalloonHeight = 8 * 16;

    /// <summary>Generic window for menus (same skin, any size).</summary>
    public void Window(SpriteBatch sb, Rectangle r)
    {
        if (Modern) { ModernBox(sb, r, ModernBase, 10, 0.92f); return; }
        if (Gilded) { GildedBox(sb, r, GildedNavy, 0.86f); return; }
        // Built on demand per size; menus are few.
        if (!_windows.TryGet((r.Width, r.Height), out var t)) t = _windows.Add((r.Width, r.Height), BuildWindow(r.Width, r.Height));
        sb.Draw(t, r.Location.ToVector2(), Color.White);
    }
    readonly TextureCache<(int, int)> _windows = new(16L << 20);

    // ---------------- modern interface ----------------

    /// <summary>The modern boxes by size and look (the message box's height changes with its lines).</summary>
    readonly TextureCache<(int W, int H, int R, Color Fill, float A)> _modern = new(32L << 20);

    /// <summary>A soft-shadowed, rounded, translucent box with a faint light edge and a slight top-down gradient.</summary>
    public void ModernBox(SpriteBatch sb, Rectangle r, Color fill, int radius, float alpha, bool shadow = true)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        var key = (r.Width, r.Height, radius, fill, alpha);
        if (!_modern.TryGet(key, out var tex)) tex = _modern.Add(key, BuildRounded(r.Width, r.Height, radius, fill, alpha));
        if (shadow) sb.Draw(tex, new Vector2(r.X + 2, r.Y + 3), Color.Black * 0.45f);
        sb.Draw(tex, r.Location.ToVector2(), Color.White);
    }

    Texture2D BuildRounded(int w, int h, int radius, Color fill, float alpha)
    {
        float rad = Math.Min(radius, Math.Min(w, h) / 2f);
        var border = new Color(255, 255, 255);
        var px = new Color[w * h];
        for (int y = 0; y < h; y++)
        {
            // gradient: a little lighter at the top
            var row = Color.Lerp(Color.Lerp(fill, Color.White, 0.07f), fill, h <= 1 ? 1 : y / (h - 1f));
            for (int x = 0; x < w; x++)
            {
                float cx = Math.Clamp(x + 0.5f, rad, w - rad), cy = Math.Clamp(y + 0.5f, rad, h - rad);
                float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                float d = rad - MathF.Sqrt(dx * dx + dy * dy);                 // distance inside the rounded edge
                float edge = Math.Min(Math.Min(x + 0.5f, w - x - 0.5f), Math.Min(y + 0.5f, h - y - 0.5f));
                float inside = dx != 0 && dy != 0 ? d : edge;
                float cover = Math.Clamp(inside + 0.5f, 0, 1);                 // anti-aliased outline
                if (cover <= 0) continue;
                float rim = Math.Clamp(1.5f - inside, 0, 1);                   // 1-px light rim
                var c = Color.Lerp(row, border, rim * 0.22f);
                px[y * w + x] = c * (alpha * cover);
            }
        }
        var tex = new Texture2D(_device, w, h);
        tex.SetData(px);
        return tex;
    }

    /// <summary>The modern selection bar: a softly pulsing accent fill with a bright left notch.</summary>
    public void ModernHighlight(SpriteBatch sb, Rectangle r, int frame)
    {
        Fill(sb, r, Accent * (0.55f + 0.15f * MathF.Sin(frame * 0.12f)));
        Fill(sb, new Rectangle(r.X, r.Y, 2, r.Height), Accent);
    }

    static readonly Color[] ModernTints = { new(36, 64, 128), new(122, 36, 44), new(120, 96, 32), ModernBase };

    // ---------------- gilded interface ----------------

    public static readonly Color GoldLight = new(240, 214, 150), Gold = new(200, 164, 96), GoldDeep = new(128, 96, 50);
    public static readonly Color GildedNavy = new(12, 18, 40), NameNavy = new(22, 32, 66);
    /// <summary>Panel tints by <see cref="PanelColor"/>: the side colours of battle and status panels, navy for menus.</summary>
    static readonly Color[] GildedTints = { new(14, 28, 66), new(56, 14, 22), new(48, 38, 12), GildedNavy };

    // the gold's pictures (Gilding), made at the render scale they were made for, by what they show (a message box's
    // width follows its text, so there are many of those)
    readonly TextureCache<(int W, int H, Color Fill, float A, int Pointer, int PointerY)> _gilded = new(64L << 20);
    readonly TextureCache<(int W, int H)> _highlights = new(16L << 20);
    readonly TextureCache<int> _rules = new(8L << 20);
    readonly TextureCache<Rectangle> _frames = new(16L << 20);
    readonly TextureCache<int> _arrows = new(4L << 20);
    int _giltScale;

    /// <summary>A gold picture from a cache, made (at the render scale) if it isn't there; every cache is emptied when the scale changes.</summary>
    Texture2D Gilt<TKey>(TextureCache<TKey> cache, TKey key, int w, int h, Func<int, Color[]> make) where TKey : notnull
    {
        int k = Math.Max(1, RenderScale);
        if (k != _giltScale)
        {
            DisposeGilt();
            _giltScale = k;
        }
        if (cache.TryGet(key, out var t)) return t;
        t = new Texture2D(_device, w * k, h * k);
        t.SetData(make(k));
        return cache.Add(key, t);
    }

    void DisposeGilt()
    {
        _gilded.Clear();
        _highlights.Clear();
        _rules.Clear();
        _frames.Clear();
        _arrows.Clear();
    }

    /// <summary>Draws a gold picture with its corner at (x, y), each of its pixels one of the render's.</summary>
    void DrawGilt(SpriteBatch sb, Texture2D t, float x, float y, Color c) =>
        sb.Draw(t, new Vector2(x, y), null, c, 0, Vector2.Zero, 1f / _giltScale, SpriteEffects.None, 0);

    /// <summary>
    /// The gilded box (<see cref="Gilding.Box"/>): a dark translucent rectangle with perfectly straight edges, a thin gold
    /// border along them. <paramref name="pointer"/> −1 / +1 grows a small pointer out of the left / right side, its tip
    /// <paramref name="pointerY"/> px below the top, toward a speaking face; the border runs round it.
    /// </summary>
    public void GildedBox(SpriteBatch sb, Rectangle r, Color fill, float alpha = 0.86f, int pointer = 0, int pointerY = 0)
    {
        if (r.Width < 8 || r.Height < 8) return;
        int w = r.Width, h = r.Height, py = pointer == 0 ? 0 : pointerY;
        const int m = Gilding.Margin;
        var t = Gilt(_gilded, (w, h, fill, alpha, pointer, py), w + 2 * m, h + 2 * m, k => Gilding.Box(w, h, fill, alpha, pointer, py, k));
        DrawGilt(sb, t, r.X - m, r.Y - m, Color.White);
    }

    /// <summary>A gilded frame round a picture: the box's border, nothing filled in but the border's faint shadow.</summary>
    public void GildedFrame(SpriteBatch sb, Rectangle r) => GildedBox(sb, r, Color.Black, 0);

    /// <summary>The gilded selection bar: a soft gold glow fading to the right, gently pulsing.</summary>
    public void GildedHighlight(SpriteBatch sb, Rectangle r, int frame)
    {
        var t = Gilt(_highlights, (r.Width, r.Height), r.Width, r.Height, k => Gilding.Highlight(r.Width, r.Height, k));
        DrawGilt(sb, t, r.X, r.Y, Color.White * (0.86f + 0.14f * MathF.Sin(frame * 0.1f)));
    }

    /// <summary>A gilded divider <paramref name="w"/> wide with its middle on row <paramref name="y"/> (the original skin's etched rules).</summary>
    public void GoldRule(SpriteBatch sb, int x, int y, int w)
    {
        if (w < 8) return;
        var t = Gilt(_rules, w, w, Gilding.DividerHeight, k => Gilding.Divider(w, k));
        DrawGilt(sb, t, x, y - Gilding.DividerHeight / 2, Color.White);
    }

    /// <summary>A small gold arrowhead centred on (cx, cy), pointing (dx, dy).</summary>
    public void Arrow(SpriteBatch sb, int cx, int cy, int dx, int dy)
    {
        const int n = Gilding.ArrowSize;
        var t = Gilt(_arrows, 0, n, n, Gilding.Arrow);
        float turn = dy < 0 ? 0 : dy > 0 ? MathF.PI : dx < 0 ? -MathF.PI / 2 : MathF.PI / 2;
        sb.Draw(t, new Vector2(cx, cy), null, Color.White, turn, new Vector2(t.Width / 2f, t.Height / 2f), 1f / _giltScale, SpriteEffects.None, 0);
    }

    /// <summary>Text with a soft shadow a pixel down and right, to read on the translucent gilded boxes.</summary>
    public void ShadowText(SpriteBatch sb, ITextFont font, string s, Vector2 pos, Color c)
    {
        if (font.Get(s) is not { } t) return;
        t.Draw(sb, pos + new Vector2(1, 1), Color.Black * 0.7f);
        t.Draw(sb, pos, c);
    }

    /// <summary>The speaker's name in its own small gilded box, at least <paramref name="minWidth"/> wide, the name centred.</summary>
    public Rectangle NameBox(SpriteBatch sb, string name, int x, int y, int minWidth = 96)
    {
        int w = Math.Max(minWidth, NameText.Measure(name) + 32);
        var r = new Rectangle(x, y, w, 26);
        GildedBox(sb, r, NameNavy, 0.95f);
        if (NameText.Get(name) is { } t)
        {
            var p = new Vector2(r.X + (w - t.Width) / 2, r.Y + (r.Height - t.LayoutHeight) / 2);
            t.Draw(sb, p + new Vector2(1, 1), Color.Black * 0.7f);
            t.Draw(sb, p, Color.White);
        }
        return r;
    }

    static (string Before, string After, string Next)[] _nameTags = ParseNameTags(Settings.DefaultNameTags);

    /// <summary>
    /// The Nametag detection setting: patterns separated by spaces, * (or ＊) standing for the name, e.g. "*： *:" or
    /// "【*】"; a pattern without one is what the line ends with. A | (or ｜) is a line break: what follows it is what the
    /// second line must start with ("*|「": a name alone on its line, the words under it in 「」), that line staying in the
    /// text. Empty: no message has a speaker.
    /// </summary>
    public static string NameTags { set => _nameTags = ParseNameTags(value); }

    static (string, string, string)[] ParseNameTags(string? patterns) => (patterns ?? "").Replace('＊', '*').Replace('｜', '|')
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.IndexOf('|') is var bar and >= 0 ? (p[..bar], p[(bar + 1)..].Replace("|", "")) : (p, ""))
        .Select(t => t.Item1.IndexOf('*') is var star and >= 0 ? (t.Item1[..star], t.Item1[(star + 1)..].Replace("*", ""), t.Item2) : ("", t.Item1, t.Item2))
        .ToArray();

    /// <summary>
    /// A message's speaker — its first line when that is just 「name：」 (or what <see cref="NameTags"/> says), as nearly
    /// every message in the game starts — and the rest. A label for a condition (勝利条件：, 敗北条件：, ボーナス条件：,
    /// 終了条件： …) is written the same way but is no one speaking: in all five games every such first line with 条件 in
    /// it is one, so it stays in the text.
    /// </summary>
    public static (string? Name, IReadOnlyList<string> Text) SplitSpeaker(IReadOnlyList<string> lines)
    {
        if (lines.Count > 0 && lines[0].Trim() is { Length: > 0 } first && !first.Contains("条件"))
            foreach (var (before, after, next) in _nameTags)
                if ((next.Length == 0 || lines.Count > 1 && lines[1].TrimStart().StartsWith(next, StringComparison.Ordinal))
                    && first.Length > before.Length + after.Length && first.StartsWith(before, StringComparison.Ordinal) && first.EndsWith(after, StringComparison.Ordinal)
                    && first[before.Length..^after.Length].Trim() is { Length: > 0 } name)
                    return (name, lines.Skip(1).ToList());
        return (null, lines);
    }

    /// <summary>
    /// A message, its window area 608 × 160 from (16, <paramref name="windowY"/>): the top, centre or bottom window
    /// (FUN_004148a4), or the battle scene's lower strip. The face 16 px in and down from the area's corner (x 480 on the
    /// right). The original and modern skins: the balloon 16 px down at x 112 without a face, 192 with one on the left, 32
    /// with one on the right, its tail toward the face, the text 18 px into it from 28 px below the area's top, 22-px lines.
    /// The gilded skin: a text box beside the face with a small pointer toward it (across the area without a face), and a
    /// first line that is just 「name：」 in a name box over the text box's top-left corner. The modern skin: the balloon's
    /// place, that name in a box of its own above the text box, apart from it. Both fit the box to the text
    /// (<see cref="TextWidth"/>): never wider than before, narrower on the side away from the face, centred without one.
    /// </summary>
    public void Message(SpriteBatch sb, Texture2D? face, int windowY, bool faceRight, bool faceFlip, IReadOnlyList<string> lines,
        bool tailLeft = false)
    {
        if (face is not null)
            sb.Draw(face, new Rectangle(faceRight ? 480 : 32, windowY + 16, 128, 128), null, Color.White, 0, Vector2.Zero,
                faceFlip ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0);
        bool left = face is not null && !faceRight || tailLeft, right = face is not null && faceRight;
        if (Modern)
        {
            // the speaker in a box of its own above the text box, the two apart; the text box stays where it is without one,
            // so the text is level either way
            var (who, said) = SplitSpeaker(lines);
            int mx = left ? 192 : right ? 32 : 112, bottom = windowY + 143;
            int bw = Math.Min(BalloonWidth - 2, 17 + TextWidth(said) + 17);
            int bx = left ? mx + 1 : right ? mx + BalloonWidth - 1 - bw : 320 - bw / 2;
            // the text box from 28 px below the area's top (room for the name box above it); lines that don't fit grow it
            // downwards, its top and the name box staying put — up only as far as it would leave the screen
            int top = windowY + 16 + 28;
            bottom = Math.Max(bottom, top + 11 + said.Count * LineHeight + 9);
            if (bottom > 476) (top, bottom) = (top - (bottom - 476), 476);
            if (who is not null)
            {
                int w = Math.Max(96, NameText.Measure(who) + 32);
                var nr = new Rectangle(bx, top - 28, w, 24);
                ModernBox(sb, nr, ModernBase, 8, 0.92f);
                if (NameText.Get(who) is { } t) ShadowText(sb, NameText, who, new Vector2(nr.X + (w - t.Width) / 2, nr.Y + (nr.Height - t.LayoutHeight) / 2), Color.White);
            }
            ModernBox(sb, new Rectangle(bx, top, bw, bottom - top), ModernBase, 12, 0.92f);
            for (int i = 0; i < said.Count; i++) DrawText(sb, said[i], new Vector2(bx + 17, top + 11 + i * LineHeight), TextColor);
            return;
        }
        if (!Gilded)
        {
            int bx = left ? 192 : right ? 32 : 112;
            MessageBalloon(sb, new Point(bx, windowY + 16), left ? -1 : right ? 1 : 0);
            for (int i = 0; i < lines.Count; i++) DrawText(sb, lines[i], new Vector2(bx + 18, windowY + 28 + i * LineHeight), TextColor);
            return;
        }
        var (name, text) = SplitSpeaker(lines);
        int x0 = left ? 172 : 24, x1 = right ? 468 : 616, need = 20 + TextWidth(text) + 20;
        if (x1 - x0 > need)
        {
            if (left) x1 = x0 + need;
            else if (right) x0 = x1 - need;
            else (x0, x1) = (320 - need / 2, 320 - need / 2 + need);
        }
        var box = new Rectangle(x0, windowY + 22, x1 - x0, 128);
        GildedBox(sb, box, GildedNavy, 0.84f, left ? -1 : right ? 1 : 0, windowY + 80 - box.Y);
        if (name is not null) NameBox(sb, name, box.X + 14, box.Y - 13);
        int ty = box.Y + (name is null && text.Count > 4 ? 14 : 20);   // level with or without a name box (five lines need the room)
        for (int i = 0; i < text.Count; i++) ShadowText(sb, Text, text[i], new Vector2(box.X + 20, ty + i * LineHeight), TextColor);
    }

    public void DrawText(SpriteBatch sb, string s, Vector2 pos, Color c) => Text.Get(s)?.Draw(sb, pos, c);

    /// <summary>A line as long as a message line gets: 20 full-width characters, the editor's 40 bytes (thousands of lines in the five games fill it, none goes past).</summary>
    static readonly string FullLine = new('国', 20);

    /// <summary>
    /// How wide a message's text is to be taken in the restyled skins: a full line in the font chosen — a kanji, the
    /// widest full-width glyph, 20 times — or this message's widest line, should a game have longer ones.
    /// </summary>
    int TextWidth(IReadOnlyList<string> lines) => Math.Max(Text.Measure(FullLine), lines.Count == 0 ? 0 : lines.Max(Text.Measure));

    /// <summary>
    /// Screen frame from SFRAME_H/V.BMP: 16-px strips around the 608x448 map viewport, copied opaque over the tiles
    /// (FUN_00413768 → FUN_004069a4); the units and a picture are drawn after it, so they cover it where they reach. Strip
    /// variants: red arrow = can scroll that way, grey = can't.
    /// </summary>
    public void ScreenFrame(SpriteBatch sb, bool up, bool down, bool left, bool right)
    {
        if (Modern)
        {
            var bg = new Color(12, 14, 20);
            Fill(sb, new Rectangle(0, 0, 640, 16), bg);
            Fill(sb, new Rectangle(0, 464, 640, 16), bg);
            Fill(sb, new Rectangle(0, 16, 16, 448), bg);
            Fill(sb, new Rectangle(624, 16, 16, 448), bg);
            Frame(sb, new Rectangle(15, 15, 610, 450), ModernBorder);
            // a small chevron in the middle of each side that can still scroll
            void Chevron(int cx, int cy, int dx, int dy)
            {
                for (int k = 0; k < 4; k++)
                {
                    int len = 7 - 2 * k;
                    if (dx == 0) Fill(sb, new Rectangle(cx - len / 2, cy + (dy < 0 ? k : -k), len, 1), Accent);
                    else Fill(sb, new Rectangle(cx + (dx < 0 ? k : -k), cy - len / 2, 1, len), Accent);
                }
            }
            if (up) Chevron(320, 6, 0, -1);
            if (down) Chevron(320, 473, 0, 1);
            if (left) Chevron(6, 240, -1, 0);
            if (right) Chevron(633, 240, 1, 0);
            return;
        }
        if (Gilded)
        {
            // dark strips with a thin gold line round the view (Gilding.ScreenFrame), gold arrows where the view can scroll
            foreach (var part in FrameParts)
                DrawGilt(sb, Gilt(_frames, part, part.Width, part.Height, k => Gilding.ScreenFrame(part, k)), part.X, part.Y, Color.White);
            if (up) Arrow(sb, 320, 6, 0, -1);
            if (down) Arrow(sb, 320, 474, 0, 1);
            if (left) Arrow(sb, 6, 240, -1, 0);
            if (right) Arrow(sb, 634, 240, 1, 0);
            return;
        }
        var h = _tex.Get("SFRAME_H.BMP", transparent: false);
        var v = _tex.Get("SFRAME_V.BMP", transparent: false);
        if (h is null || v is null) return;
        sb.Draw(h, new Vector2(0, 0), new Rectangle(0, up ? 0 : 32, 640, 16), Color.White);
        sb.Draw(h, new Vector2(0, 464), new Rectangle(0, down ? 16 : 48, 640, 16), Color.White);
        sb.Draw(v, new Vector2(0, 16), new Rectangle(left ? 0 : 32, 0, 16, 448), Color.White);
        sb.Draw(v, new Vector2(624, 16), new Rectangle(right ? 16 : 48, 0, 16, 448), Color.White);
    }

    static readonly Rectangle[] FrameParts = { new(0, 0, 640, 16), new(0, 464, 640, 16), new(0, 16, 16, 448), new(624, 16, 16, 448) };

    /// <summary>
    /// The mouse pointer from SYSTEM.BMP (FUN_00409db8): the arrow (y 0) or the hourglass (y 32), 32×32 cells from x 128
    /// drawn with their corner at the pointer; the four frames change every 2 game ticks.
    /// </summary>
    public void Cursor(SpriteBatch sb, Point pos, bool busy, int frame)
    {
        var sys = _tex.Get("SYSTEM.BMP");
        if (sys is null) return;
        int f = frame / 4 % 4;
        sb.Draw(sys, pos.ToVector2(), new Rectangle(128 + f * 32, busy ? 32 : 0, 32, 32), Color.White);
    }

    /// <summary>Panel textures: 64x64 tiles in SYSTEM.BMP's bottom row (verified for green).</summary>
    public enum PanelColor { Blue = 0, Red = 1, Yellow = 2, Green = 3 }

    /// <summary>
    /// Light/dark edge colours per panel colour (FUN_0041203c): swatches 2c and 2c + 1, in Blue/Red/Yellow/Green order
    /// (the RTP's blue light one is (248,216,192) on that row, the rows below it (240,248,248); recording r1's blue and red
    /// panels have edges of the same brightness). Green's pair is also the etched rules' (FUN_00414344: dark, then light).
    /// </summary>
    static (Color Light, Color Dark) Edges(PanelColor c) => (Swatch((int)c * 2), Swatch((int)c * 2 + 1));

    public static (Color Light, Color Dark) EdgeColors(PanelColor c) => Skin switch
    {
        UiSkin.Original => Edges(c),
        UiSkin.Gilded => (Gold * 0.75f, new Color(0, 0, 0) * 0.55f),
        _ => (new Color(255, 255, 255) * 0.22f, new Color(0, 0, 0) * 0.5f),
    };

    /// <summary>
    /// Textured panel (menus, choices, EXP messages, battle info): SYSTEM.BMP tile tiled from the
    /// panel's top-left, 1-px light edge top/left, dark bottom/right (colours per <see cref="Edges"/>).
    /// </summary>
    public void Panel(SpriteBatch sb, Rectangle r, PanelColor color = PanelColor.Green)
    {
        if (Modern) { ModernBox(sb, r, ModernTints[(int)color], 8, 0.9f); return; }
        if (Gilded) { GildedBox(sb, r, GildedTints[(int)color], 0.86f); return; }
        var sys = _tex.Get("SYSTEM.BMP", transparent: false);
        if (sys is null) { Fill(sb, r, new Color(0, 72, 0)); return; }
        int tx = (int)color * 64;
        for (int y = r.Y; y < r.Bottom; y += 64)
            for (int x = r.X; x < r.Right; x += 64)
            {
                int w = Math.Min(64, r.Right - x), h = Math.Min(64, r.Bottom - y);
                sb.Draw(sys, new Vector2(x, y), new Rectangle(tx, 224, w, h), Color.White);
            }
        var (light, dark) = Edges(color);
        Fill(sb, new Rectangle(r.X, r.Y, r.Width, 1), light);
        Fill(sb, new Rectangle(r.X, r.Y, 1, r.Height), light);
        Fill(sb, new Rectangle(r.X, r.Bottom - 1, r.Width, 1), dark);
        Fill(sb, new Rectangle(r.Right - 1, r.Y, 1, r.Height), dark);
    }

    /// <summary>Text on panels (swatch 8 in the original skin; the restyled skins' white).</summary>
    public static Color PanelText => Skin == UiSkin.Original ? Swatch(8) : new Color(248, 248, 248);
    /// <summary>A menu entry that can't be picked (FUN_00414344: SYSTEM.BMP swatch 9).</summary>
    public static Color DisabledText => Swatch(9);

    /// <summary>
    /// A menu row (FUN_00414344 / FUN_004144f4, colours from SYSTEM.BMP's swatches at y 288): the chosen row's bar —
    /// swatch 12 under swatch 13 text and swatch 10 under 11 (the RTP's light green under (0,32,0), white under
    /// (0,64,0)), swapping every 6 game ticks, or 14 under 15 (grey under (0,32,0)) when it can't be picked; other rows
    /// swatch 8, or 9 when they can't be picked. Draws the bar and returns the text colour; the modern skin uses its
    /// accent bar.
    /// </summary>
    public Color MenuRow(SpriteBatch sb, Rectangle bar, bool chosen, bool enabled, int frame)
    {
        if (Modern)
        {
            if (chosen) ModernHighlight(sb, bar, frame);
            return chosen ? (enabled ? Color.White : new Color(196, 204, 222)) : enabled ? PanelText : new Color(110, 116, 132);
        }
        if (Gilded)
        {
            if (chosen) GildedHighlight(sb, bar, frame);
            return chosen ? (enabled ? Color.White : new Color(196, 190, 172)) : enabled ? PanelText : new Color(122, 124, 138);
        }
        if (!chosen) return enabled ? PanelText : DisabledText;
        bool green = frame / 12 % 2 == 0;
        Fill(sb, bar, Swatch(!enabled ? 14 : green ? 12 : 10));
        return Swatch(!enabled ? 15 : green ? 13 : 11);
    }

    /// <summary>Stat numbers added on panels (item summaries, terrain): a soft yellow, readable on both skins.</summary>
    public static readonly Color StatText = new(248, 232, 152);

    public void Dispose()
    {
        Text.Dispose();
        SmallText.Dispose();
        NameText.Dispose();
        BoldText.Dispose();
        MenuFont?.Dispose();
        _pixel.Dispose();
        _windows.Dispose();
        _modern.Dispose();
        DisposeGilt();
    }
}
