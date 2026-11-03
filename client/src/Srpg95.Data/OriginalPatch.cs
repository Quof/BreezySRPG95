using System.Text;

namespace Srpg95.Data;

/// <summary>
/// A translation (<see cref="Translation"/>) written into copies of the game's own files, for SRPG Maker 95 itself to
/// play (modern, BreezySRPG95's own): the names and words into UNIT / CLASS / ITEM / MAGIC / GEOLOGY / WORD.DAT, the
/// messages and character choices into each map's event scripts (SMAP_nnn.DAT), the translation's pictures beside them.
/// The original has none of BreezySRPG95's fitting, so the text is fitted to it here: Shift-JIS (what can't be written
/// becomes ?), each field's size, message lines of 40 bytes (the editor's limit), five lines a window — a longer message
/// goes on in more messages, inserted after it. What had to be cut is noted.
/// </summary>
public static class OriginalPatch
{
    /// <summary>A message line's bytes (the editor's limit, which no shipped line passes) and a window's lines (FUN_0040d18c).</summary>
    public const int LineBytes = 40, WindowLines = TreeBuilder.MaxMessageLines;
    /// <summary>A character choice's option, at most (the longest the games have is 24 bytes; the original's limit isn't known).</summary>
    public const int OptionBytes = 40;
    const int FileCount = 5000, BlockSize = 100, ObjectBlock = 100 * 164;

    /// <summary>The number of files written, and what didn't fit as it was.</summary>
    public sealed record Result(int Files, List<string> Notes);

