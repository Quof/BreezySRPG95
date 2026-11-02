using Microsoft.Xna.Framework.Graphics;
using Srpg95.Data;

namespace BreezySRPG95;

/// <summary>Loads original BMPs (game folder, then RTP) into textures, cached by file name.</summary>
public sealed class Textures(GraphicsDevice device, GameFolder game) : IDisposable
{
    readonly Dictionary<(string, bool), Texture2D?> _cache = new();
    /// <summary>A bitmap the game asked for isn't there (told once per file; the original stops with an error box).</summary>
    public Action<string>? Missing { get; set; }

    /// <param name="transparent">Treat palette index 0 as transparent (sprite sheets).</param>
    public Texture2D? Get(string fileName, bool transparent = true)
    {
        if (_cache.TryGetValue((fileName, transparent), out var t)) return t;
        var bmp = game.Bitmap(fileName);
        if (bmp is not null)
        {
            t = new Texture2D(device, bmp.Width, bmp.Height, false, SurfaceFormat.Color);
            t.SetData(bmp.ToRgba(transparent));
        }
        else if (!_cache.Keys.Any(k => k.Item1 == fileName)) Missing?.Invoke(fileName);
        _cache[(fileName, transparent)] = t;
        return t;
    }

    /// <summary>A bitmap's palette indices and colours, as the game has it (null if it isn't there).</summary>
    public IndexedBitmap? Bitmap(string fileName) => game.Bitmap(fileName);

    readonly Dictionary<string, Texture2D?> _silhouettes = new();

    /// <summary>The sprite sheet with every opaque pixel white (the battle scene's hit flash, FUN_00419574).</summary>
    public Texture2D? Silhouette(string fileName)
    {
        if (_silhouettes.TryGetValue(fileName, out var t)) return t;
        var bmp = game.Bitmap(fileName);
        if (bmp is not null)
        {
            var px = bmp.ToRgba(true);
            for (int i = 0; i + 3 < px.Length; i += 4)   // RGBA bytes, premultiplied
                if (px[i + 3] != 0) px[i] = px[i + 1] = px[i + 2] = px[i + 3] = 255;
            t = new Texture2D(device, bmp.Width, bmp.Height, false, SurfaceFormat.Color);
            t.SetData(px);
        }
        _silhouettes[fileName] = t;
        return t;
    }

    /// <summary>
    /// Pictures to be made again the next time they're asked for (a translation's pictures shown or taken away). The old
    /// textures aren't disposed: something drawn may still hold one.
    /// </summary>
    public void Forget(IEnumerable<string> fileNames)
    {
        var names = fileNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var k in _cache.Keys.Where(k => names.Contains(k.Item1)).ToList()) _cache.Remove(k);
        foreach (var k in _silhouettes.Keys.Where(names.Contains).ToList()) _silhouettes.Remove(k);
    }

    public void Dispose()
    {
        foreach (var t in _cache.Values) t?.Dispose();
        foreach (var t in _silhouettes.Values) t?.Dispose();
        _cache.Clear();
        _silhouettes.Clear();
    }
}
