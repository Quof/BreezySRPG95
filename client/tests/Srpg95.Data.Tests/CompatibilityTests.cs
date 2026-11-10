using BreezySRPG95.Runtime;
using Srpg95.Rules;

namespace Srpg95.Data.Tests;

/// <summary>
/// What other SRPG Maker 95 games need beyond えるまに (found in the ＯtoＺ, 昴の騎士 and 闇鍋企画 data): units placed
/// off the map, GAME.DAT's lost-when-fallen rule, the message length limit and a fresh face for every event.
/// </summary>
public class CompatibilityTests
{
    static string? Folder(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, name))) return Path.Combine(d.FullName, name);
        return null;
    }

    [Fact]
    public void Units_off_the_map_stand_on_the_stored_grid()
    {
        if (Folder("えるまに") is not { } root) return;
        var g = new GameFolder(root);
        var map = MapData.Load(g.MapPath(3));
        var f = new Battlefield(map, Database.Load(g), g.Chipset(map.Tileset));
        Assert.Equal(MapData.MaxSize, map.Tiles.GetLength(0));
        Assert.Equal(MapData.MaxSize, map.Tiles.GetLength(1));
        // past the map's size the exe reads the rest of its 40 × 40 grid (0 in shipped maps); 闇鍋企画 hides armies at
        // start positions out there. Beyond the grid the client takes chip 0 rather than failing.
        int chip0 = f.TerrainAt(-1, -1).Index;
        Assert.Equal(chip0, f.TerrainAt(MapData.MaxSize, 0).Index);
        Assert.Equal(chip0, f.TerrainAt(0, MapData.MaxSize + 5).Index);
        if (map.Width < MapData.MaxSize) Assert.Equal(chip0, f.TerrainAt(map.Width, 0).Index);
    }

    /// <summary>A message's or message line's payload: window 0, the text, NUL.</summary>
    static byte[] Text(string s) => [0, 0, .. Bin.Sjis.GetBytes(s), 0];

    /// <summary>An event file: the 100-byte comment, the stream length, then the commands (op, u16 length, depth 0, payload).</summary>
    static byte[] Script(params (Op Op, byte[] Payload)[] commands)
    {
        var body = new List<byte>();
        foreach (var (op, p) in commands) body.AddRange([(byte)op, (byte)p.Length, (byte)(p.Length >> 8), 0, .. p]);
        var file = new byte[104 + body.Count];
        BitConverter.GetBytes(body.Count).CopyTo(file, 100);
        body.CopyTo(file, 104);
        return file;
    }

    [Fact]
    public void A_message_takes_at_most_five_lines()
    {
        // FUN_0040d18c reads a message and at most four continuation lines; 闇鍋企画 has one with a sixth, never shown
        var sc = EventScript.Parse("EV000101", Script((Op.Message, Text("1")), (Op.MessageLine, Text("2")), (Op.MessageLine, Text("3")),
            (Op.MessageLine, Text("4")), (Op.MessageLine, Text("5")), (Op.MessageLine, Text("6")), (Op.Message, Text("7")), (Op.EndOfScript, [])));
        Assert.Null(sc.Error);
        var messages = sc.Tree.OfType<MessageNode>().ToList();
        Assert.Equal(2, messages.Count);
        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, messages[0].Lines);
        Assert.Equal(new[] { "7" }, messages[1].Lines);
    }

    internal sealed class NoMap : IMapContext
    {
        public int Phase => 1;
        public bool UnitState(int unit, int state) => false;
        public void ShowTile(int x, int y) { }
        public bool Panning => false;
        public void FinishPan() { }
        public void QueueMove(int unit, IReadOnlyList<byte> steps) { }
        public void ExecuteMoves() { }
        public bool MovesRunning => false;
        public void FinishMoves() { }
        public void SwitchesChanged() { }
        public bool PartyHas(int item) => false;
        public void PartyAdd(int unit) { }
        public void PartyRemove(int unit) { }
        public void ChangeClass(int unit, int cls) { }
        public void ChangeEquipment(int unit, int item) { }
    }

    [Fact]
    public void Every_event_starts_without_a_face()
    {
        var runner = new EventRunner(new GameState(), new NoMap(), new Audio(new GameFolder(Path.GetTempPath())), new ScreenEffects());
        runner.Start(EventScript.Parse("EV000101", Script((Op.FaceGraphic, [0, 0, 5, 0]), (Op.Message, Text("a")), (Op.EndOfScript, []))));
        runner.Update(default);
        Assert.Equal(5, runner.Message?.Face);
        // the next event's message has no face until it sets one: FUN_0040cfe0 / FUN_0040d0a4 clear it when they load
        runner.Start(EventScript.Parse("EV000102", Script((Op.Message, Text("b")), (Op.EndOfScript, []))));
        runner.Update(default);
        Assert.Equal(0, runner.Message?.Face);
    }

    /// <summary>Two army units (one fallen) and two units that joined on the map (one fallen), at a map's end.</summary>
    static (GameState State, int[] Ids) MapEnd(GameFolder g)
    {
        var db = Database.Load(g);
        var map = MapData.Load(g.MapPath(Directory.GetFiles(Path.Combine(g.Root, "MAP"), "SMAP_*.DAT").Order()
            .Select(p => int.Parse(Path.GetFileNameWithoutExtension(p)[5..]) - 1).First()));
        var f = new Battlefield(map, db, g.Chipset(map.Tileset));
        var ids = Enumerable.Range(1, db.Units.Length - 1).Where(i => db.Units[i].Name.Length > 0).Take(4).ToArray();
        var s = new GameState();
        for (int k = 0; k < 4; k++)
        {
            var u = db.Units[ids[k]];
            bool army = k < 2, fallen = k % 2 == 0;
            if (army) s.Roster.Add(Party.Fresh(db, ids[k]) with { Sortie = true });
            f.Units.Add(new BattleUnit
            {
                Unit = ids[k], Side = Side.Ally, Name = u.Name, ClassId = u.ClassId, Level = u.Level, Base = (int[])u.BaseStats.Clone(),
                Hp = fallen ? 0 : u.BaseStats[0], Dead = fallen, Weapon = u.Weapon, Armor = u.Armor, Accessory = u.Accessory,
                Slot = k + 1, RosterSlot = army ? s.Roster.Count - 1 : -1,
            });
        }
        Party.SaveFromMap(s, f);
        return (s, ids);
    }

    [Fact]
    public void Fallen_units_are_lost_where_game_dat_says_so()
    {
        if (Folder("ＯtoＺ") is not { } root) return;
        var g = new GameFolder(root);
        Assert.True(Database.Load(g).FallenLost);   // GAME.DAT [5] = 1
        var (s, ids) = MapEnd(g);
        // FUN_0041d5e4: a fallen army unit leaves the roster; a unit that fell before joining doesn't join
        Assert.Equal(new[] { ids[1], ids[3] }, s.Roster.Select(e => e.Unit).Order());
    }

    [Fact]
    public void Fallen_units_come_back_in_erumani()
    {
        if (Folder("えるまに") is not { } root) return;
        var g = new GameFolder(root);
        Assert.False(Database.Load(g).FallenLost);  // GAME.DAT [5] = 0
        var (s, ids) = MapEnd(g);
        Assert.Equal(ids.Order(), s.Roster.Select(e => e.Unit).Order());
    }

    [Fact]
    public void Allies_of_one_unit_record_stay_separate_in_the_army()
    {
        // エルディア大陸戦記's prologue has three ally soldiers of one UNIT.DAT record: the original keeps all three
        if (Folder("えるまに") is not { } root) return;
        var g = new GameFolder(root);
        var db = Database.Load(g);
        var map = MapData.Load(g.MapPath(Directory.GetFiles(Path.Combine(g.Root, "MAP"), "SMAP_*.DAT").Order()
            .Select(p => int.Parse(Path.GetFileNameWithoutExtension(p)[5..]) - 1).First()));
        var f = new Battlefield(map, db, g.Chipset(map.Tileset));
        var named = Enumerable.Range(1, db.Units.Length - 1).Where(i => db.Units[i].Name.Length > 0).ToArray();
        int hero = named[1], soldier = named[0];   // the soldiers' id sorts first
        var s = new GameState();
        s.Roster.Add(Party.Fresh(db, hero) with { Sortie = true });
        BattleUnit Unit(int id, int cls, int slot, int rosterSlot) => new()
        {
            Unit = id, Side = Side.Ally, Name = db.Units[id].Name, ClassId = cls, Level = 1, Base = (int[])db.Units[id].BaseStats.Clone(),
            Hp = 1, Slot = slot, RosterSlot = rosterSlot,
        };
        f.Units.Add(Unit(hero, db.Units[hero].ClassId, 1, 0));                    // deployed from the army: back into its entry
        for (int k = 0; k < 3; k++) f.Units.Add(Unit(soldier, k, 2 + k, -1));    // three map objects, three classes: three new entries
        Party.SaveFromMap(s, f);
        Assert.Equal(new[] { soldier, soldier, soldier, hero }, s.Roster.Select(e => e.Unit));   // sorted by id, in record order
        Assert.Equal(new[] { 0, 1, 2 }, s.Roster.Take(3).Select(e => e.ClassId));

        // a camp save keeps them, and so does loading it
        var save = SaveFile.ForCamp(s, db, 0, "", null);
        var path = Path.Combine(Directory.CreateTempSubdirectory("breezysrpg95-army").FullName, "SAVE1.DAT");
        try
        {
            File.WriteAllBytes(path, save.Raw);
            var back = new GameState();
            SaveFile.Load(path)!.ApplyTo(back);
            Assert.Equal(s.Roster.Select(e => (e.Unit, e.ClassId)), back.Roster.Select(e => (e.Unit, e.ClassId)));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }

        // "remove ally" takes the first of them; a class change reaches all that are left
        Party.Remove(s, f, soldier);
        Assert.Equal(2, s.Roster.Count(e => e.Unit == soldier));
        Party.ChangeClass(s, db, null, soldier, 3);
        Assert.All(s.Roster.Where(e => e.Unit == soldier), e => Assert.Equal(3, e.ClassId));
    }
}
