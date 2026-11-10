using Srpg95.Data;
using Srpg95.Rules;

namespace BreezySRPG95.Runtime;

/// <summary>
/// The player's army as SRPGEXEC.EXE changes it (FORMAT_NOTES §16): event commands act on the roster and on the
/// units of that id already on the map, and a map's end copies the army back into the roster.
/// </summary>
public static class Party
{
    public const int MaxRoster = 50;

    /// <summary>A roster entry straight from UNIT.DAT (FUN_004244d8's fallback: equipment as listed, 出撃 off).</summary>
    public static RosterEntry Fresh(Database db, int unit)
    {
        var u = db.Units[unit];
        return new RosterEntry(unit, u.ClassId, u.Level, u.Exp, (int[])u.BaseStats.Clone(), u.Weapon, u.Armor, u.Accessory) { Sortie = false };
    }

    public static RosterEntry FromUnit(BattleUnit u) =>
        new(u.Unit, u.ClassId, u.Level, u.Exp, (int[])u.Base.Clone(), u.Weapon, u.Armor, u.Accessory) { Sortie = true };

    /// <summary>The roster entry at <paramref name="slot"/> gone, the later ones moving up, as the map's units' slots do.</summary>
    static void RemoveAt(GameState s, Battlefield? f, int slot)
    {
        s.Roster.RemoveAt(slot);
        foreach (var u in f?.Units ?? Enumerable.Empty<BattleUnit>())
            if (u.RosterSlot == slot) u.RosterSlot = -1;
            else if (u.RosterSlot > slot) u.RosterSlot--;
    }

    /// <summary>Units of this id on the map now (dead ones included until they're taken off, despawned ones not).</summary>
    static IEnumerable<BattleUnit> OnMap(Battlefield? f, int unit) =>
        f is null ? Enumerable.Empty<BattleUnit>() : f.Units.Where(u => u.Unit == unit && !u.Hidden).OrderBy(u => u.Slot).ToList();

    /// <summary>
    /// "Add ally" (FUN_004244d8): every unit of this id on the map that the computer controls (an enemy or a neutral
    /// unit) changes sides; the first one joins the roster as it is now (出撃 on, no longer a guest, at the roster's end)
    /// unless the unit is in the roster already. The others hold no entry: at the map's end they join as new ones. If
    /// none was on the map and it isn't in the roster, it joins from UNIT.DAT with 出撃 off.
    /// </summary>
    public static void Add(GameState s, Database db, Battlefield? f, int unit)
    {
        if (unit < 0 || unit >= db.Units.Length) return;
        bool done = s.Roster.Any(e => e.Unit == unit);
        foreach (var u in OnMap(f, unit).Where(u => u.Side != Side.Ally && !u.Cleared))
        {
            u.Side = Side.Ally;
            if (done || s.Roster.Count >= MaxRoster) continue;
            u.Guest = false;
            u.RosterSlot = s.Roster.Count;
            s.Roster.Add(FromUnit(u));
            done = true;
        }
        if (!done && s.Roster.Count < MaxRoster) s.Roster.Add(Fresh(db, unit));
    }

    /// <summary>"Remove ally" (FUN_00424790): the first roster entry of this id goes; units of this id the player controls on the map turn neutral.</summary>
    public static void Remove(GameState s, Battlefield? f, int unit)
    {
        if (s.Roster.FindIndex(e => e.Unit == unit) is var slot and >= 0) RemoveAt(s, f, slot);
        foreach (var u in OnMap(f, unit).Where(u => u.Side == Side.Ally)) u.Side = Side.Neutral;
    }

    /// <summary>
    /// "Change class" (FUN_00424870 → FUN_0040bb0c): every roster entry and every unit of this id on the map get the class,
    /// Str/Mag/Skl/Sta move by the classes' initial-stat difference (1–30), and equipment the new class can't use goes
    /// back to the inventory (once per record: a unit both in the roster and on the map returns it twice, as the exe does).
    /// The level stays; a class-change item instead restarts it at 1 (<see cref="UseClassChangeItem"/>).
    /// </summary>
    public static void ChangeClass(GameState s, Database db, Battlefield? f, int unit, int cls)
    {
        if (cls < 0 || cls >= db.Classes.Length) return;
        for (int i = 0; i < s.Roster.Count; i++)
            if (s.Roster[i].Unit == unit) s.Roster[i] = ClassChanged(s, db, s.Roster[i], cls, keepLevel: true);
        foreach (var u in OnMap(f, unit))
        {
            var from = db.Classes[Math.Clamp(u.ClassId, 0, db.Classes.Length - 1)];
            var b = UnitChanges.ClassChangeStats(u.Base, from, db.Classes[cls]);
            Array.Copy(b, u.Base, 6);
            u.ClassId = cls;
            u.Weapon = Keep(s, db, u.Weapon, cls);
            u.Armor = Keep(s, db, u.Armor, cls);
            u.Accessory = Keep(s, db, u.Accessory, cls);
        }
    }

