using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Srpg95.Data;

namespace BreezySRPG95.Runtime;

/// <summary>
/// What BreezySRPG95 keeps about a save beyond SAVEn.DAT, none of which SRPG Maker 95 has: the play time, a picture of
/// the screen when it was saved, and for a save made in the middle of an event the rest of that event (<see
/// cref="SceneSave"/>). They live in a "BreezySRPG95" folder beside the saves — SAVEn.png and saves.json — so the save
/// files stay exactly the original's. A note counts only while its save file is the one it was written with (the
/// file's hash): a slot the original game saved over since shows none.
/// A save's note is written after the save, off the game thread (<see cref="WriteLater"/>), one at a time in order;
/// whatever reads the notes waits for those still being written. The files are replaced whole (written beside, then
/// moved over), so nothing reads one half written.
/// </summary>
public static class SaveExtras
{
    /// <summary>A save's note: its play time (null when it isn't known), the hash of the file it belongs to, and the event it was saved in.</summary>
    public sealed record Note(double? PlaySeconds, string Hash, SceneSave? Scene = null);

    /// <summary>The notes' files are read and written by one thread at a time.</summary>
    static readonly object Files = new();

    /// <summary>The notes' folder beside the saves.</summary>
    public static string Folder(GameFolder g) => Path.Combine(SaveFile.Directory ?? g.Root, "BreezySRPG95");
    public static string PicturePath(GameFolder g, int slot) => Path.Combine(Folder(g), $"SAVE{slot}.png");
    public static string IndexPath(GameFolder g) => Path.Combine(Folder(g), "saves.json");

    /// <summary>Indented, the messages of a saved event kept readable (not \u escapes), nothing written for what isn't there.</summary>
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Hash(byte[] save) => Convert.ToHexString(SHA1.HashData(save))[..20];

    /// <summary>Every note, by slot (none when the folder or its index isn't there or can't be read), once the notes being written are.</summary>
    public static Dictionary<int, Note> Read(GameFolder g)
    {
        Flush();
        lock (Files) return ReadNow(g);
    }

    static Dictionary<int, Note> ReadNow(GameFolder g)
    {
        try
        {
            if (!File.Exists(IndexPath(g))) return new();
            var raw = JsonSerializer.Deserialize<Dictionary<string, Note>>(File.ReadAllText(IndexPath(g)), Json) ?? new();
            return raw.Where(kv => int.TryParse(kv.Key, out _)).ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
        }
        catch (Exception) { return new(); }
    }

    /// <summary>The note for the save now in <paramref name="slot"/>, if it still belongs to it.</summary>
    public static Note? For(Dictionary<int, Note> notes, int slot, byte[] save) =>
        notes.TryGetValue(slot, out var n) && n.Hash == Hash(save) ? n : null;

    /// <summary>The note of whichever slot holds exactly this save (a loaded save's play time, wherever it came from).</summary>
    public static Note? Find(GameFolder g, byte[] save)
    {
        string h = Hash(save);
        return Read(g).Values.FirstOrDefault(n => n.Hash == h);
    }

    /// <summary>Notes the save just written to <paramref name="slot"/>: its play time, its picture (a PNG, or none) and the event it was made in.</summary>
    public static void Write(GameFolder g, int slot, byte[] save, double? playSeconds, byte[]? png, SceneSave? scene = null)
    {
        Flush();
        WriteNow(g, slot, save, playSeconds, png, scene);
    }

    static void WriteNow(GameFolder g, int slot, byte[] save, double? playSeconds, byte[]? png, SceneSave? scene)
    {
        lock (Files)
        {
            var notes = ReadNow(g);
            notes[slot] = new Note(playSeconds, Hash(save), scene);
            Directory.CreateDirectory(Folder(g));
            Replace(IndexPath(g), Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(notes.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value), Json)));
            if (png is not null) Replace(PicturePath(g, slot), png);
            else if (File.Exists(PicturePath(g, slot))) File.Delete(PicturePath(g, slot));
        }
    }

    /// <summary>A file written whole: beside it first, then moved over it.</summary>
    static void Replace(string path, byte[] bytes)
    {
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    // ---------------- off the game thread ----------------

    static readonly object Queue = new();
    static Task _queue = Task.CompletedTask;

    /// <summary>Why notes written in the background couldn't be, for the game to report (the save itself stands without them).</summary>
    public static readonly ConcurrentQueue<string> Failures = new();

    /// <summary>
    /// <see cref="Write"/> in the background, after the notes before it, the picture made into a PNG there too;
    /// <paramref name="then"/> runs after it, written or not (outside the queue: it may wait for it).
    /// </summary>
    public static void WriteLater(GameFolder g, int slot, byte[] save, double? playSeconds, Png.Image? picture, SceneSave? scene = null, Action? then = null)
    {
        Task job;
        lock (Queue)
            job = _queue = _queue.ContinueWith(_ =>
            {
                try { WriteNow(g, slot, save, playSeconds, picture is null ? null : Png.Encode(picture), scene); }
                catch (Exception e) { Failures.Enqueue($"no note for save {slot} ({e.Message})"); }
            }, TaskScheduler.Default);
        if (then is not null) job.ContinueWith(_ => then(), TaskScheduler.Default);
    }

    /// <summary>Waits for the notes still being written (at most <paramref name="timeoutMs"/>).</summary>
    public static void Flush(int timeoutMs = Timeout.Infinite)
    {
        Task pending;
        lock (Queue) pending = _queue;
        pending.Wait(timeoutMs);
    }
}
