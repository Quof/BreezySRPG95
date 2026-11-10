using BreezySRPG95.Runtime;
using Srpg95.Rules;

namespace Srpg95.Data.Tests;

/// <summary>
/// Rules from the audit against SRPGEXEC.EXE (FORMAT_NOTES §16): event commands and conditions the game uses, party
/// commands, class and equipment changes, phase upkeep, terrain objects, win/lose and sortie.
/// </summary>
public class AuditTests
{
    static string? Game()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "えるまに", "SRPGEXEC.EXE"))) return Path.Combine(d.FullName, "えるまに");
        return null;
    }

    static IEnumerable<(int Index, MapData Map)> Maps(GameFolder g) =>
        Directory.GetFiles(Path.Combine(g.Root, "MAP"), "SMAP_*.DAT").Order()
            .Select(p => (int.Parse(Path.GetFileNameWithoutExtension(p)[5..]) - 1, MapData.Load(p)));

    static IEnumerable<ScriptNode> Walk(IEnumerable<ScriptNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            if (n is BranchNode b) foreach (var x in Walk(b.Then)) yield return x;
            if (n is ChoiceNode c) foreach (var x in c.Cases.SelectMany(Walk)) yield return x;
        }
    }

    [Fact]
    public void Every_command_and_condition_the_game_uses_is_handled()
    {
        if (Game() is not { } root) return;
        var g = new GameFolder(root);
        var unhandled = new List<string>();
        int[] conditions = { 0, 1, 2, 4, 5, 6, 7, 8, 9, 10, 11 };
        foreach (var (index, map) in Maps(g))
        {
            foreach (var (name, sc) in map.Scripts.Where(s => s.Value.Error is null))
                foreach (var n in Walk(sc.Tree))
                    if (n is CommandNode { Command: var c } && !EventRunner.Handled.Contains(c.Op))
                        unhandled.Add($"SMAP_{index + 1:000} {name}: {c.Op}");
            foreach (var o in map.Objects.Where(o => o.InUse))
                foreach (var sl in o.Slots.Where(s => !conditions.Contains(s.Condition)))
                    unhandled.Add($"SMAP_{index + 1:000} object {o.Index}: condition {sl.Condition}");
        }
        Assert.Empty(unhandled);
    }

    [Fact]
    public void Every_next_map_exists_except_the_known_authoring_slip()
    {
        if (Game() is not { } root) return;
        var g = new GameFolder(root);
        var maps = Maps(g).ToList();
        var have = maps.Select(m => m.Index).ToHashSet();
        var missing = new List<string>();
        foreach (var (index, map) in maps)
            foreach (var (name, sc) in map.Scripts.Where(s => s.Value.Error is null))
                foreach (var n in Walk(sc.Tree))
                    if (n is CommandNode { Command: { Op: Op.SelectMap } c } && !have.Contains(c.U16(0)))
                        missing.Add($"SMAP_{index + 1:000} {name} -> SMAP_{c.U16(0) + 1:000}");
        // SMAP_041's opening sends a missing ゴーバック to index 99 (an empty SMAP_100), an authoring bug (FORMAT_NOTES §11.4)
        Assert.All(missing, m => Assert.StartsWith("SMAP_041 OPENING", m));
    }

    static (GameState State, Database Db, Battlefield Field) Map4()
    {
        var g = new GameFolder(Game()!);
        var db = Database.Load(g);
        var map = MapData.Load(g.MapPath(3));
        var f = new Battlefield(map, db, g.Chipset(map.Tileset));
        int slot = 1;
        foreach (var o in map.Objects.Where(o => o.InUse && o.Type != ObjectType.Other))
        {
            var u = db.Units[o.Unit];
            f.Units.Add(new BattleUnit
            {
                Unit = o.Unit, Side = o.Type == ObjectType.Ally ? Side.Ally : o.Type == ObjectType.Enemy ? Side.Enemy : Side.Neutral,
                MapObject = o.Index, ClassId = u.ClassId, Level = u.Level, Base = (int[])u.BaseStats.Clone(), Hp = u.BaseStats[0],
                Weapon = u.Weapon, Armor = u.Armor, Accessory = u.Accessory, X = o.X, Y = o.Y, Slot = slot++, Name = u.Name,
            });
        }
        return (new GameState(), db, f);
    }

    [Fact]
    public void Add_ally_turns_enemies_of_that_id_into_party_members()
    {
        if (Game() is null) return;
        var (s, db, f) = Map4();
        var hans = f.Units.Single(u => u.Unit == 244);    // ハンス: an enemy on 4話 until EV000101 adds him
        Assert.Equal(Side.Enemy, hans.Side);
        Party.Add(s, db, f, 244);
        Assert.Equal(Side.Ally, hans.Side);
        Assert.True(s.Roster.First(e => e.Unit == 244).Sortie);
        Assert.Equal(hans.Level, s.Roster.First(e => e.Unit == 244).Level);

        Party.Add(s, db, f, 224);                           // 紅葉 isn't on the map: straight from UNIT.DAT, 出撃 off
        Assert.False(s.Roster.First(e => e.Unit == 224).Sortie);
        Assert.Equal(db.Units[224].Level, s.Roster.First(e => e.Unit == 224).Level);

        Party.Remove(s, f, 244);                            // leaving: out of the roster, neutral on the map
        Assert.DoesNotContain(s.Roster, e => e.Unit == 244);
        Assert.Equal(Side.Neutral, hans.Side);
    }

    [Fact]
    public void Class_change_moves_stats_by_the_classes_initial_stats_and_keeps_the_level()
    {
        if (Game() is null) return;
        var (s, db, f) = Map4();
        Party.Add(s, db, f, 244);
        var before = s.Roster.First(e => e.Unit == 244);
        int to = Enumerable.Range(0, db.Classes.Length).First(c => c != before.ClassId && db.Classes[c].InitStats.Any(v => v != 0));
        Party.ChangeClass(s, db, f, 244, to);
        var after = s.Roster.First(e => e.Unit == 244);
        var from = db.Classes[before.ClassId];
        for (int i = 0; i < 4; i++)
            Assert.Equal(Math.Clamp(before.Base[2 + i] + db.Classes[to].InitStats[i] - from.InitStats[i], 1, 30), after.Base[2 + i]);
        Assert.Equal(before.Base[0], after.Base[0]);
        Assert.Equal(before.Level, after.Level);
        Assert.Equal(to, f.Units.Single(u => u.Unit == 244).ClassId);   // the unit on the map too
    }

    [Fact]
    public void Equipment_change_fills_the_items_slot_and_returns_the_old_one()
    {
        if (Game() is null) return;
        var (s, db, f) = Map4();
        Party.Add(s, db, f, 244);
        var armor = db.Items.First(it => it.Type == ItemType.Armor && it.Index != s.Roster.First(e => e.Unit == 244).Armor);
        int old = s.Roster.First(e => e.Unit == 244).Armor;
        Party.ChangeEquipment(s, db, f, 244, armor.Index);
        Assert.Equal(armor.Index, s.Roster.First(e => e.Unit == 244).Armor);
        if (old >= 0) Assert.Equal(2, s.ItemCount(old));   // once for the roster entry, once for the unit on the map (as the exe)
    }

    [Fact]
    public void Stat_up_items_stop_at_60_and_30()
    {
        var (b, gained) = UnitChanges.StatUp(new[] { 58, 10, 29, 5, 5, 5 }, new[] { 5, 0, 5, -3, 0, 0 });
        Assert.Equal(new[] { 60, 10, 30, 5, 5, 5 }, b);
        Assert.Equal(new[] { 2, 0, 1, 0, 0, 0 }, gained);
    }

    [Fact]
    public void Phase_upkeep_follows_the_exe()
    {
        if (Game() is null) return;
        var (_, _, f) = Map4();
        var ally = f.Units.First(u => u.Side == Side.Enemy);
        ally.Side = Side.Neutral;                // neutral units get their upkeep with the player's phase
        ally.Hp = 18;
        ally.PoisonTurns = 2;
        f.NextPhase();                           // enemy phase: not the neutral's
        Assert.Equal(18, ally.Hp);
        f.NextPhase();                           // player phase
        Assert.Equal(18 - 18 / 4 + f.TerrainAt(ally.X, ally.Y).HpRecovery is var hp && hp > ally.MaxHp ? ally.MaxHp : Math.Max(1, hp), ally.Hp);
        Assert.Equal(1, ally.PoisonTurns);
        Assert.Equal(4, Combat.PoisonDamage(19));   // a quarter, rounded down
    }

    [Fact]
    public void Other_objects_graphics_change_the_terrain_under_them()
    {
        if (Game() is null) return;
        var (_, db, f) = Map4();
        var plain = f.TerrainAt(0, 0);
        f.ChipOverride = (x, y) => x == 0 && y == 0 ? 143 : null;
        Assert.Equal(db.Terrains[Math.Min(new GameFolder(Game()!).Chipset(f.Map.Tileset)![143], db.Terrains.Length - 1)].Index, f.TerrainAt(0, 0).Index);
        f.ChipOverride = null;
        Assert.Equal(plain, f.TerrainAt(0, 0));
    }

    [Fact]
    public void Leader_maps_are_won_when_no_enemy_leader_is_on_the_map()
    {
        if (Game() is null) return;
        var (_, _, f) = Map4();
        f.Units.Add(new BattleUnit { Unit = 242, Side = Side.Ally, Base = new int[6], Hp = 10, X = 0, Y = 0, Slot = 99 });
        foreach (var u in f.Units.Where(u => u.Side == Side.Enemy)) u.Dead = true;
        Assert.True(f.Won());                    // 4話 is "defeat all enemies"
        Assert.False(f.Lost());
    }

    [Fact]
    public void Sortie_keeps_the_character_list_and_the_start_positions()
    {
        if (Game() is null) return;
        var g = new GameFolder(Game()!);
        var db = Database.Load(g);
        var next = MapData.Load(g.MapPath(16));   // SMAP_017: 3 start positions, character list ルーティ, ジュナ, セイナ
        var s = new GameState();
        foreach (var id in new[] { 236, 237, 238, 239, 240, 242, 243 }) s.Roster.Add(Party.Fresh(db, id) with { Sortie = true });
        Party.PrepareSortie(s, next);
        var on = s.Roster.Where(e => e.Sortie).Select(e => e.Unit).ToList();
        // the required ones always stay on; of the rest only the first ones in roster (unit id) order, up to the start
        // positions, keep it — as the exe trims, so a required unit can end up past the limit (the player fixes it at camp)
        Assert.Contains(242, on);
        Assert.Contains(243, on);
        Assert.Contains(240, on);
        Assert.Equal(new[] { 236, 237, 238, 240, 242, 243 }, on);
    }

    [Fact]
    public void Spells_never_touch_unattackable_neutral_units()
    {
        if (Game() is null) return;
        var (_, db, f) = Map4();
        var caster = f.Units.First(u => u.Side == Side.Enemy);
        var npc = new BattleUnit { Unit = 0, Side = Side.Neutral, Unattackable = true };
        foreach (var m in db.Magic.Where(m => m.Name.Length > 0).Take(20)) Assert.False(f.Affects(caster, m, npc));
    }
}