    static RosterEntry ClassChanged(GameState s, Database db, RosterEntry e, int cls, bool keepLevel)
    {
        var from = db.Classes[Math.Clamp(e.ClassId, 0, db.Classes.Length - 1)];
        return e with
        {
            ClassId = cls, Level = keepLevel ? e.Level : 1, Base = UnitChanges.ClassChangeStats(e.Base, from, db.Classes[cls]),
            Weapon = Keep(s, db, e.Weapon, cls), Armor = Keep(s, db, e.Armor, cls), Accessory = Keep(s, db, e.Accessory, cls),
        };
    }

    /// <summary>Keeps an equipped item the class can use; otherwise it goes to the inventory (FUN_0040b038).</summary>
    static int Keep(GameState s, Database db, int item, int cls)
    {
        if (item < 0 || item >= db.Items.Length || UnitChanges.Usable(db.Items[item], cls)) return item;
        s.AddItem(item, 1);
        return -1;
    }

    /// <summary>
    /// "Change equipment" (FUN_0042490c → FUN_0040afe0): the item goes into its type's slot on every roster entry and on
    /// every unit of this id on the map; whatever was there before goes to the inventory (once per record). The new
    /// item doesn't come out of the inventory.
    /// </summary>
    public static void ChangeEquipment(GameState s, Database db, Battlefield? f, int unit, int item)
    {
        if (item < 0 || item >= db.Items.Length) return;
        int slot = UnitChanges.Slot(db.Items[item]);
        if (slot < 0) return;
        int Swap(int old) { if (old != item && old >= 0) s.AddItem(old, 1); return item; }
        for (int i = 0; i < s.Roster.Count; i++)
        {
            var e = s.Roster[i];
            if (e.Unit != unit) continue;
            s.Roster[i] = slot switch
            {
                0 => e.Weapon == item ? e : e with { Weapon = Swap(e.Weapon) },
                1 => e.Armor == item ? e : e with { Armor = Swap(e.Armor) },
                _ => e.Accessory == item ? e : e with { Accessory = Swap(e.Accessory) },
            };
        }
        foreach (var u in OnMap(f, unit))
        {
            if (slot == 0 && u.Weapon != item) u.Weapon = Swap(u.Weapon);
            if (slot == 1 && u.Armor != item) u.Armor = Swap(u.Armor);
            if (slot == 2 && u.Accessory != item) u.Accessory = Swap(u.Accessory);
        }
    }

    /// <summary>
    /// A class-change item (FUN_0040be00 → FUN_0040bb0c): the matching (from → to) pair's class, the level back to 1,
    /// the initial-stat difference, unusable equipment to the inventory, for the roster entry at <paramref name="slot"/>.
    /// Returns the new class, or −1.
    /// </summary>
    public static int UseClassChangeItem(GameState s, Database db, int slot, Item it)
    {
        if (slot < 0 || slot >= s.Roster.Count) return -1;
        var e = s.Roster[slot];
        int to = UnitChanges.ClassChangeTarget(it, e.ClassId);
        if (to < 0 || to >= db.Classes.Length) return -1;
        s.Roster[slot] = ClassChanged(s, db, e, to, keepLevel: false);
        return to;
    }

    /// <summary>
    /// A map's end (FUN_0041d5e4): every player unit on the map that isn't a guest or neutral (fallen ones too), in record
    /// order, is copied into the roster — into the entry it holds (<see cref="BattleUnit.RosterSlot"/>), else as a new
    /// one at the end (none once there are 50) — with 出撃 on; HP, MP and statuses come back in full. Then the roster is
    /// sorted by unit id (entries of one id keeping their order). When the game has fallen units lost (GAME.DAT [5]), a
    /// fallen unit holding an entry first has it deleted, and a fallen one without doesn't join.
    /// </summary>
    public static void SaveFromMap(GameState s, Battlefield f)
    {
        var players = f.Units.Where(u => !u.Hidden && u.Side == Side.Ally && !u.Guest).OrderBy(u => u.Slot).ToList();
        if (f.Db.FallenLost)
            foreach (var u in players.Where(u => u.Dead && u.FromRoster).ToList())
            {
                RemoveAt(s, f, u.RosterSlot);
                players.Remove(u);
            }
        foreach (var u in players)
        {
            if (u.FromRoster && u.RosterSlot < s.Roster.Count) { s.Roster[u.RosterSlot] = FromUnit(u); continue; }
            if (s.Roster.Count >= MaxRoster) break;
            if (f.Db.FallenLost && u.Dead) continue;
            s.Roster.Add(FromUnit(u));
        }
        var sorted = s.Roster.OrderBy(e => e.Unit).ToList();   // stable: one id's entries keep their order
        s.Roster.Clear();
        s.Roster.AddRange(sorted);
        foreach (var u in f.Units) u.RosterSlot = -1;   // the entries have moved: the map is over
    }

    /// <summary>
    /// Entering a camp (FUN_0041d5e4): the next map's character list must sortie; then only as many as it has start
    /// positions keep 出撃, in roster order, the required ones always.
    /// </summary>
    public static void PrepareSortie(GameState s, MapData next)
    {
        int n = 0;
        for (int i = 0; i < s.Roster.Count; i++)
        {
            var e = s.Roster[i];
            bool forced = next.CharacterList.Contains(e.Unit);
            bool on = e.Sortie || forced;
            if (on && n < next.StartPositions.Length) n++;
            else if (on && !forced) on = false;
            if (on != e.Sortie) s.Roster[i] = e with { Sortie = on };
        }
    }
}
