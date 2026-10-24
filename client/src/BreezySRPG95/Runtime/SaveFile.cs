using System.Buffers.Binary;
using System.Text;
using Srpg95.Data;

namespace BreezySRPG95.Runtime;

/// <summary>
/// SAVEn.DAT, the original runtime's save format (FORMAT_NOTES §14; FUN_0040be6c writes it, FUN_0040bfbc reads it): a
/// 2000-byte header, 100 map-unit and 50 roster unit records of 640 bytes, and a copy of the map's object table.
/// Writing starts from the bytes of the slot's previous file when there is one, so fields the client doesn't
/// understand survive.
/// </summary>
public sealed class SaveFile
{
    public const int Size = 114_400, UnitOff = 0x7D0, UnitSize = 640, MapUnits = 100, RosterUnits = 50;
    const int RosterOff = UnitOff + MapUnits * UnitSize;   // 0x101D0

    public byte[] Raw { get; }

    static SaveFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    SaveFile(byte[] raw) => Raw = raw;

    /// <summary>Where SAVEn.DAT files live: the game folder (as the original), unless --saves points elsewhere.</summary>
    public static string? Directory { get; set; }

    /// <summary>A slot's file: SAVE{slot}.DAT, or (off Windows) the existing file whose name differs from it only in case.</summary>
    public static string PathFor(GameFolder game, int slot) => FileNames.In(Directory ?? game.Root, $"SAVE{slot}.DAT");

