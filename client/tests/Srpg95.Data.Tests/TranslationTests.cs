using BreezySRPG95.Runtime;
using BreezySRPG95.Ui;

namespace Srpg95.Data.Tests;

/// <summary>Translations (translation/en): the text files, the lookups, the database's names, long messages over several windows.</summary>
public class TranslationTests
{
    [Fact]
    public void A_translation_file_reads_as_its_entries()
    {
        var e = TextFile.Parse("# a comment\n<<< opening  12\nルーティ：\nん？\n>>>\nRutee:\nHuh?\n\n\n<<< OPENING 13 CHOICE\nはい\nいいえ\n>>>\n");
        Assert.Equal(2, e.Count);
        Assert.Equal("OPENING 12", e[0].Key);
        Assert.Equal(new[] { "ルーティ：", "ん？" }, e[0].Original);
        Assert.Equal(new[] { "Rutee:", "Huh?" }, e[0].Text);   // the empty lines before the next entry aren't the text's
        Assert.Empty(e[1].Text);

        // written out, it reads back the same
        var sb = new System.Text.StringBuilder();
        foreach (var x in e) TextFile.Write(sb, x.Key, x.Original, x.Text);
        Assert.Equal(e, TextFile.Parse(sb.ToString()), new EntryComparer());
    }

    sealed class EntryComparer : IEqualityComparer<TextEntry>
    {
        public bool Equals(TextEntry? a, TextEntry? b) => a!.Key == b!.Key && a.Original.SequenceEqual(b.Original) && a.Text.SequenceEqual(b.Text);
        public int GetHashCode(TextEntry e) => e.Key.GetHashCode();
    }

    static (Translation T, string Dir) Load(params (string File, string Text)[] files)
    {
        var dir = Directory.CreateTempSubdirectory("breezysrpg95-translation").FullName;
        foreach (var (f, text) in files) File.WriteAllText(Path.Combine(dir, f), text);
        return (Translation.Load(dir)!, dir);
    }

    static EventScript Script(string map, string name) => new() { Name = name, Map = map, Comment = "", Commands = new(), Tree = new(), RoundTrips = true };