    /// <param name="speaker">whether a message's first line is its speaker's name (repeated on each window it's split into)</param>
    public static Result Build(GameFolder g, Translation t, string outDir, Func<IReadOnlyList<string>, bool> speaker, Action<double>? progress = null)
    {
        var notes = new List<string>();
        int files = 0;
        Directory.CreateDirectory(outDir);
        void Save(string relative, byte[] data)
        {
            string path = Path.Combine(outDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
            files++;
        }

        // ---- the names and words: the translation put into a database of the game's, written where it changed ----
        var game = Database.Load(g);
        var tr = Database.Load(g);
        t.Apply(tr, true);
        void Table<T>(string file, int size, T[] before, T[] after, Action<byte[], int, T, T> write)
        {
            var d = g.ReadData(file);
            var copy = (byte[])d.Clone();
            for (int i = 0; i < before.Length; i++) write(copy, i * size, before[i]!, after[i]!);
            if (!copy.AsSpan().SequenceEqual(d)) Save(file, copy);
        }
        void Field(byte[] d, int at, int size, string before, string after, string what)
        {
            if (!Unchanged(Lines(before), Lines(after))) Put(d, at, size, after, what, notes);
        }
        Table("UNIT.DAT", 480, game.Units, tr.Units, (d, o, a, b) =>
        {
            Field(d, o, 32, a.Name, b.Name, $"UNIT {a.Index + 1} NAME");
            if (!Unchanged(Lines(a.DeathMessage), Lines(b.DeathMessage)))
                Put(d, o + 0x6c, 0x1e0 - 0x6c, string.Join("\r\n", Fit(b.DeathMessage.Replace("\r\n", "\n").Split('\n'), false, $"UNIT {a.Index + 1} DEATH", notes)[0]),
                    $"UNIT {a.Index + 1} DEATH", notes);
        });
        Table("CLASS.DAT", 640, game.Classes, tr.Classes, (d, o, a, b) => Field(d, o, 32, a.Name, b.Name, $"CLASS {a.Index + 1} NAME"));
        Table("ITEM.DAT", 640, game.Items, tr.Items, (d, o, a, b) =>
        {
            Field(d, o, 32, a.Name, b.Name, $"ITEM {a.Index + 1} NAME");
            Field(d, o + 0x28, 0x50, a.Desc, b.Desc, $"ITEM {a.Index + 1} DESC");
        });
        Table("MAGIC.DAT", 480, game.Magic, tr.Magic, (d, o, a, b) =>
        {
            Field(d, o, 32, a.Name, b.Name, $"MAGIC {a.Index + 1} NAME");
            Field(d, o + 0x28, 0x50, a.Desc, b.Desc, $"MAGIC {a.Index + 1} DESC");
        });
        Table("GEOLOGY.DAT", 60, game.Terrains, tr.Terrains, (d, o, a, b) => Field(d, o, 32, a.Name, b.Name, $"TERRAIN {a.Index + 1} NAME"));
        Table("WORD.DAT", 32, game.Words, tr.Words.Select((w, i) => Piece(i, game.Words[i], w)).ToArray(),
            (d, o, a, b) => Field(d, o, 32, a, b, $"WORD {o / 32}"));
        progress?.Invoke(0.05);

        // ---- the maps: the scripts with translated texts rewritten, the rest of each file as it was ----
        var maps = TextDump.MapFiles(g).ToList();
        for (int k = 0; k < maps.Count; k++)
        {
            var path = maps[k];
            var map = MapData.Load(path);
            var changed = new Dictionary<string, List<Command>>();
            foreach (var s in map.Scripts.Values)
                if (Rewrite(s, t, speaker, notes) is { } cmds) changed[s.Name] = cmds;
            if (changed.Count > 0) Save(Path.Combine("MAP", Path.GetFileName(path)), WriteMap(File.ReadAllBytes(path), changed));
            progress?.Invoke(0.05 + 0.9 * (k + 1) / maps.Count);
        }

        // ---- the translation's pictures ----
        string pictures = Path.Combine(t.Folder, "BMP");
        if (Directory.Exists(pictures))
            foreach (var f in Directory.GetFiles(pictures)) Save(Path.Combine("BMP", Path.GetFileName(f)), File.ReadAllBytes(f));
        progress?.Invoke(1);
        return new Result(files, notes);
    }

    static string[] Lines(string s) => s.Replace("\r\n", "\n").Split('\n');

    /// <summary>
    /// A "translation" that is the game's own words (a file copied from translation/jp and not yet translated, give or
    /// take the spaces at the ends of lines): the game's are left as they are, and a map with nothing else isn't in the patch.
    /// </summary>
    static bool Unchanged(IEnumerable<string> game, IEnumerable<string> translated) => TextFile.Trim(game).SequenceEqual(TextFile.Trim(translated));

    /// <summary>
    /// WORD.DAT's sentence pieces (the ones put after a name or a number: 60–62, 64–73) as the original joins them, with
    /// nothing between: a translated one starts with a space (an empty one is a space), so 「ＨＰ」+「」+「５」+「pts」 reads
    /// "ＨＰ ５ pts". BreezySRPG95 puts the spaces in itself (Lang.Phrase).
    /// </summary>
    static string Piece(int word, string game, string translated)
    {
        if (Unchanged(new[] { game }, new[] { translated }) || word is not (>= 60 and <= 62 or >= 64 and <= 73)) return translated;
        return translated.Length == 0 ? " " : char.IsLetterOrDigit(translated[0]) ? " " + translated : translated;
    }

    // ---------------- texts to the original's sizes ----------------

    static readonly Encoding Sjis = Bin.Sjis;

    /// <summary>
    /// A string into a fixed-size, NUL-terminated field: Shift-JIS, cut (between characters) to leave room for the NUL,
    /// the rest of the field cleared. Characters Shift-JIS hasn't got become ?, and that's noted, as is a cut.
    /// </summary>
    static void Put(byte[] d, int at, int size, string text, string what, List<string> notes)
    {
        var bytes = Encode(text, size - 1, what, notes);
        d.AsSpan(at, size).Clear();
        bytes.CopyTo(d, at);
    }

    /// <summary>A string as Shift-JIS, at most <paramref name="max"/> bytes (cut between characters), with what was lost noted.</summary>
    static byte[] Encode(string text, int max, string what, List<string> notes)
    {
        var bytes = Sjis.GetBytes(text);
        if (Sjis.GetString(bytes) != text) notes.Add($"{what}: characters Shift-JIS hasn't got became ? in \"{text}\"");
        if (bytes.Length <= max) return bytes;
        int n = 0;
        foreach (var r in text)
        {
            int c = Sjis.GetByteCount(r.ToString());
            if (n + c > max) break;
            n += c;
        }
        notes.Add($"{what}: cut to {max} bytes: \"{text}\"");
        return bytes[..n];
    }

    static int Bytes(string s) => Sjis.GetByteCount(s);

    /// <summary>
    /// A message's lines in the original's windows: wrapped between words (anywhere in text without spaces) to
    /// <see cref="LineBytes"/>, five lines a window, a speaker's line (<paramref name="hasSpeaker"/>) heading each.
    /// </summary>
    public static List<List<string>> Fit(IReadOnlyList<string> lines, bool hasSpeaker, string what, List<string> notes)
    {
        var head = hasSpeaker && lines.Count > 0 ? new List<string> { Cut(lines[0], what, notes) } : new List<string>();
        var text = new List<string>();
        foreach (var line in lines.Skip(head.Count))
        {
            string rest = line.TrimEnd();
            if (rest.Length == 0) { text.Add(""); continue; }
            while (rest.Length > 0)
            {
                if (Bytes(rest) <= LineBytes) { text.Add(rest); break; }
                int fit = 1;
                while (fit < rest.Length && Bytes(rest[..(fit + 1)]) <= LineBytes) fit++;
                int space = rest.LastIndexOf(' ', fit);
                int cut = space > 0 ? space : fit;
                text.Add(rest[..cut].TrimEnd());
                rest = rest[cut..].TrimStart();
            }
        }
        while (text.Count > 0 && text[^1].Length == 0) text.RemoveAt(text.Count - 1);
        int per = WindowLines - head.Count;
        if (text.Count <= per) return new() { head.Concat(text).ToList() };
        return text.Chunk(per).Select(c => head.Concat(c).ToList()).ToList();
    }

    static string Cut(string line, string what, List<string> notes) =>
        Bytes(line) <= LineBytes ? line : Sjis.GetString(Encode(line, LineBytes, what, notes));

    // ---------------- the scripts ----------------

    /// <summary>
    /// A script's commands with its translated messages and choices put in (null when it has none): a message (op 01
    /// and the 0xC9 lines after it) becomes as many messages as its windows need, at the same depth and window; a
    /// choice gets its options. Done from the last to the first, so each command's number is still the game's.
    /// </summary>
    static List<Command>? Rewrite(EventScript s, Translation t, Func<IReadOnlyList<string>, bool> speaker, List<string> notes)
    {
        if (s.Error is not null) return null;
        var edits = new List<(int At, List<Command> Commands, int Length)>();
        var cmds = s.Commands;
        var stack = new Stack<IEnumerator<ScriptNode>>();
        stack.Push(s.Tree.GetEnumerator());
        while (stack.Count > 0)
        {
            if (!stack.Peek().MoveNext()) { stack.Pop(); continue; }
            switch (stack.Peek().Current)
            {
                case MessageNode m when m.At >= 0 && t.Message(s, m) is { } lines && !Unchanged(m.Lines, lines):
                {
                    int end = m.At + 1;
                    while (end < cmds.Count && cmds[end].Op == Op.MessageLine) end++;
                    string what = $"{s.Map} {Translation.MessageKey(s, m)}";
                    var windows = Fit(lines, speaker(lines), what, notes);
                    var c = cmds[m.At];
                    edits.Add((m.At, windows.SelectMany(w => w.Select((l, i) => Text(i == 0 ? Op.Message : Op.MessageLine, c.Depth, m.Window, l, what, notes))).ToList(), end - m.At));
                    break;
                }
                case BranchNode b:
                    stack.Push(b.Then.GetEnumerator());
                    break;
                case ChoiceNode ch:
                    if (ch.At >= 0 && t.Choice(s, ch) is { } options && !Unchanged(ch.Options, options))
                    {
                        string what = $"{s.Map} {Translation.ChoiceKey(s, ch)}";
                        var p = new List<byte> { (byte)options.Length, (byte)(options.Length >> 8) };
                        foreach (var o in options) { p.AddRange(Encode(o, OptionBytes, what, notes)); p.Add(0); }
                        var c = cmds[ch.At];
                        edits.Add((ch.At, new() { new Command(Op.CharacterChoice, c.Depth, p.ToArray()) }, 1));
                    }
                    for (int k = ch.Cases.Count - 1; k >= 0; k--) stack.Push(ch.Cases[k].GetEnumerator());
                    break;
            }
        }
        if (edits.Count == 0) return null;
        var result = cmds.ToList();
        foreach (var (at, commands, length) in edits.OrderByDescending(e => e.At))
        {
            result.RemoveRange(at, length);
            result.InsertRange(at, commands);
        }
        return result;
    }

    /// <summary>A message (01) or message line (C9): u16 window, the text in Shift-JIS, a NUL.</summary>
    static Command Text(Op op, int depth, int window, string line, string what, List<string> notes)
    {
        var bytes = Encode(line, LineBytes, what, notes);
        var p = new byte[2 + bytes.Length + 1];
        p[0] = (byte)window;
        p[1] = (byte)(window >> 8);
        bytes.CopyTo(p, 2);
        return new Command(op, depth, p);
    }

    static byte[] Encode(IEnumerable<Command> cmds)
    {
        var b = new List<byte>();
        foreach (var c in cmds)
        {
            b.Add((byte)c.Op); b.Add((byte)c.Payload.Length); b.Add((byte)(c.Payload.Length >> 8)); b.Add((byte)c.Depth);
            b.AddRange(c.Payload);
        }
        return b.ToArray();
    }

    // ---------------- the map file ----------------

    /// <summary>
    /// A map file with some scripts' commands replaced (<paramref name="changed"/>, by name). The script container is
    /// rewritten as the editor writes it — FAT, directory, the blocks in use packed in block order (MapData.ReadScripts) —
    /// with every other file's blocks where they were: only the changed files' chains are freed and laid out again, in
    /// the lowest free blocks. Each changed file keeps its comment and whatever followed its command stream.
    /// </summary>
    public static byte[] WriteMap(byte[] d, IReadOnlyDictionary<string, List<Command>> changed)
    {
        int fat = MapData.ObjectsOffset(d) + ObjectBlock, dir = fat + FileCount * 4, data = dir + FileCount * 16;
        var next = new int[FileCount];
        var blocks = new byte[FileCount][];
        for (int k = 0, o = data; k < FileCount; k++)
        {
            next[k] = Bin.I32(d, fat + k * 4);
            if (next[k] == -1) continue;
            blocks[k] = new byte[BlockSize];
            if (o < d.Length) d.AsSpan(o, Math.Min(BlockSize, d.Length - o)).CopyTo(blocks[k]);
            o += BlockSize;
        }
        var head = d[..data];
        for (int i = 0; i < FileCount; i++)
        {
            int e = dir + i * 16;
            if (!head.AsSpan(e, 16).ContainsAnyExcept((byte)0)) continue;
            string name = Bin.Str(head, e, 8);
            if (!changed.TryGetValue(name, out var cmds)) continue;

            // the file as it was: its chain, cut to its size
            int first = Bin.I32(head, e + 8), size = Bin.I32(head, e + 12);
            var old = new byte[Math.Max(0, size)];
            var chain = new List<int>();
            for (int k = first, o = 0; k is >= 0 and < FileCount && o < old.Length; k = next[k], o += BlockSize)
            {
                chain.Add(k);
                blocks[k]?.AsSpan(0, Math.Min(BlockSize, old.Length - o)).CopyTo(old.AsSpan(o));
            }
            if (old.Length < 104) continue;
            int n = Math.Clamp(Bin.I32(old, 100), 0, old.Length - 104);
            // the stream's end after its commands (the closing NUL) and the file's after the stream stay as they were
            var stream = old.AsSpan(104, n);
            int commands = Encode(EventScript.Parse(name, old).Commands).Length;
            var file = new List<byte>(old[..100]);
            var body = Encode(cmds).Concat(stream[Math.Min(commands, n)..].ToArray()).ToArray();
            file.AddRange(BitConverter.GetBytes(body.Length));
            file.AddRange(body);
            file.AddRange(old[(104 + n)..]);

            // its old blocks freed, the new ones taken from the lowest free
            foreach (var k in chain) { next[k] = -1; blocks[k] = null!; }
            int need = (file.Count + BlockSize - 1) / BlockSize, prev = -1, start = -1;
            for (int k = 0, got = 0; got < need; k++)
            {
                if (k >= FileCount) throw new InvalidDataException($"{name}: the map's 5000 blocks are full");
                if (next[k] != -1) continue;
                var block = new byte[BlockSize];
                file.Skip(got * BlockSize).Take(BlockSize).ToArray().CopyTo(block, 0);
                blocks[k] = block;
                next[k] = -2;
                if (prev >= 0) next[prev] = k; else start = k;
                prev = k;
                got++;
            }
            BitConverter.GetBytes(start).CopyTo(head, e + 8);
            BitConverter.GetBytes(file.Count).CopyTo(head, e + 12);
        }
        for (int k = 0; k < FileCount; k++) BitConverter.GetBytes(next[k]).CopyTo(head, fat + k * 4);
        var result = new List<byte>(head);
        for (int k = 0; k < FileCount; k++) if (next[k] != -1) result.AddRange(blocks[k]);
        return result.ToArray();
    }
}
