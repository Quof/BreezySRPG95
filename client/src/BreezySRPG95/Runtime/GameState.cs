namespace BreezySRPG95.Runtime;

/// <summary>Persistent game state: what a save file holds besides the map's own (<see cref="SaveFile"/>).</summary>
public sealed class GameState
{
    public const int SwitchCount = 500, VariableCount = 500;

    public bool[] Switches { get; } = new bool[SwitchCount];
    public int[] Variables { get; } = new int[VariableCount];
    public Inventory Items { get; } = new();
    public int Gold { get; set; }

    /// <summary>
    /// The player's army between maps (save-file roster, FORMAT_NOTES §14): up to 50 entries, as many of one unit id as
    /// the game puts in (three soldiers of one UNIT.DAT record are three entries). The original sorts it by unit id at
    /// every map's end (FUN_0041d5e4), so the camp and the deployment follow unit-id order; events add to its end.
    /// </summary>
    public List<RosterEntry> Roster { get; } = new();

    /// <summary>The map being played or the camp's map, 0-based (save header 0x004).</summary>
    public int MapIndex { get; set; }
    /// <summary>The map that comes next, 0-based (save header 0x010): MapIndex + 1 unless a "Next Map" command set it.</summary>
    public int NextMap { get; set; }
    /// <summary>Bytes of the last save loaded, the template for unknown fields when saving.</summary>
    public byte[]? LastSave { get; set; }
    /// <summary>
    /// Time played in this game, counted by BreezySRPG95 (SRPG Maker 95 keeps none): known from a new game on, or from a
    /// save that BreezySRPG95 noted (<see cref="SaveExtras"/>); unknown after loading any other save.
    /// </summary>
    public double PlaySeconds { get; set; }
    public bool PlayTimeKnown { get; set; }

    public int ItemCount(int item) => Items.TryGetValue(item, out var n) ? n : 0;

    /// <summary>A new game.</summary>
    public void Reset()
    {
        Array.Clear(Switches);
        Array.Clear(Variables);
        Items.Clear();
        Gold = 0;
        Roster.Clear();
        MapIndex = 0;
        NextMap = 1;
        LastSave = null;
        (PlaySeconds, PlayTimeKnown) = (0, true);
    }

    /// <summary>FUN_0040add8 / FUN_0040ae68: at most 99 of an item and 100 different items; −1 is no item.</summary>
    public void AddItem(int item, int count)
    {
        if (item < 0) return;
        if (!Items.ContainsKey(item) && count > 0 && Items.Count >= 100) return;
        int n = Math.Clamp(ItemCount(item) + count, 0, 99);
        if (n == 0) Items.Remove(item); else Items[item] = n;
    }
}

/// <summary>
/// The inventory as the exe keeps it (save header 0x210, FUN_0040add8 / FUN_0040ae68): (item, count) entries in the
/// order they came in; a count changes in place, a new item takes the end, and an item used up leaves its entry, the
/// ones after it moving up. Lists of items (battle, camp) show them in this order.
/// </summary>
public sealed class Inventory : IEnumerable<KeyValuePair<int, int>>
{
    readonly List<KeyValuePair<int, int>> _entries = new();

    public int Count => _entries.Count;
    public IEnumerable<int> Keys => _entries.Select(e => e.Key);

    public bool TryGetValue(int item, out int count)
    {
        int i = _entries.FindIndex(e => e.Key == item);
        count = i >= 0 ? _entries[i].Value : 0;
        return i >= 0;
    }

    public bool ContainsKey(int item) => _entries.Exists(e => e.Key == item);

    public int this[int item]
    {
        get => TryGetValue(item, out var n) ? n : throw new KeyNotFoundException($"item {item}");
        set
        {
            int i = _entries.FindIndex(e => e.Key == item);
            if (i >= 0) _entries[i] = new(item, value);
            else _entries.Add(new(item, value));
        }
    }

    public bool Remove(int item) => _entries.RemoveAll(e => e.Key == item) > 0;
    public void Clear() => _entries.Clear();

    public IEnumerator<KeyValuePair<int, int>> GetEnumerator() => _entries.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A party member's persistent state between maps: its UNIT.DAT record, class, level...</summary>
public sealed record RosterEntry(int Unit, int ClassId, int Level, int Exp, int[] Base, int Weapon, int Armor, int Accessory)
{
    /// <summary>出撃: goes onto the next map's start positions (record [13]).</summary>
    public bool Sortie { get; init; } = true;
}

/// <summary>Battle-map state the event conditions can look at.</summary>
public interface IMapContext
{
    /// <summary>Phase counter: ally turn t = 2t-1, enemy turn t = 2t (same encoding as the data).</summary>
    int Phase { get; }
    /// <summary>0 alive, 1 dead, 2 not present, 3 controlled by player, 4 controlled by computer.</summary>
    bool UnitState(int unit, int state);
    /// <summary>Scrolls (smoothly) until the tile is in the view's middle box; <see cref="Panning"/> while it moves.</summary>
    void ShowTile(int x, int y);
    bool Panning { get; }
    /// <summary>A pan under way ends at once where it was going (cutscene skip).</summary>
    void FinishPan();
    void QueueMove(int unit, IReadOnlyList<byte> steps);
    /// <summary>Starts all queued moves; <see cref="MovesRunning"/> while they are still animating.</summary>
    void ExecuteMoves();
    bool MovesRunning { get; }
    /// <summary>Puts every moving unit at the end of its moves at once (cutscene skip).</summary>
    void FinishMoves();
    /// <summary>A switch changed: units appear and leave by their objects' switch conditions (FUN_00409658).</summary>
    void SwitchesChanged();
    /// <summary>"Party possesses item" (FUN_00424e70): in the inventory, or equipped by a player-side unit on the map.</summary>
    bool PartyHas(int item);
    /// <summary>Event party commands, acting on the roster and on units of that id on the map (FORMAT_NOTES §16).</summary>
    void PartyAdd(int unit);
    void PartyRemove(int unit);
    void ChangeClass(int unit, int cls);
    void ChangeEquipment(int unit, int item);
}
