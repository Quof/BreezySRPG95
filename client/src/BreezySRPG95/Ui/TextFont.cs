using BreezySRPG95.Config;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace BreezySRPG95.Ui;

/// <summary>
/// A string rendered to a texture, <see cref="Scale"/> texture pixels to a pixel of the 640 × 480 screen: 1 for the
/// original's bitmap text (scaled with the rest of the picture), the render scale for the restyled skins' smooth text, so
/// that it lands on the window's own pixels and stays crisp however large the window is. A smooth face is drawn
/// <see cref="Lift"/> px higher and laid out by <see cref="RefHeight"/>, so that every face sits where Yu Gothic UI —
/// the face the restyled skins were laid out with — would.
/// </summary>
public readonly record struct TextImage(Texture2D Texture, int Scale, float Lift = 0, float RefHeight = 0)
{
    /// <summary>Its size in 640 × 480 pixels.</summary>
    public int Width => (Texture.Width + Scale - 1) / Scale;
    public int Height => (Texture.Height + Scale - 1) / Scale;
    /// <summary>The height to centre it by: the reference face's line for smooth text, its own for the bitmap fonts.</summary>
    public float LayoutHeight => RefHeight > 0 ? RefHeight : Texture.Height / (float)Scale;

    /// <summary>Draws it with its corner at <paramref name="pos"/> (640 × 480 pixels, under the frame's scale transform).</summary>
    public void Draw(SpriteBatch sb, Vector2 pos, Color c)
    {
        if (Scale == 1 && Lift == 0) { sb.Draw(Texture, pos, c); return; }
        // on whole render pixels, so each texel lands on exactly one
        var p = new Vector2(MathF.Round(pos.X * Scale) / Scale, MathF.Round((pos.Y - Lift) * Scale) / Scale);
        sb.Draw(Texture, p, null, c, 0, Vector2.Zero, 1f / Scale, SpriteEffects.None, 0);
    }
}

/// <summary>
/// One of the interface's text styles (message text, the small menu text, names, the status window's bold class):
/// strings rendered to textures and measured, in the original skin's face or the restyled skins' chosen font.
/// </summary>
public interface ITextFont : IDisposable
{
    /// <summary>The face the original skin's text is in (MS Gothic through GDI, IPAGothic through FreeType).</summary>
    string FaceName { get; }
    /// <summary>The style's pixel height, as the original asks for it (18 for messages, 12 for menus...).</summary>
    int Height { get; }
    /// <summary>A string's width in 640 × 480 pixels.</summary>
    int Measure(string s);
    /// <summary>White text on transparent background (null for an empty string).</summary>
    TextImage? Get(string s);
}

/// <summary>
/// Which text renderer draws the interface's text: Windows GDI on Windows (<see cref="GdiText"/>, exactly the original's
/// MS Gothic and the Windows fonts), FreeType everywhere else (<see cref="FreeTypeText"/>: the fonts inside the executable,
/// IPAGothic standing in for MS Gothic). BREEZY_TEXT=freetype makes Windows use FreeType too, to compare the two (GDI
/// still, should FreeType not load).
/// </summary>
public static class TextFonts
{
    static bool? _useFreeType;

    public static bool UseFreeType => _useFreeType ??= Decide();

    static bool Decide()
    {
        if (!OperatingSystem.IsWindows()) return true;
        if (!string.Equals(Environment.GetEnvironmentVariable("BREEZY_TEXT"), "freetype", StringComparison.OrdinalIgnoreCase)) return false;
        try { FreeTypeFonts.Init(); return true; }
        catch (Exception) { return false; }
    }

    /// <summary>A text style: <paramref name="height"/> px at <paramref name="weight"/>; <paramref name="envOverride"/> names its calibration variable ("height,weight").</summary>
    public static ITextFont Create(GraphicsDevice device, int height = 18, int weight = 700, string envOverride = "BREEZY_FONT") =>
        UseFreeType ? new FreeTypeText(device, height, weight, envOverride) : new GdiText(device, height, weight, envOverride);

    /// <summary>Whether a Font option's faces are there, for the options to say so.</summary>
    public static bool Available(UiFont f) => UseFreeType ? FreeTypeText.Available(f) : GdiText.Available(f);
}