    public static SaveFile? Load(string path)
    {
        if (!File.Exists(path)) return null;
        byte[] raw;
        try { raw = ReadShared(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }   // locked or unreadable: as if not there
        return raw.Length == Size ? new SaveFile(raw) : null;
    }

    /// <summary>
    /// A file's bytes, read without stopping anyone writing, moving or deleting it meanwhile: the save list is read off the
    /// game thread (<see cref="SaveSlots"/>), and the game's own save mustn't fail because the list was reading that file.
    /// </summary>
    public static byte[] ReadShared(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[f.Length];
        f.ReadExactly(bytes);
        return bytes;
    }

    int I32(int off) => BinaryPrimitives.ReadInt32LittleEndian(Raw.AsSpan(off));
    void I32(int off, int v) => BinaryPrimitives.WriteInt32LittleEndian(Raw.AsSpan(off), v);

    /// <summary>1 when saved at a camp, 0 on a map (FUN_0040be6c writes whether the top state is the camp's).</summary>
    public bool AtCamp => I32(0x000) == 1;
    /// <summary>The map being played, or the camp's map, 0-based (header 0x004: what FUN_004048ac loads).</summary>
    public int MapIndex => I32(0x004);
    /// <summary>The map after it, 0-based (header 0x010): MapIndex + 1 unless a "Next Map" command changed it.</summary>
    public int NextMap => I32(0x010);
    /// <summary>The camera, in tiles: the view's top-left tile (header 0x008 / 0x00C; FUN_0040dee0 adds them to the pointer's tile, and a load keeps them as they are, FUN_0040e334).</summary>
    public (int X, int Y) CameraTile => (I32(0x008), I32(0x00C));
    public int Gold => I32(0x534);
    /// <summary>The music playing when the game was saved (header 0x53C, 48 bytes of Shift-JIS).</summary>
    public string Bgm => Encoding.GetEncoding(932).GetString(Raw, 0x53C, 48).Split('\0')[0];
    /// <summary>The screen tone when the game was saved, R / G / B / vividness in % (header 0x56C; FUN_0040bfbc puts it back).</summary>
    public int[] Tone => I32(0x56C) == 0 && I32(0x570) == 0 && I32(0x574) == 0 && I32(0x578) == 0
        ? new[] { 100, 100, 100, 100 } : new[] { I32(0x56C), I32(0x570), I32(0x574), I32(0x578) };

    /// <summary>Copies the save into the game state: switches, variables, inventory, gold and the roster.</summary>
    public void ApplyTo(GameState s)
    {
        for (int i = 0; i < GameState.SwitchCount; i++) s.Switches[i] = Raw[0x14 + i] != 0;
        Array.Clear(s.Variables);
        for (int i = 0; i < 250; i++) s.Variables[i] = BinaryPrimitives.ReadInt16LittleEndian(Raw.AsSpan(0x5DC + i * 2));
        s.Items.Clear();
        int count = Math.Clamp(I32(0x530), 0, 100);
        for (int i = 0; i < count; i++)
        {
            int item = I32(0x210 + i * 8), n = I32(0x214 + i * 8);
            if (item >= 0 && n > 0) s.AddItem(item, n);
        }
        s.Gold = Gold;
        s.MapIndex = MapIndex;
        s.NextMap = NextMap;
        s.Roster.Clear();
        for (int i = 0; i < RosterUnits; i++)
        {
            int off = RosterOff + i * UnitSize;
            if (I32(off) == 0) continue;
            int Rec(int k) => I32(off + k * 4);
            int unit = Rec(8);
            var bases = Enumerable.Range(20, 6).Select(Rec).ToArray();
            s.Roster[unit] = new RosterEntry(Rec(9), Rec(10), Rec(11), bases, Rec(40), Rec(41), Rec(42)) { Sortie = Rec(13) != 0 };
        }
        s.LastSave = (byte[])Raw.Clone();
    }

    /// <summary>
    /// A camp save of the current state, leading to map <paramref name="mapIndex"/> (0-based). Header fields and
    /// roster records the client knows are written; everything else comes from <paramref name="template"/>
    /// (the slot's old file, else the last save loaded, else zeros).
    /// </summary>
    /// <param name="tone">the screen's tone now (the exe writes the palette's, FUN_0040be6c); null keeps the template's (100 %)</param>
    public static SaveFile ForCamp(GameState s, Database db, int mapIndex, string bgm, byte[]? template, int? nextMap = null, int[]? tone = null)
    {
        var f = new SaveFile(template is { Length: Size } ? (byte[])template.Clone() : new byte[Size]);
        f.I32(0x000, 1);
        f.I32(0x004, mapIndex);
        f.I32(0x010, nextMap ?? (mapIndex + 1) % 100);
        for (int i = 0; i < GameState.SwitchCount; i++) f.Raw[0x14 + i] = (byte)(s.Switches[i] ? 1 : 0);
        Array.Clear(f.Raw, 0x210, 0x320);
        int n = 0;
        foreach (var (item, count) in s.Items.Where(kv => kv.Value > 0).Take(100))
        {
            f.I32(0x210 + n * 8, item);
            f.I32(0x214 + n * 8, count);
            n++;
        }
        f.I32(0x530, n);
        f.I32(0x534, s.Gold);
        f.I32(0x538, s.Roster.Count);
        var name = new byte[48];
        var enc = Encoding.GetEncoding(932).GetBytes(bgm);
        Array.Copy(enc, name, Math.Min(enc.Length, 47));
        name.CopyTo(f.Raw, 0x53C);
        if (tone is { Length: 4 }) for (int i = 0; i < 4; i++) f.I32(0x56C + i * 4, tone[i]);
        else if (f.I32(0x56C) == 0) for (int i = 0; i < 4; i++) f.I32(0x56C + i * 4, 100);   // screen tone 100 %
        for (int i = 0; i < 250; i++)
            BinaryPrimitives.WriteInt16LittleEndian(f.Raw.AsSpan(0x5DC + i * 2), (short)Math.Clamp(s.Variables[i], short.MinValue, short.MaxValue));

        // roster: keep each unit's old record if the template had it (unknown fields), then patch the known ones
        var old = new Dictionary<int, byte[]>();
        for (int i = 0; i < RosterUnits; i++)
        {
            int off = RosterOff + i * UnitSize;
            if (f.I32(off) != 0) old[f.I32(off + 32)] = f.Raw.AsSpan(off, UnitSize).ToArray();
        }
        Array.Clear(f.Raw, RosterOff, RosterUnits * UnitSize);
        int slot = 0;
        foreach (var (unit, e) in s.Roster.Take(RosterUnits))
        {
            int off = RosterOff + slot++ * UnitSize;
            bool had = old.TryGetValue(unit, out var rec);
            if (had) rec!.CopyTo(f.Raw, off);
            WriteUnit(f, off, db, unit, e, keepBattleState: had);
        }
        return f;
    }

    /// <summary>The roster record fields of §14 (stats with equipment, range, terrain costs), HP/MP full as at camp.</summary>
    /// <param name="keepBattleState">leave the last battle's leftovers alone: movement left [50] (SAVE5 has 0 there)</param>
    static void WriteUnit(SaveFile f, int off, Database db, int unit, RosterEntry e, bool keepBattleState)
    {
        void R(int k, int v) => f.I32(off + k * 4, v);
        var cls = db.Classes[Math.Clamp(e.ClassId, 0, db.Classes.Length - 1)];
        Item? It(int id) => id >= 0 && id < db.Items.Length ? db.Items[id] : null;
        var (w, a) = (It(e.Weapon), It(e.Armor));
        var (str, mag, skl, sta, moveBonus) = Combat.WithAccessory(e.Base[2], e.Base[3], e.Base[4], e.Base[5], It(e.Accessory));
        var (rmin, rmax) = w is { Type: ItemType.Weapon } && w.S.WpnRange is >= 0 and < 8 ? Item.WeaponRanges[w.S.WpnRange] : (1, 1);
        R(0, 1); R(1, 1); R(2, 0); R(3, 0); R(4, -1); R(7, -1); R(8, unit); R(9, e.ClassId); R(10, e.Level); R(11, e.Exp); R(13, e.Sortie ? 1 : 0);
        R(19, cls.Movement + moveBonus);
        for (int i = 0; i < 6; i++) R(20 + i, e.Base[i]);
        R(26, e.Base[0]); R(27, e.Base[1]); R(28, str); R(29, mag); R(30, skl); R(31, sta);
        R(32, Combat.Attack(str, w)); R(33, Combat.Speed(skl, w, a)); R(34, Combat.Defense(sta, a)); R(35, Combat.MagicDefense(mag, a));
        R(36, 0);
        R(40, e.Weapon); R(41, e.Armor); R(42, e.Accessory); R(43, rmin); R(44, rmax);
        if (!keepBattleState) R(50, cls.Movement + moveBonus);
        // [130..149] known spells (MAGIC ids, in class skill-list order), [150] their count: グレイ 8 (活性波),
        // ジュリア 1 (剣の舞) then 1, 2 (虎伏演舞) once she reached level 4 (all saves agree)
        var spells = new List<int>();
        for (int i = 0; i < cls.SkillLevels.Length && cls.SkillLevels[i] > 0; i++)
            if (cls.SkillLevels[i] <= e.Level) spells.Add(cls.SkillIds[i]);
        for (int i = 0; i < 20; i++) R(130 + i, i < spells.Count ? spells[i] : 0);
        R(150, Math.Min(20, spells.Count));
        int mt = Math.Clamp(cls.MoveType, 0, 2);
        for (int t = 0; t < 70; t++)
        {
            int ov = t < cls.TerrainMoveOverride.Length ? cls.TerrainMoveOverride[t] : -1;
            R(60 + t, ov >= 0 ? ov : t < db.Terrains.Length ? db.Terrains[t].MoveCost[mt] : 0);
        }
    }

    /// <summary>Writes the file; false (nothing written, the game goes on) when it can't be, e.g. while another program holds it.</summary>
    public bool Write(string path)
    {
        try { File.WriteAllBytes(path, Raw); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // ---------------- battle saves ----------------

    const int ObjectsOff = 0x17ED0, ObjectSize = 164, FiredOff = 124;

    /// <summary>Phase counter (ally turn t = 2t−1) at 0x20C.</summary>
    public int Phase => I32(0x20C);

    /// <summary>
    /// One map-unit record of a battle save (§14 fields): its record number, [3] computer, [14] guest, [15] neutral, [4] ≥ 0
    /// holds a roster entry, [36]–[38] the buffs, [45] / [46] the poison and sleep counters, [12] no more actions this phase.
    /// </summary>
    public sealed record SavedUnit(int Record, int Object, int Unit, int ClassId, int Level, int Exp, int[] Base, int Hp, int Mp,
        int Weapon, int Armor, int Accessory, int X, int Y, bool Acted, bool Alive, bool Computer, bool Guest, bool Neutral, bool FromRoster,
        int PowerUp = 0, int DefUp = 0, int MdefUp = 0, int PoisonTurns = 0, int SleepTurns = 0, bool NoMoreTurns = false);

    /// <summary>The map units of a battle save, in record order (records start at index 1).</summary>
    public IEnumerable<SavedUnit> MapUnitRecords()
    {
        for (int i = 0; i < MapUnits; i++)
        {
            int off = UnitOff + i * UnitSize;
            if (I32(off) == 0) continue;
            int R(int k) => I32(off + k * 4);
            yield return new SavedUnit(i, R(7), R(8), R(9), R(10), R(11), Enumerable.Range(20, 6).Select(R).ToArray(), R(26), R(27),
                R(40), R(41), R(42), R(5), R(6), R(2) != 0, R(1) != 0, R(3) != 0, R(14) != 0, R(15) != 0, R(4) >= 0,
                R(36), R(37), R(38), R(45), R(46), R(12) != 0);
        }
    }

    /// <summary>The object table copy: whether an object is still waiting to come onto the map, and where (in-use word, x, y).</summary>
    public (bool Pending, int X, int Y) ObjectState(int obj) =>
        (I32(ObjectsOff + obj * ObjectSize) != 0, I32(ObjectsOff + obj * ObjectSize + 4), I32(ObjectsOff + obj * ObjectSize + 8));

    /// <summary>Event slots that have fired: the object table copy keeps a 16-bit flag per slot at +124 + 2·(slot−1).</summary>
    public IEnumerable<(int Object, int Slot)> FiredSlots()
    {
        for (int o = 0; o < 100; o++)
            for (int s = 1; s <= 5; s++)
                if (BinaryPrimitives.ReadInt16LittleEndian(Raw.AsSpan(ObjectsOff + o * ObjectSize + FiredOff + (s - 1) * 2)) != 0)
                    yield return (o, s);
    }

    /// <summary>
    /// A battle save (header 0x000 = 0): the camp fields plus the phase, one record per unit on the map (units deployed
    /// from the army first, then map objects in order, from index 1 as the original does), and the map's object table
    /// with spawned objects' in-use word cleared and fired event slots flagged (both as in SAVE1/SAVE3).
    /// </summary>
    public static SaveFile ForBattle(GameState s, Database db, int mapIndex, string bgm, int phase, string mapPath,
        IEnumerable<Srpg95.Rules.BattleUnit> units, IEnumerable<(int Object, int Slot)> fired, byte[]? template, int[]? tone = null,
        (int X, int Y)? camera = null)
    {
        var f = ForCamp(s, db, mapIndex, bgm, template, s.NextMap, tone);
        f.I32(0x000, 0);
        if (camera is { } c) { f.I32(0x008, c.X); f.I32(0x00C, c.Y); }
        f.I32(0x20C, phase);
        Array.Clear(f.Raw, UnitOff, MapUnits * UnitSize);
        var all = units.ToList();
        var onMap = all.Where(u => !u.Hidden && u.Slot is > 0 and < MapUnits).OrderBy(u => u.Slot).ToList();
        var rosterOrder = s.Roster.Keys.ToList();
        foreach (var u in onMap)
        {
            // each unit at its own record number, as the exe keeps them
            int off = UnitOff + u.Slot * UnitSize;
            WriteUnit(f, off, db, u.Unit, new RosterEntry(u.ClassId, u.Level, u.Exp, u.Base, u.Weapon, u.Armor, u.Accessory), keepBattleState: false);
            void R(int k, int v) => f.I32(off + k * 4, v);
            R(1, u.Dead && u.Cleared ? 0 : 1); R(2, u.Acted ? 1 : 0); R(3, u.Side == Srpg95.Rules.Side.Enemy ? 1 : 0); R(12, u.NoMoreTurns ? 1 : 0);
            R(5, u.X); R(6, u.Y); R(7, u.MapObject); R(13, u.MapObject < 0 ? 1 : 0);
            R(14, u.Guest ? 1 : 0); R(15, u.Side == Srpg95.Rules.Side.Neutral ? 1 : 0); R(16, u.Unattackable ? 1 : 0);
            R(26, Math.Max(0, u.Hp)); R(27, u.Mp); R(36, u.PowerUp); R(37, u.DefUp); R(38, u.MdefUp); R(45, u.PoisonTurns); R(46, u.SleepTurns);
            R(54, u.X); R(55, u.Y);
            // [4]: the unit's roster index when it holds a roster entry (ルーティ 0, ジュナ 1 in SAVE3), else −1
            R(4, rosterOrder.IndexOf(u.Unit) is var ri && ri >= 0 && u.FromRoster ? ri : -1);
            // [50] movement left: computer units only get theirs back on their own phase (0 during the player's, SAVE3)
            if (u.Side != Srpg95.Rules.Side.Ally && phase % 2 == 1) R(50, 0);
        }
        // object table: the map's own; objects whose unit is on the map are consumed (in-use 0), and an object whose
        // unit left the map waits where the unit stood; fired slots flagged
        var smap = File.ReadAllBytes(mapPath);
        Array.Copy(smap, MapData.ObjectsOffset(smap), f.Raw, ObjectsOff, 100 * ObjectSize);
        foreach (var u in all.Where(u => u.MapObject is >= 0 and < 100))
        {
            int o = ObjectsOff + u.MapObject * ObjectSize;
            if (!u.Hidden) f.I32(o, 0);
            else if (u.Appeared) { f.I32(o + 4, u.X); f.I32(o + 8, u.Y); }
        }
        foreach (var (o, slot) in fired)
            if (o is >= 0 and < 100 && slot is >= 1 and <= 5)
                BinaryPrimitives.WriteInt16LittleEndian(f.Raw.AsSpan(ObjectsOff + o * ObjectSize + FiredOff + (slot - 1) * 2), 1);
        return f;
    }
}