    [Fact]
    public void Messages_and_choices_are_found_by_map_script_and_command()
    {
        var (t, dir) = Load(("SMAP_001.txt", "<<< OPENING 3\nルーティ：\nん？\n>>>\nRutee:\n“Huh?”…\n\n<<< OPENING 7 CHOICE\nはい\nいいえ\n>>>\nYes\n"));
        try
        {
            var s = Script("SMAP_001", "OPENING");
            var m = new MessageNode(2, new List<string> { "ルーティ：", "ん？" }, 3);
            Assert.Equal(new[] { "Rutee:", "\"Huh?\"..." }, t.Message(s, m));   // typographic punctuation made plain
            Assert.Null(t.Message(Script("SMAP_002", "OPENING"), m));         // another map's
            Assert.Null(t.Message(s, m with { At = 4 }));                      // another command's
            Assert.Empty(t.Problems);

            // a choice translated in part keeps the game's for the rest, and says so
            var c = new ChoiceNode(new Command(Op.CharacterChoice, 0, new byte[2]), new[] { "はい", "いいえ" }, new(), 7);
            Assert.Equal(new[] { "Yes", "いいえ" }, t.Choice(s, c));
            Assert.Single(t.Problems);

            // the game's words changed since the file was written: still used, and told
            Assert.NotNull(t.Message(s, m with { Lines = new List<string> { "ジュナ：", "ん？" } }));
            Assert.Equal(2, t.Problems.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    static Database Db() => new()
    {
        Units = new[] { new Unit(0, "ルーティ", 1, 0, 1, 0, new int[6], new int[6], -1, -1, -1, "ああ…\r\nごめん") },
        Classes = Array.Empty<ClassDef>(), Items = Array.Empty<Item>(), Magic = Array.Empty<Magic>(), Terrains = Array.Empty<Terrain>(),
        Animations = Array.Empty<Animation>(), Words = new[] { "", "ＬＶ", "が" }, MapNames = new[] { "１話" },
        VariableNames = Array.Empty<string>(), SwitchNames = Array.Empty<string>(), StartMap = 0,
    };

    [Fact]
    public void Names_go_into_the_database_and_back_out()
    {
        var db = Db();
        Assert.Contains(("UNIT 1 DEATH", new[] { "ああ…", "ごめん" }), TextDump.DatabaseTexts(db).Select(x => (x.Key, x.Lines)), new PairComparer());
        var (t, dir) = Load(("DATABASE.txt", "<<< UNIT 1 NAME\nルーティ\n>>>\nRutee\n\n<<< UNIT 1 DEATH\nああ…\nごめん\n>>>\nAh...\nSorry\n\n"
            + "<<< WORD 2\nが\n>>>\n<empty>\n"));
        try
        {
            var units = db.Units;
            t.Apply(db, true);
            Assert.Same(units, db.Units);   // changed in place: whatever holds the array sees it
            Assert.Equal("Rutee", db.Units[0].Name);
            Assert.Equal("Ah...\r\nSorry", db.Units[0].DeathMessage);
            Assert.Equal("", db.Words[2]);
            Assert.Equal("ＬＶ", db.Words[1]);
            t.Apply(db, false);
            Assert.Equal("ルーティ", db.Units[0].Name);
            Assert.Equal("が", db.Words[2]);
            Assert.Empty(t.Problems);
        }
        finally { Directory.Delete(dir, true); }
    }

    sealed class PairComparer : IEqualityComparer<(string, string[])>
    {
        public bool Equals((string, string[]) a, (string, string[]) b) => a.Item1 == b.Item1 && a.Item2.SequenceEqual(b.Item2);
        public int GetHashCode((string, string[]) x) => x.Item1.GetHashCode();
    }

    [Fact]
    public void Sentence_pieces_get_spaces_only_in_a_translated_game()
    {
        try
        {
            Lang.GameTranslated = false;
            Assert.Equal("ＨＰが５ポイント回復した", Lang.Phrase("ＨＰ", "が", "５", "ポイント", "回復した"));
            Lang.GameTranslated = true;
            Assert.Equal("HP 5 pts recovered", Lang.Phrase("HP", "", "5", "pts", "recovered"));
            Assert.Equal("1200 G", Lang.Phrase("1200", " G"));
            Assert.Equal("Rutee reached level 5!", Lang.Phrase("Rutee", "reached level", "5", "!"));
        }
        finally { Lang.GameTranslated = false; }
    }

    [Fact]
    public void A_long_translated_message_goes_on_over_more_windows_before_its_choice()
    {
        // a message, then a choice: translated, the message takes two windows, the second staying up under the choice
        var c = new ChoiceNode(new Command(Op.CharacterChoice, 0, new byte[2]), new[] { "X", "Y" }, new() { new(), new() }, 1);
        var script = Script("SMAP_001", "OPENING");
        script = new EventScript { Name = script.Name, Map = script.Map, Comment = "", Commands = new(), RoundTrips = true,
            Tree = new List<ScriptNode> { new MessageNode(2, new List<string> { "あ" }, 0), c } };
        var r = new EventRunner(new GameState(), new CompatibilityTests.NoMap(), new Audio(new GameFolder(Path.GetTempPath())), new ScreenEffects())
        {
            TranslateMessage = (_, _) => new[] { "one", "two" },
            TranslateChoice = (_, _) => new[] { "Ex", "Why" },
            Fit = lines => lines.Select(l => ((IReadOnlyList<string>)new[] { l }, l == "two")).ToList(),
        };
        r.Start(script);
        r.Update(new EventInput(false, false, false, false, false));
        Assert.Equal("one", string.Join("/", r.Message!.Lines));
        Assert.Null(r.Choice);
        r.Update(new EventInput(true, false, false, false, false));
        Assert.Null(r.Message);
        Assert.Equal("two", string.Join("/", r.LastMessage!.Lines));
        Assert.True(r.LastMessage.Small);
        Assert.Equal(new[] { "Ex", "Why" }, r.Choice);

        // saved at the first window, the second still comes after it
        r.Start(script);
        r.Update(new EventInput(false, false, false, false, false));
        var save = r.Save()!;
        var again = new EventRunner(new GameState(), new CompatibilityTests.NoMap(), new Audio(new GameFolder(Path.GetTempPath())), new ScreenEffects())
        {
            TranslateChoice = (_, _) => new[] { "Ex", "Why" },
        };
        Assert.True(again.Restore(script, save));
        again.Update(new EventInput(true, false, false, false, false));
        Assert.Equal("two", string.Join("/", again.LastMessage!.Lines));
        Assert.Equal(new[] { "Ex", "Why" }, again.Choice);
    }
}
