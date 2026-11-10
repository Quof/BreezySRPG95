using BreezySRPG95.Runtime;

namespace Srpg95.Data.Tests;

/// <summary>SAVEn.DAT round trips against the user's real saves (えるまに - Save testplay).</summary>
public class SaveFileTests
{
    static string? Folder(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, name))) return Path.Combine(d.FullName, name);
        return null;
    }

    [Fact]
    public void Every_camp_save_rebuilds_its_roster_records()
    {
        var test = Folder("えるまに - Save testplay");
        var game = Folder("えるまに");
        if (test is null || game is null) return;
        var db = Database.Load(new GameFolder(game));
        int checkedSaves = 0;
        foreach (var path in Directory.GetFiles(test, "SAVE?.DAT"))
        {
            var save = SaveFile.Load(path);
            if (save is not { AtCamp: true }) continue;
            var state = new GameState();
            save.ApplyTo(state);
            var blank = (byte[])save.Raw.Clone();
            Array.Clear(blank, 0x101D0, 50 * 640);
            var bgm = System.Text.Encoding.GetEncoding(932).GetString(save.Raw, 0x53C, 48).TrimEnd('\0');
            var fresh = SaveFile.ForCamp(state, db, save.MapIndex, bgm, blank);
            for (int u = 0; u < state.Roster.Count; u++)
                for (int k = 0; k < 160; k++)
                {
                    if (k is 5 or 6 or 50 or 52 or 54 or 55 or 56) continue;   // last battle's leftovers: position, movement left, [52], [56]
                    int off = 0x101D0 + u * 640 + k * 4;
                    Assert.True(BitConverter.ToInt32(save.Raw, off) == BitConverter.ToInt32(fresh.Raw, off),
                        $"{Path.GetFileName(path)} roster {u} field [{k}]: {BitConverter.ToInt32(save.Raw, off)} vs {BitConverter.ToInt32(fresh.Raw, off)}");
                }
            checkedSaves++;
        }
        Assert.True(checkedSaves > 0 || Environment.GetEnvironmentVariable("SRPG95_REQUIRE_DATA") != "1");
    }

    [Fact]
    public void Camp_save_round_trips_byte_for_byte()
    {
        var test = Folder("えるまに - Save testplay");
        var game = Folder("えるまに");
        if (test is null || game is null || !File.Exists(Path.Combine(test, "SAVE6.DAT")))
        {
            Assert.True(Environment.GetEnvironmentVariable("SRPG95_REQUIRE_DATA") != "1", "save data missing");
            return;
        }
        var db = Database.Load(new GameFolder(game));
        var save = SaveFile.Load(Path.Combine(test, "SAVE6.DAT"))!;   // camp before map 2
        Assert.True(save.AtCamp);
        Assert.Equal(1, save.MapIndex);   // header 0x004: the camp's map (SMAP_002)
        Assert.Equal(2, save.NextMap);    // header 0x010: the one after it

        var state = new GameState();
        save.ApplyTo(state);
        Assert.Equal(new[] { 242, 243 }, state.Roster.Select(e => e.Unit).Order());
        Assert.Equal(1, state.ItemCount(40));                        // one ポーション
        Assert.Equal(10, state.Gold);

        // rebuild it from the parsed state on a blank template for the roster part and compare every byte
        var bgm = System.Text.Encoding.GetEncoding(932).GetString(save.Raw, 0x53C, 48).TrimEnd('\0');
        var again = SaveFile.ForCamp(state, db, save.MapIndex, bgm, save.Raw);
        Assert.Equal(save.Raw, again.Raw);

        // and with the roster records rebuilt from nothing (positions aside, which a camp doesn't know)
        var blankRoster = (byte[])save.Raw.Clone();
        Array.Clear(blankRoster, 0x101D0, 50 * 640);
        var fresh = SaveFile.ForCamp(state, db, save.MapIndex, bgm, blankRoster);
        int[] skip = { 5, 6, 54, 55 };   // x, y and position before moving
        for (int u = 0; u < 2; u++)
            for (int k = 0; k < 160; k++)
            {
                if (skip.Contains(k)) continue;
                int off = 0x101D0 + u * 640 + k * 4;
                Assert.True(BitConverter.ToInt32(save.Raw, off) == BitConverter.ToInt32(fresh.Raw, off),
                    $"roster {u} field [{k}]: {BitConverter.ToInt32(save.Raw, off)} vs {BitConverter.ToInt32(fresh.Raw, off)}");
            }
    }

    [Fact]
    public void Battle_save_records_match_the_original_apart_from_unknown_leftovers()
    {
        var test = Folder("えるまに - Save testplay");
        var game = Folder("えるまに");
        if (test is null || game is null || !File.Exists(Path.Combine(test, "SAVE3.DAT"))) return;
        var folder = new GameFolder(game);
        var db = Database.Load(folder);
        var save = SaveFile.Load(Path.Combine(test, "SAVE3.DAT"))!;   // map 2, turn 1
        Assert.False(save.AtCamp);
        var state = new GameState();
        save.ApplyTo(state);
        var again = Rewrite(folder, db, save, state, BattleUnits(save));
        var differing = new HashSet<string>();
        for (int i = 0; i < SaveFile.Size; i++)
            if (save.Raw[i] != again.Raw[i])
                differing.Add(i is >= 0x7D0 and < 0x101D0 ? $"unit[{(i - 0x7D0) % 640 / 4}]" : $"0x{i:X}");
        // what the client can't know: an ally's movement left and position before moving from earlier play, and [52]
        Assert.Subset(new HashSet<string> { "unit[50]", "unit[52]", "unit[54]", "unit[55]" }, differing);
    }

    [Fact]
    public void Battle_saves_keep_buffs_poison_and_sleep()
    {
        var test = Folder("えるまに - Save testplay");
        var game = Folder("えるまに");
        if (test is null || game is null || !File.Exists(Path.Combine(test, "SAVE3.DAT"))) return;
        var folder = new GameFolder(game);
        var save = SaveFile.Load(Path.Combine(test, "SAVE3.DAT"))!;
        var state = new GameState();
        save.ApplyTo(state);
        var units = BattleUnits(save);
        var u = units.First(x => x.Side == Srpg95.Rules.Side.Ally);
        (u.PowerUp, u.DefUp, u.MdefUp, u.PoisonTurns, u.SleepTurns) = (2, -1, 3, 4, 1);
        // records [36]–[38] and [45]/[46], written and read back as a load restores them
        var back = Rewrite(folder, Database.Load(folder), save, state, units).MapUnitRecords().Single(r => r.Record == u.Slot);
        Assert.Equal((2, -1, 3, 4, 1), (back.PowerUp, back.DefUp, back.MdefUp, back.PoisonTurns, back.SleepTurns));
    }

    /// <summary>A battle save's units rebuilt the way the client holds them.</summary>
    static List<Srpg95.Rules.BattleUnit> BattleUnits(SaveFile save) => save.MapUnitRecords().Select(r => new Srpg95.Rules.BattleUnit
    {
        Unit = r.Unit, Side = r.Computer ? Srpg95.Rules.Side.Enemy : Srpg95.Rules.Side.Ally, MapObject = r.Object,
        ClassId = r.ClassId, Level = r.Level, Exp = r.Exp, Base = r.Base, Hp = r.Hp, Mp = r.Mp,
        Weapon = r.Weapon, Armor = r.Armor, Accessory = r.Accessory, X = r.X, Y = r.Y, Acted = r.Acted, Dead = !r.Alive,
        Cleared = !r.Alive, Slot = r.Record, Guest = r.Guest, RosterSlot = r.RosterSlot,
    }).ToList();

    /// <summary>The battle save the client writes for these units, on top of the original.</summary>
    static SaveFile Rewrite(GameFolder folder, Database db, SaveFile save, GameState state, List<Srpg95.Rules.BattleUnit> units)
    {
        var bgm = System.Text.Encoding.GetEncoding(932).GetString(save.Raw, 0x53C, 48).TrimEnd('\0');
        return SaveFile.ForBattle(state, db, save.MapIndex, bgm, save.Phase, folder.MapPath(save.MapIndex), units, save.FiredSlots(), save.Raw);
    }
}