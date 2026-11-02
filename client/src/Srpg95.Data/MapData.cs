namespace Srpg95.Data;

// SMAP_nnn.DAT layout: re/FORMAT_NOTES.md §10-§11.

public enum ObjectType { Enemy, Ally, Neutral, Other }

public readonly record struct MovePattern(short Switch, short Action, short X, short Y, short SwitchToApply);

public readonly record struct EventSlot(short Condition, short Switch, short Turn, short TargetUnit)
{
    // 0 do-not-perform, 1 switch, 2 turn, 4 first contact (any time), 5 when unit dies,
    // 6 passes by, 7 stops on, 8 victory, 9 first contact (after move), 10/11 first attack (before/after)
    public bool IsEnemyTurn => Turn % 2 == 0;
    public int TurnNumber => (Turn + 1) / 2;
}

public sealed record MapObject(
    int Index, bool InUse, int X, int Y, ObjectType Type, short SwitchCondition, int Unit, int Graphic,
    MovePattern[] MovePatterns, bool Leader, bool Guest, int RewardItem, int RewardGold, EventSlot[] Slots, bool Unattackable)
{
    internal static MapObject Parse(int i, ReadOnlySpan<byte> r)
    {
        var mp = new MovePattern[4];
        for (int k = 0; k < 4; k++)
        {
            int o = 0x1c + k * 10;
            mp[k] = new(Bin.I16(r, o), Bin.I16(r, o + 2), Bin.I16(r, o + 4), Bin.I16(r, o + 6), Bin.I16(r, o + 8));
        }
        var sl = new EventSlot[5];
        for (int k = 0; k < 5; k++)
        {
            int o = 0x54 + k * 8;
            sl[k] = new(Bin.I16(r, o), Bin.I16(r, o + 2), Bin.I16(r, o + 4), Bin.I16(r, o + 6));
        }
        return new(i, Bin.I32(r, 0) != 0, Bin.I32(r, 4), Bin.I32(r, 8), (ObjectType)Bin.I32(r, 0xc), Bin.I16(r, 0x10),
            Bin.I32(r, 0x14), Bin.I32(r, 0x18), mp, Bin.I32(r, 0x44) != 0, Bin.I32(r, 0x48) != 0, Bin.I32(r, 0x4c),
            Bin.I32(r, 0x50), sl, Bin.I32(r, 0xa0) != 0);
    }

    public static string ScriptName(int objectIndex, int slot1Based) => $"EV{objectIndex:0000}{slot1Based:00}";
}

public sealed class MapData
{
    public const int MaxSize = 40;
    const int TilesOff = 0x4b0, ObjectSize = 164, ObjectCount = 100, FileCount = 5000, BlockSize = 100;

    public required string Name { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Tileset { get; init; }
    public required int WinCondition { get; init; }          // 0 defeat all, 1 leader, 2 event
    /// <summary>camp, player turn, enemy turn, player battle, enemy battle, boss battle, victory, game over</summary>
    public required string[] Bgm { get; init; }
    public required bool CampBeforeBattle { get; init; }
    public required int[] ShopItems { get; init; }
    public required (int X, int Y)[] StartPositions { get; init; }
    public required int[] CharacterList { get; init; }
    public required int CampBackground { get; init; }
    public required bool DisablePartyChanges { get; init; }
    /// <summary>
    /// [y, x] chip index into MAPC_tileset.BMP, the whole 40 × 40 grid the exe keeps (FUN_004048ac; FUN_0040c1d4 reads it
    /// with no bounds check): past the map's width and height it holds what the file stores there (0 in every shipped
    /// map), which units placed off the map stand on — some games hide the army there with start positions outside the
    /// map.
    /// </summary>
    public required ushort[,] Tiles { get; init; }
    public required MapObject[] Objects { get; init; }
    public required Dictionary<string, EventScript> Scripts { get; init; }

    public static MapData Load(string path)
    {
        var map = Parse(File.ReadAllBytes(path));
        foreach (var s in map.Scripts.Values) s.Map = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
        return map;
    }

    /// <summary>
    /// Reads the parts in the order the runtime's map loader does (FUN_004048ac): the 0x4b0-byte header, the tiles —
    /// the whole 40 × 40 grid when header +0x444 is set (every shipped map), else h rows of w chips — the 100 objects,
    /// then the script container (FUN_0040289c).
    /// </summary>
    /// <summary>Where the 100 object records start in an SMAP file: after the header and the tiles.</summary>
    public static int ObjectsOffset(byte[] d) =>
        TilesOff + (Bin.I32(d, 0x444) != 0 ? MaxSize * MaxSize : Bin.I32(d, 0x20) * Bin.I32(d, 0x24)) * 2;

    public static MapData Parse(byte[] d)
    {
        int w = Bin.I32(d, 0x20), h = Bin.I32(d, 0x24);
        bool whole = Bin.I32(d, 0x444) != 0;
        int rows = whole ? MaxSize : Math.Clamp(h, 0, MaxSize), cols = whole ? MaxSize : Math.Clamp(w, 0, MaxSize), stride = whole ? MaxSize : w;
        var tiles = new ushort[MaxSize, MaxSize];
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < cols; x++)
                tiles[y, x] = Bin.U16(d, TilesOff + (y * stride + x) * 2);

        int objOff = ObjectsOffset(d);
        var objects = new List<MapObject>();
        for (int i = 0; i < ObjectCount; i++)
        {
            var rec = d.AsSpan(objOff + i * ObjectSize, ObjectSize);
            if (rec.ContainsAnyExcept((byte)0)) objects.Add(MapObject.Parse(i, rec));
        }

        int nStart = Bin.I32(d, 0x348), nChars = Bin.I32(d, 0x43c), nShop = Bin.I32(d, 0x344);
        var starts = new (int, int)[Math.Clamp(nStart, 0, 20)];
        for (int i = 0; i < starts.Length; i++) starts[i] = (Bin.I32(d, 0x34c + i * 4), Bin.I32(d, 0x39c + i * 4));

        return new MapData
        {
            Name = Bin.Str(d, 0, 32),
            Width = w,
            Height = h,
            Tileset = Bin.I32(d, 0x28),
            WinCondition = Bin.I32(d, 0x2c),
            Bgm = Enumerable.Range(0, 8).Select(i => Bin.Str(d, 0x30 + i * 0x30, 0x30)).ToArray(),
            CampBeforeBattle = Bin.I32(d, 0x1b0) != 0,
            ShopItems = Bin.I32s(d, 0x1b4, Math.Clamp(nShop, 0, 100)),
            StartPositions = starts,
            CharacterList = Bin.I32s(d, 0x3ec, Math.Clamp(nChars, 0, 20)),
            CampBackground = Bin.I32(d, 0x440),
            DisablePartyChanges = Bin.I32(d, 0x450) != 0,
            Tiles = tiles,
            Objects = objects.ToArray(),
            Scripts = ReadScripts(d, objOff + ObjectCount * ObjectSize),
        };
    }

