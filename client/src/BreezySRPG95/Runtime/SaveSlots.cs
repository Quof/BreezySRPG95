using System.Globalization;
using Srpg95.Data;

namespace BreezySRPG95.Runtime;

/// <summary>
/// What the save / load screen shows of SAVE1–100.DAT, kept between visits so that opening it doesn't read every save
/// again: 100 files of 114 KB to read and hash took 30 ms with the disk's cache warm, and half a second or more without.
/// The files are found with one listing of the folder, and a file is read again only when its time or size has changed
/// since, or the game has saved to it (<see cref="Changed"/>: every save is 114,400 bytes, and a FAT32 drive keeps the
/// time to 2 seconds). The notes (<see cref="SaveExtras"/>, a millisecond to read) are read each time and matched to the
/// files by their hash. The game looks in the background when it starts and after each save, so the screen opens on
/// what's known, and looks again then — the listing alone, unless something changed — which also catches a file another
/// program wrote meanwhile.
/// </summary>
public sealed class SaveSlots(GameFolder game)
{
    public const int Count = 100;

    /// <summary>
    /// A file's details: its map (1-based), camp or turn, the play time, when it was written, whether it has a picture, and
    /// whether it was saved in the middle of an event.
    /// </summary>
    public sealed record Info(int Map, bool Camp, int Turn, double? Play, DateTime Saved, bool Picture, bool Scene);

    /// <summary>A save file as it was last read: its time and size then, its hash and what the list shows of it.</summary>
    sealed record Seen(DateTime Time, long Length, string Hash, int Map, bool Camp, int Turn);

    readonly object _gate = new();
    readonly Seen?[] _seen = new Seen?[Count];
    /// <summary>Slots the game has saved to since they were last read (1): read again whatever their time says.</summary>
    readonly int[] _changed = new int[Count];
    Task _background = Task.CompletedTask;

    /// <summary>Looks at the files in the background, after any look already going on.</summary>
    public void RefreshLater()
    {
        lock (_gate) _background = _background.ContinueWith(_ => Refresh(), TaskScheduler.Default);
    }

    /// <summary>The game has just written <paramref name="slot"/> (1–100): it's read again next time.</summary>
    public void Changed(int slot)
    {
        if (slot is >= 1 and <= Count) Volatile.Write(ref _changed[slot - 1], 1);
    }

    /// <summary>Every file's details as they are on the disk now (null: no file, or not a save), by slot from 0.</summary>
    public Info?[] Current()
    {
        Task pending;
        lock (_gate) pending = _background;
        pending.Wait();
        return Refresh();
    }

    /// <summary>File n's slot number from its name, SAVE{n}.DAT in any case; 0 for another file.</summary>
    static int Slot(string name)
    {
        if (name.Length < 9 || !name.StartsWith("SAVE", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".DAT", StringComparison.OrdinalIgnoreCase))
            return 0;
        string digits = name[4..^4];
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is >= 1 and <= Count
            && digits == n.ToString(CultureInfo.InvariantCulture) ? n : 0;
    }

    Info?[] Refresh()
    {
        var infos = new Info?[Count];
        try
        {
            var notes = SaveExtras.Read(game);   // after any note still being written
            lock (_seen)
            {
                var files = new FileInfo?[Count];
                foreach (var f in List(SaveFile.Directory ?? game.Root, "SAVE*.DAT"))
                    if (Slot(f.Name) is var n and > 0 && (files[n - 1] is null || f.Name == $"SAVE{n}.DAT")) files[n - 1] = f;   // as SaveFile.PathFor
                var pictures = new HashSet<int>(List(SaveExtras.Folder(game), "SAVE*.png")
                    .Select(f => Slot(Path.ChangeExtension(f.Name, ".DAT"))).Where(n => n > 0));
                for (int i = 0; i < Count; i++)
                {
                    if (files[i] is not { } f) { _seen[i] = null; continue; }
                    var seen = _seen[i];
                    if (Interlocked.Exchange(ref _changed[i], 0) == 1 || seen is null || seen.Time != f.LastWriteTimeUtc || seen.Length != f.Length)
                        seen = _seen[i] = Read(f);
                    if (seen is null) continue;
                    var note = notes.TryGetValue(i + 1, out var x) && x.Hash == seen.Hash ? x : null;
                    infos[i] = new Info(seen.Map, seen.Camp, seen.Turn, note?.PlaySeconds, f.LastWriteTime,
                        note is not null && pictures.Contains(i + 1), note?.Scene is not null && !seen.Camp);
                }
            }
        }
        catch (Exception) { }   // a folder that can't be read: what was found so far
        return infos;
    }

    /// <summary>A folder's files matching a pattern, in any case; none if it can't be listed.</summary>
    static List<FileInfo> List(string dir, string pattern)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles(pattern, FileNames.AnyCase).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(); }
    }

    /// <summary>A save file read for the list, or null when it can't be read or isn't one.</summary>
    static Seen? Read(FileInfo f) => SaveFile.Load(f.FullName) is { } save
        ? new Seen(f.LastWriteTimeUtc, f.Length, SaveExtras.Hash(save.Raw), save.MapIndex + 1, save.AtCamp, (save.Phase + 1) / 2)
        : null;
}
