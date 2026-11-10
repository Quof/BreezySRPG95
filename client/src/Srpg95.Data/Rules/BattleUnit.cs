using Srpg95.Data;

namespace Srpg95.Rules;

public enum Side { Ally, Enemy, Neutral }

/// <summary>A unit fighting on a battle map (state mirrors a save-file unit record, FORMAT_NOTES §14).</summary>
public sealed class BattleUnit
{
    public required int Unit { get; init; }
    public required Side Side { get; set; }
    /// <summary>SMAP object index (-1 = deployed from the roster onto a start position).</summary>
    public int MapObject { get; init; } = -1;
    /// <summary>
    /// The roster entry the unit holds (record [4], −1 for none): deployed from the army, or the first of its id to join
    /// it by an event (FUN_004244d8). At the map's end the unit goes back into that entry (others join as new ones); with
    /// GAME.DAT [5] the entry is deleted when the unit has fallen.
    /// </summary>
    public int RosterSlot { get; set; } = -1;
    public bool FromRoster => RosterSlot >= 0;
    public int ClassId { get; set; }
    public int Level { get; set; } = 1;
    public int Exp { get; set; }
    /// <summary>Max HP, max MP, Str, Mag, Skl, Sta without equipment.</summary>
    public int[] Base { get; init; } = new int[6];
    public int[] Growth { get; init; } = new int[6];
    public int Hp { get; set; }
    public int Mp { get; set; }
    public int Weapon { get; set; } = -1;
    public int Armor { get; set; } = -1;
    public int Accessory { get; set; } = -1;
    public int X { get; set; }
    public int Y { get; set; }
    public int Facing { get; set; }
    public bool Acted { get; set; }
    /// <summary>
    /// Record +0x30: an extra action would be wasted on it this phase — it gave one away itself (an extra-action spell's
    /// caster, FUN_0041ba0c), or as a computer unit it found nothing to do (FUN_0041589c). The AI's extra-action score
    /// passes it over (FUN_0041664c); cleared for every unit when a phase begins (FUN_00410b90).
    /// </summary>
    public bool NoMoreTurns { get; set; }
    public bool Dead { get; set; }
    /// <summary>The unit id of whoever defeated it (record [56] keeps the killer), for "dies" slots that name a target.</summary>
    public int KilledBy { get; set; } = -1;
    /// <summary>
    /// A fallen unit the event checkpoint has taken off the map (the exe clears record [1] only after the unit's
    /// own events had their chance, FUN_0040c24c): until then it still counts as present for event conditions.
    /// </summary>
    public bool Cleared { get; set; }
    /// <summary>Not on the map right now (its object's switch condition is OFF, or it hasn't appeared yet).</summary>
    public bool Hidden { get; set; }
    /// <summary>Has been on the map before (a unit that disappears and comes back starts afresh, FUN_00409658).</summary>
    public bool Appeared { get; set; }
    /// <summary>The unit's record number on the map (1–99): the order the exe walks units in. 0 while not on the map.</summary>
    public int Slot { get; set; }
    /// <summary>Developer option (--place): stays on the map whatever its object's switch condition says.</summary>
    public bool ForceShown { get; set; }
    public bool Leader { get; init; }
    public bool Guest { get; set; }
    public bool Unattackable { get; init; }
    public int PowerUp { get; set; }
    public int DefUp { get; set; }
    public int MdefUp { get; set; }
    public int PoisonTurns { get; set; }
    public int SleepTurns { get; set; }
    public MovePattern[] MovePatterns { get; init; } = Array.Empty<MovePattern>();
    public string Name { get; init; } = "";

    public int MaxHp => Base[0];
    public int MaxMp => Base[1];
}

/// <summary>Derived values for a unit, computed against the game databases.</summary>
public readonly record struct UnitStats(
    int Str, int Mag, int Skl, int Sta, int Movement, int MoveType,
    int Attack, int Speed, int Defense, int MagicDefense, int RangeMin, int RangeMax);