    /// <summary>
    /// The map's script container as FUN_0040289c loads it: a FAT of 5000 block links (−1 free, −2 end of chain), a
    /// 5000-entry directory (name, first block, size), then only the blocks in use, 100 bytes each, packed in block
    /// order — a map whose FAT has free holes (SMAP_056) keeps its later blocks earlier in the file. A file is its chain
    /// from the first block, cut to its size (FUN_0040262c); a block the file lacks reads as zeros.
    /// </summary>
    static Dictionary<string, EventScript> ReadScripts(byte[] d, int at)
    {
        int dirOff = at + FileCount * 4;
        var stored = new int[FileCount];
        for (int k = 0, o = dirOff + FileCount * 16; k < FileCount; k++)
        {
            stored[k] = Bin.I32(d, at + k * 4) != -1 ? o : -1;
            if (stored[k] >= 0) o += BlockSize;
        }

        var result = new Dictionary<string, EventScript>();
        for (int i = 0; i < FileCount; i++)
        {
            var e = d.AsSpan(dirOff + i * 16, 16);
            if (!e.ContainsAnyExcept((byte)0)) continue;
            string name = Bin.Str(e, 0, 8);
            var file = new byte[Math.Max(0, Bin.I32(e, 12))];
            for (int k = Bin.I32(e, 8), o = 0; k is >= 0 and < FileCount && o < file.Length; k = Bin.I32(d, at + k * 4), o += BlockSize)
            {
                int src = stored[k], n = Math.Min(BlockSize, file.Length - o);
                if (src >= 0 && src < d.Length) d.AsSpan(src, Math.Min(n, d.Length - src)).CopyTo(file.AsSpan(o));
            }
            result[name] = EventScript.Parse(name, file);
        }
        return result;
    }
}

public static class Chipset
{
    /// <summary>MAPC_nnn.DAT: GEOLOGY (terrain) id for each of the 384 chips of MAPC_nnn.BMP.</summary>
    public static byte[] ParseTerrain(byte[] data) =>
        data.Length == 384 ? data : throw new InvalidDataException($"MAPC: expected 384 bytes, got {data.Length}");

    public const int ChipSize = 32, HalfColumns = 6, HalfChips = 192;

    /// <summary>
    /// Chip number -> (column, row) in MAPC_nnn.BMP (12x32 chips). The sheet is two
    /// 6-column halves (the editor's "A" and "B" tabs): chips 0-191 run left-to-right,
    /// top-to-bottom through the left half, 192-383 through the right half.
    /// </summary>
    public static (int Col, int Row) ChipCell(int chip)
    {
        int half = chip / HalfChips, r = chip % HalfChips;
        return (half * HalfColumns + r % HalfColumns, r / HalfColumns);
    }
}
