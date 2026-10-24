using System.Collections.Concurrent;

namespace Srpg95.Data;

/// <summary>
/// A game folder (the one containing SRPGEXEC.EXE and the .DAT files), with an optional
/// RTP folder as a fallback for graphics, music and sounds the game doesn't ship itself.
/// File lookup is case-insensitive because the original files mix cases (Swname.dat, ...). Off Windows it also ignores
/// the names' Unicode form (<see cref="FileNames.Key"/>): macOS can store a Japanese name decomposed.
/// </summary>
public sealed class GameFolder
{
    public string Root { get; }
    public string? RtpRoot { get; }
    readonly Dictionary<string, Dictionary<string, string>> _dirs = new(StringComparer.OrdinalIgnoreCase);
    readonly bool _normalize;

    /// <param name="normalizeNames">compare names in Unicode normal form C (default: off Windows, <see cref="FileNames.MatchByHand"/>)</param>
    public GameFolder(string root, string? rtpRoot = null, bool? normalizeNames = null)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        Root = root;
        RtpRoot = rtpRoot is not null && Directory.Exists(rtpRoot) ? rtpRoot : null;
        _normalize = normalizeNames ?? FileNames.MatchByHand;
    }

    /// <summary>A file name as the listings compare it.</summary>
    string Key(string name) => _normalize ? FileNames.Key(name) : name;

    Dictionary<string, string> Listing(string dir)
    {
        if (!_dirs.TryGetValue(dir, out var map))
        {
            map = new(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFileSystemEntries(dir))
                    map.TryAdd(Key(Path.GetFileName(f)), f);
            _dirs[dir] = map;
        }
        return map;
    }

    string? FindIn(string root, string relative)
    {
        string dir = root;
        var parts = relative.Split('/', '\\');
        for (int i = 0; i < parts.Length; i++)
        {
            if (!Listing(dir).TryGetValue(Key(parts[i]), out var next)) return null;
            dir = next;
        }
        return dir;
    }

    /// <summary>A folder (BGM, EFS…) in the game folder and in the RTP folder, those that exist, the game's first.</summary>
    public List<string> Folders(string name) =>
        new[] { FindIn(Root, name), RtpRoot is null ? null : FindIn(RtpRoot, name) }.OfType<string>().Where(Directory.Exists).ToList();

    /// <summary>Path of a file in the game folder, else the RTP folder, else null.</summary>
    public string? Find(string relative) => FindIn(Root, relative) ?? (RtpRoot is null ? null : FindIn(RtpRoot, relative));

    /// <summary>Reads a data file from the game folder itself (data never falls back to the RTP).</summary>
    public byte[] ReadData(string relative) =>
        File.ReadAllBytes(FindIn(Root, relative) ?? throw new FileNotFoundException($"missing game file {relative}", Path.Combine(Root, relative)));

    public IndexedBitmap? Bitmap(string fileName) => Find("BMP/" + fileName) is { } p ? IndexedBitmap.Decode(Read(p), p) : null;

    /// <summary>Files read ahead (<see cref="ReadAhead"/>), by path, kept: a picture is made again at other settings.</summary>
    readonly ConcurrentDictionary<string, byte[]> _readAhead = new(StringComparer.OrdinalIgnoreCase);

    byte[] Read(string path) => _readAhead.TryGetValue(path, out var b) ? b : File.ReadAllBytes(path);

    /// <summary>
    /// Reads a folder's files (BMP) into memory in the background: the game's, then the RTP's the game doesn't replace
    /// (22–190 MB for the games here, plus up to 125 MB of the RTP's). Pictures are read the first time they're shown, and
    /// on a slow or sleeping drive (a USB disk) that kept the game waiting; read ahead, only their making is left.
    /// </summary>
    public void ReadAhead(string name)
    {
        string? game = FindIn(Root, name), rtp = RtpRoot is null ? null : FindIn(RtpRoot, name);   // here: _dirs isn't thread-safe
        Task.Run(() =>
        {
            var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in new[] { game, rtp })
            {
                if (dir is null || !Directory.Exists(dir)) continue;
                try
                {
                    foreach (var path in Directory.EnumerateFiles(dir))
                    {
                        string file = Key(Path.GetFileName(path));
                        if (dir == rtp && own.Contains(file)) continue;   // the game's own is the one shown
                        if (dir == game) own.Add(file);
                        try { _readAhead.TryAdd(path, File.ReadAllBytes(path)); } catch (Exception) { }   // read when it's shown instead
                    }
                }
                catch (Exception) { }
            }
        });
    }

    public string MapPath(int mapIndex) => FindIn(Root, $"MAP/SMAP_{mapIndex + 1:000}.DAT")
        ?? throw new FileNotFoundException($"map {mapIndex} (SMAP_{mapIndex + 1:000}.DAT) not found");

    public byte[]? Chipset(int tileset) => FindIn(Root, $"MAP/MAPC_{tileset:000}.DAT") is { } p ? File.ReadAllBytes(p) : null;
}
