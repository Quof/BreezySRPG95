using System.Text;

namespace Srpg95.Data;

/// <summary>
/// One text of a translation file: where it is (<see cref="Key"/>), the game's own words as they were when the file was
/// written, and the translation. A translation file (UTF-8) holds them as
/// <code>
/// &lt;&lt;&lt; OPENING 12
/// (the game's lines)
/// &gt;&gt;&gt;
/// (the translated lines)
/// </code>
/// the next "&lt;&lt;&lt; " ending one; lines before the first are comments.
/// </summary>
public sealed record TextEntry(string Key, string[] Original, string[] Text);

public static class TextFile
{
    public const string Start = "<<< ", Split = ">>>";

    public static List<TextEntry> Parse(string text)
    {
        var list = new List<TextEntry>();
        string? key = null;
        List<string> orig = new(), tr = new();
        bool inText = false;
        void Flush()
        {
            if (key is not null) list.Add(new TextEntry(key, Trim(orig), Trim(tr)));
            (orig, tr, inText) = (new(), new(), false);
        }
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('\uFEFF').Split('\n'))
        {
            if (raw.StartsWith(Start, StringComparison.Ordinal)) { Flush(); key = NormalKey(raw[Start.Length..]); continue; }
            if (key is null) continue;
            if (!inText && raw.TrimEnd() == Split) { inText = true; continue; }
            (inText ? tr : orig).Add(raw);
        }
        Flush();
        return list;
    }

    public static List<TextEntry> Read(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    /// <summary>Spaces in a key are one space, upper case: "opening  12" and "OPENING 12" are one key.</summary>
    public static string NormalKey(string key) => string.Join(' ', key.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    /// <summary>Lines with what ends them trimmed off, and the empty lines after the last.</summary>
    public static string[] Trim(IEnumerable<string> lines)
    {
        var l = lines.Select(s => s.TrimEnd()).ToList();
        while (l.Count > 0 && l[^1].Length == 0) l.RemoveAt(l.Count - 1);
        return l.ToArray();
    }

    public static void Write(StringBuilder sb, string key, IReadOnlyList<string> original, IReadOnlyList<string> text)
    {
        sb.Append(Start).Append(key).Append('\n');
        foreach (var l in original) sb.Append(l).Append('\n');
        sb.Append(Split).Append('\n');
        foreach (var l in text) sb.Append(l).Append('\n');
        sb.Append('\n');
    }
}

/// <summary>
/// A game's translation (modern, BreezySRPG95's own): a folder of <see cref="TextFile"/>s — DATABASE.txt for the names
/// and words, SMAP_nnn.txt for each map's events — whose texts are shown instead of the game's. Messages and character
/// choices are looked up as they are shown, by the map, the script and the command's number; the names are put into
/// the <see cref="Database"/> itself (<see cref="Apply"/>), the game's kept to put back.
/// </summary>
public sealed class Translation
{
    public const string DatabaseFile = "DATABASE";
    /// <summary>A translation of just this is an empty one (a WORD piece the translation's language doesn't need).</summary>
    public const string Blank = "<empty>";

    public string Folder { get; }
    readonly Dictionary<string, Dictionary<string, TextEntry>> _files = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _reported = new();
    /// <summary>What didn't fit the game (a key it hasn't, words that changed...), each told once.</summary>
    public List<string> Problems { get; } = new();

    Translation(string folder) => Folder = folder;

    /// <summary>The translation in <paramref name="folder"/>, or null when there is none (no folder, no .txt in it).</summary>
    public static Translation? Load(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        var t = new Translation(folder);
        foreach (var f in Directory.GetFiles(folder, "*.txt"))
        {
            string name = Path.GetFileNameWithoutExtension(f);
            if (!name.Equals(DatabaseFile, StringComparison.OrdinalIgnoreCase) && !name.StartsWith("SMAP_", StringComparison.OrdinalIgnoreCase)) continue;
            var entries = new Dictionary<string, TextEntry>();
            try
            {
                foreach (var e in TextFile.Read(f))
                    if (!entries.TryAdd(e.Key, e)) t.Report($"{name}: {e.Key} is in the file twice (the first is used)");
            }
            catch (IOException e) { t.Report($"{name}: can't be read ({e.Message})"); }
            t._files[name.ToUpperInvariant()] = entries;
        }
        return t._files.Count > 0 ? t : null;
    }

    void Report(string problem) { if (_reported.Add(problem)) Problems.Add(problem); }

    TextEntry? Entry(string file, string key) =>
        _files.TryGetValue(file, out var d) && d.TryGetValue(key, out var e) && e.Text.Length > 0 ? e : null;

    /// <summary>
    /// The translation of a message, or null (the game's own then). One whose original no longer says what the game does
    /// is still used, and told (<see cref="Problems"/>): the key is what places it.
    /// </summary>
    public IReadOnlyList<string>? Message(EventScript s, MessageNode m)
    {
        if (Entry(s.Map, MessageKey(s, m)) is not { } e) return null;
        if (!Same(e.Original, m.Lines)) Report($"{s.Map}: {e.Key}: the game's words aren't the ones in the file");
        return e.Text.Select(Plain).ToList();
    }

    /// <summary>A character choice's options translated (as many as it has: missing ones stay the game's), or null.</summary>
    public string[]? Choice(EventScript s, ChoiceNode c)
    {
        if (Entry(s.Map, ChoiceKey(s, c)) is not { } e) return null;
        if (!Same(e.Original, c.Options)) Report($"{s.Map}: {e.Key}: the game's options aren't the ones in the file");
        if (e.Text.Length != c.Options.Length) Report($"{s.Map}: {e.Key}: {e.Text.Length} options for the game's {c.Options.Length}");
        return c.Options.Select((o, i) => i < e.Text.Length && e.Text[i].Length > 0 ? Plain(e.Text[i]) : o).ToArray();
    }

    public static string MessageKey(EventScript s, MessageNode m) => $"{s.Name} {m.At}".ToUpperInvariant();
    public static string ChoiceKey(EventScript s, ChoiceNode c) => $"{s.Name} {c.At} CHOICE".ToUpperInvariant();

    static bool Same(IReadOnlyList<string> file, IReadOnlyList<string> game) => TextFile.Trim(file).SequenceEqual(TextFile.Trim(game));

    /// <summary>
    /// Typographic punctuation as plain ASCII (machine translations are full of it, and the game's fonts draw it full
    /// width or not at all): curly quotes, the ellipsis, dashes, no-break spaces.
    /// </summary>
    public static string Plain(string s) => s.Replace('\u2018', '\'').Replace('\u2019', '\'').Replace('\u201C', '"').Replace('\u201D', '"')
        .Replace("\u2026", "...").Replace('\u2014', '-').Replace('\u2013', '-').Replace('\u00A0', ' ');

    // ---------------- the names and words (DATABASE.txt) ----------------

    (Unit[] Units, ClassDef[] Classes, Item[] Items, Magic[] Magic, Terrain[] Terrains, string[] Words)? _game;

    /// <summary>
    /// Puts the translation's names and words into <paramref name="db"/> (<paramref name="on"/>), or the game's back. The
    /// arrays are changed in place, so whatever holds them sees it; units already on a map keep the names they came with.
    /// </summary>
    public void Apply(Database db, bool on)
    {
        _game ??= ((Unit[])db.Units.Clone(), (ClassDef[])db.Classes.Clone(), (Item[])db.Items.Clone(), (Magic[])db.Magic.Clone(),
            (Terrain[])db.Terrains.Clone(), (string[])db.Words.Clone());
        var g = _game.Value;
        g.Units.CopyTo(db.Units, 0); g.Classes.CopyTo(db.Classes, 0); g.Items.CopyTo(db.Items, 0); g.Magic.CopyTo(db.Magic, 0);
        g.Terrains.CopyTo(db.Terrains, 0); g.Words.CopyTo(db.Words, 0);
        if (!on) return;

        string? One(string key, string game)
        {
            if (Entry(DatabaseFile, key) is not { } e) return null;
            if (!Same(e.Original, Lines(game))) Report($"{DatabaseFile}: {key}: the game's words aren't the ones in the file");
            return e.Text is [Blank] ? "" : string.Join("\r\n", e.Text.Select(Plain));
        }
        for (int i = 0; i < db.Units.Length; i++)
        {
            var u = db.Units[i];
            db.Units[i] = u with { Name = One($"UNIT {i + 1} NAME", u.Name) ?? u.Name, DeathMessage = One($"UNIT {i + 1} DEATH", u.DeathMessage) ?? u.DeathMessage };
        }
        for (int i = 0; i < db.Classes.Length; i++) db.Classes[i] = db.Classes[i] with { Name = One($"CLASS {i + 1} NAME", db.Classes[i].Name) ?? db.Classes[i].Name };
        for (int i = 0; i < db.Items.Length; i++)
        {
            var it = db.Items[i];
            db.Items[i] = it with { Name = One($"ITEM {i + 1} NAME", it.Name) ?? it.Name, Desc = One($"ITEM {i + 1} DESC", it.Desc) ?? it.Desc };
        }
        for (int i = 0; i < db.Magic.Length; i++)
        {
            var m = db.Magic[i];
            db.Magic[i] = m with { Name = One($"MAGIC {i + 1} NAME", m.Name) ?? m.Name, Desc = One($"MAGIC {i + 1} DESC", m.Desc) ?? m.Desc };
        }
        for (int i = 0; i < db.Terrains.Length; i++) db.Terrains[i] = db.Terrains[i] with { Name = One($"TERRAIN {i + 1} NAME", db.Terrains[i].Name) ?? db.Terrains[i].Name };
        for (int i = 0; i < db.Words.Length; i++) db.Words[i] = One($"WORD {i}", db.Words[i]) ?? db.Words[i];
    }

    static string[] Lines(string s) => s.Replace("\r\n", "\n").Split('\n');

    /// <summary>
    /// Checks the whole translation against the game — every key a text of the game's, every original as the game has it —
    /// and returns what didn't fit (the problems met so far included).
    /// </summary>
    public List<string> Check(GameFolder g, Database game)
    {
        var known = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var dbKeys = TextDump.DatabaseTexts(game).ToDictionary(t => t.Key, t => t.Lines);
        known[DatabaseFile] = dbKeys.Keys.ToHashSet();
        foreach (var path in TextDump.MapFiles(g))
        {
            var map = MapData.Load(path);
            var keys = new HashSet<string>();
            foreach (var s in map.Scripts.Values)
                foreach (var (key, lines) in TextDump.ScriptTexts(s))
                {
                    keys.Add(key);
                    if (Entry(s.Map, key) is { } e && !Same(e.Original, lines)) Report($"{s.Map}: {key}: the game's words aren't the ones in the file");
                }
            known[map.Scripts.Values.FirstOrDefault()?.Map ?? Path.GetFileNameWithoutExtension(path).ToUpperInvariant()] = keys;
        }
        foreach (var (file, entries) in _files)
            foreach (var key in entries.Keys)
                if (!known.TryGetValue(file, out var k) || !k.Contains(key)) Report($"{file}: {key}: no such text in the game");
                else if (file == DatabaseFile && Entry(file, key) is { } e && !Same(e.Original, dbKeys[key])) Report($"{file}: {key}: the game's words aren't the ones in the file");
        return Problems;
    }
}

/// <summary>Writes a game's texts as translation files (<see cref="Translation"/>), the translation half its own words.</summary>
public static class TextDump
{
    /// <summary>
    /// The names and words, in DATABASE.txt's order: units, classes, items, spells, terrain, words. Not the map names:
    /// nothing shows them (MAPNAME.DAT has the author's working names); each map's file has its own in its head.
    /// </summary>
    public static IEnumerable<(string Key, string[] Lines)> DatabaseTexts(Database db)
    {
        static string[] L(string s) => s.Replace("\r\n", "\n").Split('\n');
        foreach (var u in db.Units)
        {
            if (u.Name.Length > 0) yield return ($"UNIT {u.Index + 1} NAME", L(u.Name));
            if (u.DeathMessage.Trim().Length > 0) yield return ($"UNIT {u.Index + 1} DEATH", L(u.DeathMessage));
        }
        foreach (var c in db.Classes) if (c.Name.Length > 0) yield return ($"CLASS {c.Index + 1} NAME", L(c.Name));
        foreach (var it in db.Items)
        {
            if (it.Name.Length > 0) yield return ($"ITEM {it.Index + 1} NAME", L(it.Name));
            if (it.Name.Length > 0 && it.Desc.Length > 0) yield return ($"ITEM {it.Index + 1} DESC", L(it.Desc));
        }
        foreach (var m in db.Magic)
        {
            if (m.Name.Length > 0) yield return ($"MAGIC {m.Index + 1} NAME", L(m.Name));
            if (m.Name.Length > 0 && m.Desc.Length > 0) yield return ($"MAGIC {m.Index + 1} DESC", L(m.Desc));
        }
        foreach (var t in db.Terrains) if (t.Name.Length > 0) yield return ($"TERRAIN {t.Index + 1} NAME", L(t.Name));
        for (int i = 0; i < db.Words.Length; i++) if (db.Words[i].Length > 0) yield return ($"WORD {i}", L(db.Words[i]));
    }

    /// <summary>A script's messages and character choices in order, as the player sees them (a message's lines merged).</summary>
    public static IEnumerable<(string Key, string[] Lines)> ScriptTexts(EventScript s)
    {
        if (s.Error is not null) yield break;
        var stack = new Stack<IEnumerator<ScriptNode>>();
        stack.Push(s.Tree.GetEnumerator());
        while (stack.Count > 0)
        {
            if (!stack.Peek().MoveNext()) { stack.Pop(); continue; }
            switch (stack.Peek().Current)
            {
                case MessageNode m when m.Lines.Any(l => l.Trim().Length > 0):
                    yield return (Translation.MessageKey(s, m), m.Lines.ToArray());
                    break;
                case BranchNode b:
                    stack.Push(b.Then.GetEnumerator());
                    break;
                case ChoiceNode c:
                    yield return (Translation.ChoiceKey(s, c), c.Options);
                    // the cases after the choice, last pushed first
                    for (int k = c.Cases.Count - 1; k >= 0; k--) stack.Push(c.Cases[k].GetEnumerator());
                    break;
            }
        }
    }

    public static IEnumerable<string> MapFiles(GameFolder g) =>
        Enumerable.Range(0, 100).Select(i => { try { return g.MapPath(i); } catch (FileNotFoundException) { return null; } })
            .OfType<string>().Where(File.Exists);

    /// <summary>
    /// Writes the game's texts into <paramref name="dir"/>: DATABASE.txt and one SMAP_nnn.txt per map, each text's
    /// translation half its own words, ready to be translated. Returns the number of files written.
    /// </summary>
    public static int Write(GameFolder g, Database db, string dir, string header)
    {
        Directory.CreateDirectory(dir);
        int files = 0;
        var sb = new StringBuilder();
        void Save(string name)
        {
            File.WriteAllText(Path.Combine(dir, name + ".txt"), sb.ToString(), new UTF8Encoding(false));
            sb.Clear();
            files++;
        }
        sb.Append(header.Replace("{FILE}", "DATABASE (names, item and spell descriptions, menu words)"));
        foreach (var (key, lines) in DatabaseTexts(db)) TextFile.Write(sb, key, lines, lines);
        Save(Translation.DatabaseFile);

        foreach (var path in MapFiles(g))
        {
            var map = MapData.Load(path);
            string name = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
            int index = int.TryParse(name.AsSpan(5), out var n) ? n - 1 : -1;
            string title = index >= 0 && index < db.MapNames.Length && db.MapNames[index].Length > 0 ? db.MapNames[index] : map.Name;
            // the opening first, then the rest by name (EVoooo ss: map object oooo's event slot ss)
            var scripts = map.Scripts.Values.OrderBy(s => s.Name == "OPENING" ? 0 : 1).ThenBy(s => s.Name, StringComparer.Ordinal);
            var texts = scripts.SelectMany(ScriptTexts).ToList();
            if (texts.Count == 0) continue;
            sb.Append(header.Replace("{FILE}", $"{name} ({title})"));
            foreach (var (key, lines) in texts) TextFile.Write(sb, key, lines, lines);
            Save(name);
        }
        return files;
    }
}
