namespace BreezySRPG95.Runtime;

/// <summary>
/// A save made in the middle of an event (modern; BreezySRPG95's own). SRPG Maker 95 saves only between events, from the
/// turn menu or the camp, and SAVEn.DAT has no room for a script half run: the map as it stands goes into SAVEn.DAT as
/// an ordinary battle save, and the rest is kept here, in the save's note beside it (<see cref="SaveExtras"/>) — where
/// the event stands, what the screen shows, and what the map does once the event is over. Running effects, moves and
/// pans are jumped to their ends before saving, so none of them is half done in here.
/// </summary>
public sealed record SceneSave
{
    public int Version { get; init; } = 1;
    public required RunnerSave Event { get; init; }
    public required MapSceneSave Map { get; init; }
    /// <summary>The camera in pixels (the battle save keeps whole tiles).</summary>
    public int CameraX { get; init; }
    public int CameraY { get; init; }
    /// <summary>The screen's brightness: 100, or 0 / 200 while an erase holds it black / white.</summary>
    public int Brightness { get; init; } = 100;
    /// <summary>The pictures shown, oldest first.</summary>
    public List<PictureSave> Pictures { get; init; } = new();
    /// <summary>The music playing, "" for none.</summary>
    public string Bgm { get; init; } = "";
    public int TickMs { get; init; } = 30;
}

public sealed record PictureSave(string File, int Position, bool Transparent);

/// <summary>
/// An event's progress (<see cref="EventRunner"/>): its script, and its stack of blocks from the script's top level
/// up, each as [which block, the next command in it] — a block being a branch's body (−1) or a choice's case (its
/// number) of the command before the next one in the block below. <paramref name="Pages"/>: a translated message's windows
/// still to come, a character choice after the last of them if <paramref name="ChoiceAfterPages"/>.
/// </summary>
public sealed record RunnerSave(string Script, List<int[]> Frames, int Face, bool FaceRight, bool FaceFlip,
    MessageView? Message, MessageView? LastMessage, bool Choice, int ChoiceIndex, double WaitMs, bool WaitButton,
    List<MessageView>? Pages = null, bool ChoiceAfterPages = false);

/// <summary>
/// What the map does once the event is over, and what of the map a battle save has no room for (<see
/// cref="BattleController"/>). Units are named by <c>r</c> and their record number, or <c>o</c> and their map object.
/// </summary>
/// <param name="Opening">the map's opening: the first turn follows</param>
/// <param name="Fight">the fight the event came before ("before" events): it follows as it was rolled</param>
/// <param name="AfterFight">the fight's next "after" events follow (<paramref name="AfterAttacker"/>, <paramref name="AfterOthers"/>)</param>
/// <param name="AiSlot">the computer's phase: the record number it has come to (0 in older saves: it goes on from the first)</param>
public sealed record MapSceneSave(bool Opening, FightSave? Fight, bool AfterFight, string? Actor, string? AfterAttacker,
    List<string>? AfterOthers, int Group, Dictionary<string, string> PassMarks, Dictionary<string, string> StopMarks,
    OverlaySave? Action, int CursorX, int CursorY, List<UnitSceneSave> Units, int AiSlot = 0);

/// <summary>A unit's facing, its killer (a fallen one) and the steps a move command gave it that haven't run yet.</summary>
public sealed record UnitSceneSave(string Unit, int Facing, int KilledBy, int[] Pending);

/// <summary>A fight waiting for its "before" events: an attack on <paramref name="Target"/>, or spell <paramref name="Spell"/> (≥ 0).</summary>
public sealed record FightSave(string Actor, string? Target, int Spell, ResultSave Result);

/// <summary>A fight as it was rolled (Srpg95.Rules.ExchangeResult).</summary>
public sealed record ResultSave(List<StrikeSave> Strikes, List<EffectSave> Effects, Dictionary<string, int> Exp,
    Dictionary<string, int[]> LevelUps, int RewardGold, List<int> RewardItems, Dictionary<string, int[]> Learned, int? MpAfter);

public sealed record StrikeSave(string Attacker, string Target, bool Hit, bool Critical, int Damage, bool Killed, int HpAfter, int Moved);
public sealed record EffectSave(string Target, int Kind, int Amount, bool Killed, int HpAfter, int Moved);

/// <summary>The tiles an action shows on the map until its scene begins: [x, y] each.</summary>
public sealed record OverlaySave(int[] Actor, int Colour, List<int[]> Area, List<int[]> Met);
