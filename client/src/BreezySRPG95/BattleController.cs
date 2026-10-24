using BreezySRPG95.Runtime;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Srpg95.Data;
using Srpg95.Rules;

namespace BreezySRPG95;

/// <summary>Per-frame input for the battle map.</summary>
public readonly record struct BattleInput(
    bool Confirm, bool Cancel, bool Up, bool Down, bool Left, bool Right,
    Point? MouseTile, bool MouseMoved, bool Click, bool RightClick, bool FastForward, bool Info = false, int Wheel = 0,
    int CycleUnits = 0);

public enum MapOutcome { None, Won, Lost, GameOver, ReturnToTitle }

/// <summary>
/// Battle-map flow: events (opening, turn, contact, attack, death, victory triggers), the
/// player phase (select, move, act), the enemy phase (AI), and win/lose, laid out and timed as
/// SRPGEXEC.EXE's map states (FUN_0040dee0) do it. The one-line popups (battle scenes off) and the
/// forecast and terrain panels are the client's own.
/// </summary>
public sealed class BattleController
{
    enum State { Events, Idle, Selected, Moving, ActionMenu, Targeting, Showing, TurnMenu, EnemyPhase, Status, Ended,
        SpellList, ItemList, Aiming, MedicineTarget, Scene, Files, EquipList, WholeMap, EndTurnConfirm, Leaving,
        EquipSlots, UseConfirm, Acting }

    readonly MapSession _s;
    readonly EventRunner _runner;
    readonly GameState _state;
    readonly Database _db;
    readonly Ui.Ui _ui;
    readonly Textures _tex;
    readonly HashSet<string> _fired = new();
    State _state_ = State.Events;
    Point _cursor;
    BattleUnit? _sel;
    (int X, int Y) _selFrom;
    Dictionary<(int X, int Y), (int Cost, (int X, int Y) From)>? _reach;
    /// <summary>The selected unit's movement as shown: <see cref="_reach"/> and the tiles of the units it could walk through.</summary>
    HashSet<(int X, int Y)>? _reachShown;
    /// <summary>A clicked enemy's attack range around its movement (modern): what it could hit from where it can stop, past <see cref="_reach"/>.</summary>
    List<(int X, int Y)>? _reachHits;
    List<BattleUnit> _targets = new();
    int _menuIndex;
    string[] _menu = Array.Empty<string>();
    bool[] _menuEnabled = Array.Empty<bool>();

    /// <summary>Moves the mouse pointer to a 640×480 point (FUN_00409d24); the game leaves it alone outside its window.</summary>
    public Action<Point>? WarpPointer { get; set; }
    /// <summary>
    /// Pointer places kept for later (FUN_00409cac / FUN_00409cd4), with the map cursor's tile for keys: [0] where the turn
    /// menu was opened (back there when it closes or the turn ends), [1] where a move ended (back there on 行動終了 and when
    /// the action menu is closed).
    /// </summary>
    readonly (Point Pointer, Point Tile)[] _pointerSlots = new (Point, Point)[2];
    /// <summary>+0x1c0: the attack's targets came from the action menu's 攻撃 (closing them goes back to that row).</summary>
    bool _attackFromMenu;
    /// <summary>A menu opened or came back: the pointer goes onto its row once the view is still (FUN_00413100, +0x51d5).</summary>
    bool _pointMenu;
    /// <summary>The spell list's row an aim came from (+0x1ac).</summary>
    int _spellRow;

    void SavePointer(int slot) => _pointerSlots[slot] = (MouseVirtual, _cursor);

    void RestorePointer(int slot)
    {
        var (pointer, tile) = _pointerSlots[slot];
        _cursor = tile;
        WarpPointer?.Invoke(pointer);
    }

    /// <summary>FUN_00417188: the pointer to the middle of the unit's tile (attacks, spells and items aim from there).</summary>
    void PointAt(BattleUnit u)
    {
        _cursor = new Point(u.X, u.Y);
        WarpPointer?.Invoke(_s.ScreenPos(u.X, u.Y, ViewportRect).ToPoint() + new Point(MapSession.Tile / 2, MapSession.Tile / 2));
    }

    /// <summary>The open menu's current row (its bar), for the pointer; null when no menu is open.</summary>
    Rectangle? MenuRow() => _state_ switch
    {
        State.ActionMenu or State.TurnMenu => MenuBarRect(MenuRect(), _menuIndex),
        State.SpellList or State.ItemList or State.EquipList or State.EquipSlots when _menu.Length > 0 => RowRect(_menuIndex),
        State.UseConfirm => _useMenu.Row(_useMenu.Index),
        State.EndTurnConfirm => _quit.Row(_quit.Index),
        State.Files when _saveMenu is null => _files.Row(_files.Index),
        _ => null,
    };
    /// <summary>
    /// A message panel on the map (FUN_00414b14 rewards at x 200, FUN_00414d40 level-up at x 140, FUN_004157a4 item
    /// results at x 160): green, <c>Units</c> × 12 px wide at y 64, a 16-px row per line plus one, waiting for a click.
    /// The client's own one-line messages (strike results with the battle scenes off) are centred and go by themselves;
    /// <c>Kill</c> is the unit that strike felled, kept on the map until its message goes.
    /// </summary>
    sealed record Panel(string[] Lines, int X, int Units, BattleUnit? Kill, Action? Sound, bool Auto);
    readonly Queue<Panel> _panels = new();
    Panel? _panel;
    int _panelFrames;
    AiPlan? _plan;
    BattleUnit? _statusUnit;
    /// <summary>The status window's page (modern): 0 the stats and equipment, 1 the abilities; the chosen ability and the list's first row shown.</summary>
    int _statusPage, _statusSkill, _statusTop;
    /// <summary>The status window's equipment box the status key picked (−1 none).</summary>
    int _statusItem = -1;

    public MapOutcome Outcome { get; private set; }
    /// <summary>One-line state summary (screenshot mode writes it next to the PNG).</summary>
    public string DebugState => $"{_state_} phase={_s.Phase} sel={_sel?.Name} at={_sel?.X},{_sel?.Y} cursor={_cursor.X},{_cursor.Y} outcome={Outcome} telop={_telop} events={_eventsRun} last={_runner.Name} [{_runner.DebugState}] plan={_plan?.Unit.Name}/{_planWalking} group={_group} ai={_aiSlot} pan={_s.Panning} cam={_s.Camera} moves={_s.MovesRunning} fired={string.Join(",", _fired)} scene={_scene?.DebugPhase}" +
        string.Concat(_s.Field.Units.Select(u => $"\n  {u.Side} {u.Name} ({u.X},{u.Y}) HP {u.Hp} Lv {u.Level} Exp {u.Exp} hidden={u.Hidden} run={_s.Visuals[u].Running.Count}/{_s.Visuals[u].StepFrames}/{_s.Visuals[u].Pending.Count}{(u.Dead ? " dead" : "")}"));
    public bool EventRunning => _state_ == State.Events && _runner.Running;
    public bool Busy => _telop is not null || _ending != MapOutcome.None
        || _state_ is State.Events or State.Moving or State.Showing or State.EnemyPhase or State.Scene or State.Leaving or State.Acting
        || (_s.Panning && _state_ is State.ActionMenu or State.Targeting);
    /// <summary>
    /// The frame's red arrows may show (FUN_00413768): the player's phase, the map idle, a unit selected, or a target or
    /// a spell's aim being chosen (states 2, 6, 0x11, 0x10) — not an item's aim (0xf).
    /// </summary>
    public bool ScrollArrows => EdgeScrolls && _state_ is not State.MedicineTarget && !(_state_ == State.Aiming && _aimItem is not null);
    /// <summary>
    /// The left button held on the frame scrolls the view (FUN_0040dee0): in the states of <see cref="ScrollArrows"/> and
    /// while a medicine's target or a magic item's aim is chosen (0xf).
    /// </summary>
    public bool EdgeScrolls => _s.Field.AllyPhase && _telop is null && _ending == MapOutcome.None
        && _state_ is State.Idle or State.Selected or State.Targeting or State.Aiming or State.MedicineTarget;
    /// <summary>
    /// The mouse pointer the exe draws (FUN_00409db8, mode 0x198): 0 the arrow, 1 the hourglass, 2 none. None during
    /// captions, battle scenes and the computer's phase; the hourglass while a message waits, a result shows or the
    /// whole map is up; the arrow otherwise (a choice needs it).
    /// </summary>
    public int PointerMode =>
        _telop is not null || _ending != MapOutcome.None || (_state_ == State.Scene && _scene?.OverMap != true) || _state_ == State.Leaving ? 2
        : _state_ == State.Events && _runner.Running ? (_runner.Message is not null ? 1 : _runner.Choice is not null ? 0 : 2)
        : _state_ is State.Showing or State.WholeMap ? 1
        : _s.Field.AllyPhase ? 0 : 2;
    /// <summary>
    /// The unit that shows its way on the map while chosen (FUN_00413918): the one selected or walking (states 6, 7) — in
    /// the player's phase only one of the player's.
    /// </summary>
    public BattleUnit? FacingUnit => _state_ is State.Selected or State.Moving && _sel is { } u && !(_s.Field.AllyPhase && u.Side != Side.Ally) ? u : null;
    /// <summary>Asks the game to play a sound effect (EFS wav).</summary>
    public Action<string>? PlaySound { get; set; }

    // ---------------- battle scenes ----------------

    readonly Queue<BattleScene> _scenes = new();
    BattleScene? _scene;
    public bool SceneShowing => _scene is not null;
    /// <summary>Cancel would do something now (for the CANCEL sound).</summary>
    public bool AcceptsCancel => _state_ is State.WholeMap or State.Selected or State.ActionMenu or State.Targeting or State.TurnMenu or State.Status
        or State.SpellList or State.ItemList or State.Aiming or State.MedicineTarget or State.Files or State.EquipList
        or State.EndTurnConfirm or State.EquipSlots or State.UseConfirm;
    /// <summary>A menu has keyboard focus (W/S move in it instead of the camera).</summary>
    public bool MenuOpen => _state_ is State.WholeMap or State.ActionMenu or State.TurnMenu or State.SpellList or State.ItemList or State.Files
        or State.EquipList or State.EndTurnConfirm or State.EquipSlots or State.UseConfirm;
    /// <summary>The wheel is the map's own, not the backlog's: a menu's, or the spell forecast's while a spell is aimed (FocusAim).</summary>
    public bool UsesWheel => MenuOpen || _state_ is State.Aiming or State.Status;
    /// <summary>A click would move things on: a battle scene's death message, or COMPLETE!! from its 60th tick.</summary>
    public bool WaitingForClick => _scene?.WaitingForClick == true || _state_ == State.Showing
        || (_telop == CompleteTelop && _telopTick >= 60 && _ending == MapOutcome.None);
    /// <summary>The message panel up now (the screenshot harness's "panel+N").</summary>
    public bool PanelShowing => _state_ == State.Showing && _panel is not null;
    /// <summary>Set to false to skip the battle scenes (popups only): the 戦闘演出 option's 簡易演出.</summary>
    public bool ShowScenes { get; set; } = true;
    /// <summary>Battle scenes only for a player-side unit's action — an ally's, a guest's or a neutral unit's (自軍ユニットのみ).</summary>
    public bool PlayerScenesOnly { get; set; }
    bool SceneFor(BattleUnit actor) => ShowScenes && !Quiet && !(PlayerScenesOnly && actor.Side == Side.Enemy);
    public bool FastForwardSkipsScenes { get; set; }

    /// <summary>
    /// Battle music, chosen at each step's start (FUN_00419574; the same file already playing goes on, FUN_0040931c): map
    /// BGM slot 5 when a leader of the computer's takes part in the step — a strike's two units, a cast's caster, a
    /// spell's caster and the unit it lands on (+0x6122, FUN_0041a9c4 / FUN_0041b6ec / FUN_0041ba0c) — else 3 in the
    /// player's phase and 4 in the computer's.
    /// </summary>
    void BattleMusic(BattleUnit actor, BattleUnit? other)
    {
        // combat music off (modern): the phase's music from the fight's start, as the original has it once the fight is
        // over — music an event put on (a reinforcement theme...) ends with the first fight as it does there, while the
        // phase's own goes on without a restart
        if (!CombatMusic) { PhaseMusic(); return; }
        static bool Boss(BattleUnit? u) => u is { Leader: true, Side: Side.Enemy };
        PlayMapBgm?.Invoke(Boss(actor) || Boss(other) ? 5 : _s.Field.AllyPhase ? 3 : 4);
    }

    /// <summary>The phase's music, map BGM slot 1 or 2, what a fight's scenes leave playing (the same file goes on).</summary>
    void PhaseMusic() => PlayMapBgm?.Invoke(_s.Field.AllyPhase ? 1 : 2);

    /// <summary>The attack or spell being shown, for its EXP once its scenes are over.</summary>
    ExchangeResult? _fight;

    void RunScenes()
    {
        if (_scenes.Count == 0) { AfterScenes(); return; }
        _scene = _scenes.Dequeue();
        _scene.PlaySound = PlaySound;
        _state_ = State.Scene;
    }

    /// <summary>
    /// The action's scenes are over, or it had none: the overlay goes and the actor has acted (FUN_00419574's end); then
    /// a player unit still standing gets the EXP rolled for it, its level-ups rolled now, and the rewards and level-up
    /// panels (FUN_0041093c).
    /// </summary>
    void AfterScenes()
    {
        _action = null;
        if (_fight is { } r)
        {
            _fight = null;
            if (_actor is not null) _actor.Acted = true;
            _s.Field.ApplyGrowth(r);
            ShowGrowth(r);
        }
        NextPopup();
    }

    void UpdateScene(BattleInput i)
    {
        // skipping (modern): cancel / right click, or fast-forward when that option is on
        if (i.Cancel || i.RightClick || (i.FastForward && FastForwardSkipsScenes)) _scene!.Skip();
        _scene!.Update(i.FastForward, i.Confirm || i.Click);
        if (!_scene.OverMap) _action = null;   // the overlays go when the scene begins (FUN_00410388 → FUN_00411050)
        // the steps set the palette's brightness (FUN_00419574); the tick is 33 ms, slowed by GAME.DAT [4] while an
        // animation plays, and the map stays at 33 ms afterwards (FUN_00408530): only a new map or camp brings back 30 ms
        if (_scene.Brightness is int b) _fx.SetBrightness(b);
        GameClock.SetTick(_scene.Animating ? 33 + 33 * _db.SceneSlowdown / 100 : 33);
        if (!_scene.Done) return;
        _scene = null;
        _fx.FadeIn(0, 20);   // the scene leaves the palette black with the fade-in flag: the map comes back 20 % a tick
        if (_scenes.Count > 0) { RunScenes(); return; }
        PhaseMusic();   // back to the phase music
        AfterScenes();
    }

    Dictionary<BattleUnit, int> HpSnapshot(IEnumerable<BattleUnit> units) => units.Distinct().ToDictionary(u => u, u => u.Hp);

    /// <summary>Asks the game to play one of the map's BGM slots (1 player turn, 2 enemy turn, 6 victory...).</summary>
    public Action<int>? PlayMapBgm { get; set; }

    // ---------------- captions (TELOP1-3.BMP) ----------------

    // TELOP1 "PLAYER TURN", TELOP2 "ENEMY TURN", TELOP3 "COMPLETE!!" (512x96, index 0 transparent), as FUN_0040e5a8 (turn
    // captions), FUN_0041083c (COMPLETE!!) and FUN_0041421c (drawing) run them. The caption's counter goes up once a game
    // tick from 0; the picture is drawn after each step at y 192, x 64 (centred), coming in from the right 96 px a tick
    // while the counter is under 8, over the screen frame. A turn caption leaves to the left at the same speed from 33 and
    // ends at 40; its music starts at 20, and clicks do nothing meanwhile. COMPLETE!! stays: the fanfare starts at 20, and
    // from 60 on the fanfare reaching its end, or a click, fades the screen out (5 % a tick); then the next map.
    string? _telop;
    public string? CurrentTelop => _telop;
    const string CompleteTelop = "TELOP3.BMP";
    int _telopTick, _telopFrame;
    /// <summary>The map's end waiting for the fade-out: Won after COMPLETE!!, Lost through map_complete "lose".</summary>
    MapOutcome _ending;
    /// <summary>The current BGM has played to its end at least once (HarmonyGetMusicLooping).</summary>
    public Func<bool>? MusicLooped { get; set; }

    // ---------------- saving and loading (turn menu) ----------------

    /// <summary>セーブする / ロードする from the turn menu; the game writes / reads the file.</summary>
    public Action<int>? SaveRequested { get; set; }
    public Func<int, bool>? LoadRequested { get; set; }
    public Func<int, bool>? SlotExists { get; set; }
    /// <summary>The turn menu's 環境設定 (modern): the game opens the options.</summary>
    public Action? OptionsRequested { get; set; }
    readonly Camp.MenuList _files = new() { ItemWidth = 60 };
    readonly Camp.MenuList _quit = new() { ItemWidth = 36 };
    bool _saving;
    /// <summary>The extended save / load screen (modern), when the options choose it over the original's file list (null).</summary>
    public Func<bool, Ui.SaveMenu?>? NewSaveMenu { get; set; }
    Ui.SaveMenu? _saveMenu;

    /// <summary>Event slots that have run (object, slot), for a battle save's fired flags.</summary>
    public IEnumerable<(int Object, int Slot)> FiredSlots =>
        _fired.Where(n => n.Length == 8 && n.StartsWith("EV")).Select(n => (int.Parse(n[2..6]), int.Parse(n[6..8])));

    /// <summary>Continue a restored battle: no opening, the saved events stay fired, straight into the phase.</summary>
    public void Resume(IEnumerable<(int Object, int Slot)> fired)
    {
        foreach (var (o, s) in fired) _fired.Add(MapObject.ScriptName(o, s));
        _opening = false;
        if (_s.FirstPlayerUnit() is { } first) _cursor = new Point(first.X, first.Y);
        (_group, _aiSlot) = (1, 0);
        _plan = null;
        _state_ = _s.Field.AllyPhase ? State.Idle : State.EnemyPhase;
    }

    // ---------------- saving in the middle of an event (modern, SceneSave) ----------------

    /// <summary>An event is running and nothing of the map's own is under way (a caption, a battle scene, a panel): it can be saved where it stands.</summary>
    public bool CanSaveScene => _state_ == State.Events && _runner.CanSave && Outcome == MapOutcome.None && _ending == MapOutcome.None
        && _telop is null && _scene is null && _scenes.Count == 0 && _panel is null && _panels.Count == 0;

    /// <summary>A unit as a save names it: its record number while it's on the map, else its map object (null for neither).</summary>
    static string? Ref(BattleUnit? u) => u is null ? null : u is { Hidden: false, Slot: > 0 } ? $"r{u.Slot}" : u.MapObject >= 0 ? $"o{u.MapObject}" : null;

    BattleUnit? Unref(string? r) => r is { Length: > 1 } && int.TryParse(r[1..], out int n)
        ? r[0] == 'r' ? _s.Field.Units.FirstOrDefault(u => !u.Hidden && u.Slot == n)
        : r[0] == 'o' ? _s.Field.Units.FirstOrDefault(u => u.MapObject == n) : null
        : null;

    /// <summary>
    /// The event and what follows it, once the runner and the screen have been settled; null when it can't be saved now.
    /// The map itself goes into the battle save written with it.
    /// </summary>
    public SceneSave? SaveScene()
    {
        if (!CanSaveScene || _runner.Save() is not { } ev) return null;
        FightSave? fight = null;
        if (_beforeFight is { } f && (fight = SaveFight(f)) is null) return null;
        static Dictionary<string, string> Marks(Dictionary<(int Obj, int Slot), BattleUnit> marks) =>
            marks.Where(kv => Ref(kv.Value) is not null).ToDictionary(kv => $"{kv.Key.Obj}.{kv.Key.Slot}", kv => Ref(kv.Value)!);
        var units = _s.Field.Units.Where(u => !u.Hidden && Ref(u) is not null).Select(u => (u, v: _s.Visuals[u]))
            .Where(x => x.v.Facing != 0 || x.v.Pending.Count > 0 || x.u.Dead)
            .Select(x => new UnitSceneSave(Ref(x.u)!, x.v.Facing, x.u.KilledBy, x.v.Pending.Select(b => (int)b).ToArray())).ToList();
        var overlay = _action is { } o
            ? new OverlaySave(new[] { o.Actor.X, o.Actor.Y }, o.Colour, o.Area.Select(t => new[] { t.X, t.Y }).ToList(), o.Met.Select(t => new[] { t.X, t.Y }).ToList())
            : null;
        var map = new MapSceneSave(_opening, fight, _afterFightNext, Ref(_actor), Ref(_after?.Attacker), _after?.Others.Select(Ref).OfType<string>().ToList(),
            _group, Marks(_passMarks), Marks(_stopMarks), overlay, _cursor.X, _cursor.Y, units, _aiSlot);
        return new SceneSave { Event = ev, Map = map, CameraX = _s.Camera.X, CameraY = _s.Camera.Y };
    }

    /// <summary>The fight a "before" event holds up, as rolled; null if one of its units can't be named.</summary>
    static FightSave? SaveFight(PendingFight f)
    {
        var r = f.Result;
        if (Ref(f.Actor) is not { } actor || (f.Spell is null && Ref(f.Target) is null)) return null;
        var strikes = new List<StrikeSave>();
        foreach (var s in r.Strikes)
        {
            if (Ref(s.Attacker) is not { } a || Ref(s.Target) is not { } t) return null;
            strikes.Add(new StrikeSave(a, t, s.Hit, s.Critical, s.Damage, s.Killed, s.HpAfter, s.Moved));
        }
        var effects = new List<EffectSave>();
        foreach (var e in r.Effects)
        {
            if (Ref(e.Target) is not { } t) return null;
            effects.Add(new EffectSave(t, (int)e.Kind, e.Amount, e.Killed, e.HpAfter, e.Moved));
        }
        static Dictionary<string, T> ByRef<T>(Dictionary<BattleUnit, T> d) => d.Where(kv => Ref(kv.Key) is not null).ToDictionary(kv => Ref(kv.Key)!, kv => kv.Value);
        return new FightSave(actor, Ref(f.Target), f.Spell?.Index ?? -1, new ResultSave(strikes, effects, ByRef(r.ExpGained), ByRef(r.LevelUps),
            r.RewardGold, r.RewardItems.ToList(), ByRef(r.Learned.ToDictionary(kv => kv.Key, kv => kv.Value.Select(m => m.Index).ToArray())), r.MpAfter));
    }

    PendingFight? LoadFight(FightSave f)
    {
        var target = Unref(f.Target);
        var spell = f.Spell >= 0 && f.Spell < _db.Magic.Length ? _db.Magic[f.Spell] : null;
        if (Unref(f.Actor) is not { } actor || (spell is null && target is null)) return null;
        var s = f.Result;
        var r = new ExchangeResult { RewardGold = s.RewardGold, MpAfter = s.MpAfter };
        foreach (var x in s.Strikes)
        {
            if (Unref(x.Attacker) is not { } a || Unref(x.Target) is not { } t) return null;
            r.Strikes.Add(new Strike(a, t, x.Hit, x.Critical, x.Damage, x.Killed, x.HpAfter, x.Moved));
        }
        foreach (var x in s.Effects)
        {
            if (Unref(x.Target) is not { } t) return null;
            r.Effects.Add(new Srpg95.Rules.Effect(t, (EffectKind)x.Kind, x.Amount, x.Killed, x.HpAfter, x.Moved));
        }
        foreach (var (k, v) in s.Exp) if (Unref(k) is { } u) r.ExpGained[u] = v;
        foreach (var (k, v) in s.LevelUps) if (Unref(k) is { } u) r.LevelUps[u] = v;
        foreach (var (k, v) in s.Learned)
            if (Unref(k) is { } u) r.Learned[u] = v.Where(i => i >= 0 && i < _db.Magic.Length).Select(i => _db.Magic[i]).ToList();
        r.RewardItems.AddRange(s.RewardItems);
        return new PendingFight(actor, target, spell, r);
    }

    /// <summary>
    /// Goes on with a restored battle (<see cref="Resume"/> first) in the middle of the event <paramref name="save"/> holds.
    /// Should its script no longer have the place it was saved at (the game's files changed since), the event starts
    /// over; false when the map has no such event at all.
    /// </summary>
    public bool ResumeScene(SceneSave save)
    {
        if (!_s.Map.Scripts.TryGetValue(save.Event.Script, out var sc)) return false;
        var m = save.Map;
        _opening = m.Opening;
        _beforeFight = m.Fight is { } f ? LoadFight(f) : null;
        _afterFightNext = m.AfterFight;
        _actor = Unref(m.Actor);
        _after = Unref(m.AfterAttacker) is { } attacker ? (attacker, (m.AfterOthers ?? new()).Select(Unref).OfType<BattleUnit>().ToList()) : null;
        (_group, _aiSlot) = (m.Group, m.AiSlot);
        void Marks(Dictionary<(int Obj, int Slot), BattleUnit> into, Dictionary<string, string> from)
        {
            into.Clear();
            foreach (var (key, unit) in from)
                if (key.Split('.') is [var o, var s] && int.TryParse(o, out int obj) && int.TryParse(s, out int slot) && Unref(unit) is { } u)
                    into[(obj, slot)] = u;
        }
        Marks(_passMarks, m.PassMarks);
        Marks(_stopMarks, m.StopMarks);
        _action = m.Action is { Actor.Length: 2 } a
            ? new ActionOverlay((a.Actor[0], a.Actor[1]), a.Colour, a.Area.Where(t => t.Length == 2).Select(t => (t[0], t[1])).ToHashSet(),
                a.Met.Where(t => t.Length == 2).Select(t => (t[0], t[1])).ToList())
            : null;
        _cursor = new Point(m.CursorX, m.CursorY);
        foreach (var us in m.Units)
            if (Unref(us.Unit) is { } u)
            {
                var v = _s.Visuals[u];
                v.Facing = us.Facing;
                if (u.Dead) u.KilledBy = us.KilledBy;
                v.Pending.Clear();
                foreach (var step in us.Pending) v.Pending.Enqueue((byte)step);
            }
        _s.Camera = new Point(save.CameraX, save.CameraY);
        _s.ClampCamera();
        _eventsRun++;
        _state_ = State.Events;
        if (!_runner.Restore(sc, save.Event)) _runner.Start(sc);
        return true;
    }

    /// <summary>
    /// セーブする / ロードする (FUN_00418bdc / FUN_00418edc): 「セーブします」 (「ロードします」), a rule and ファイル１–８ in a green
    /// panel (the larger of 5 and half the title's bytes, plus 2) × 12 px wide and 11 rows high, one tile left of the
    /// clicked tile at its top, inside the frame (FUN_0041855c); the files 60 px wide in the middle. Saving goes straight
    /// back to the turn menu, loading fades the map out first.
    /// </summary>
    void OpenFiles(bool save)
    {
        _saving = save;
        if (NewSaveMenu?.Invoke(save) is { } menu)
        {
            _saveMenu = menu;
            _state_ = State.Files;
            return;
        }
        string title = Word(save ? 53 : 54);   // セーブします / ロードします
        int w = (Math.Max(5, (Bin.Sjis.GetByteCount(title) + 1) / 2) + 2) * 12, h = 11 * 16;
        _files.Rect = new Rectangle(Math.Clamp(_turnMenuAt.X - 32, 16, 624 - w), Math.Clamp(_turnMenuAt.Y, 16, 464 - h), w, h);
        _files.ItemX = w / 2 - 30;
        _files.Header = new[] { title, "-" };
        _files.Set(Enumerable.Range(81, 8).Select(Word).ToArray(),   // ファイル１–８
            save ? null : Enumerable.Range(1, 8).Select(n => SlotExists?.Invoke(n) ?? false).ToArray());
        _files.Index = 0;
        _state_ = State.Files;
        _pointMenu = true;
    }

    /// <summary>FUN_00410728 / FUN_004107a0: after a save, or on a right click, back to the turn menu's セーブする / ロードする.</summary>
    void UpdateFiles(BattleInput i)
    {
        int pick;
        if (_saveMenu is { } menu)
        {
            var (result, slot) = menu.Update(i, MouseVirtual);
            if (result == Ui.SaveMenu.Result.None) return;
            menu.Dispose();
            _saveMenu = null;
            if (result == Ui.SaveMenu.Result.Cancelled) { ReopenTurnMenu(_saving ? 2 : 3); return; }
            pick = slot - 1;
        }
        else
        {
            if (i.Cancel || i.RightClick) { ReopenTurnMenu(_saving ? 2 : 3); return; }
            pick = _files.Navigate(i, MouseVirtual);
            if (pick < 0) return;
        }
        if (_saving) { SaveRequested?.Invoke(pick + 1); ReopenTurnMenu(2); }
        else Leave(() =>
        {
            if (LoadRequested?.Invoke(pick + 1) == true) return;
            _fx.FadeIn(0, 20);   // an unreadable file: back to the map
            _state_ = State.Idle;
        });
    }

    Action? _afterLeave;

    /// <summary>The turn menu's ロードする (FUN_00410728): the screen fades out first, 20 % a tick.</summary>
    void Leave(Action then)
    {
        _afterLeave = then;
        _fx.FadeOut(20);
        _state_ = State.Leaving;
    }

    Action? _afterTelop;

    /// <summary>PLAYER TURN (1) or ENEMY TURN (2), then <paramref name="then"/>.</summary>
    void TurnTelop(int n, Action then)
    {
        _telop = $"TELOP{n}.BMP";
        _telopTick = _telopFrame = 0;
        _afterTelop = then;
    }

    /// <summary>The caption's x at counter <paramref name="c"/> (FUN_0041421c).</summary>
    int TelopX(int c) => c < 8 ? (8 - c) * 96 + 64 : _telop != CompleteTelop && c > 32 ? 64 - (c * 96 - 3072) : 64;

    /// <summary>The caption's counter, one game tick every two frames; true while a caption holds the map.</summary>
    bool UpdateTelop(BattleInput i)
    {
        if (_telop is null) return false;
        bool complete = _telop == CompleteTelop;
        // COMPLETE!!: from tick 60 on, the fanfare's end or a click (FUN_0041083c)
        if (complete && _ending == MapOutcome.None && _telopTick >= 60 && (MusicLooped?.Invoke() == true || i.Click || i.Confirm))
        {
            _ending = MapOutcome.Won;
            _fx.FadeOut(5);
        }
        if (_telopFrame++ % 2 != 0) return true;
        int c = _telopTick;
        if (complete)
        {
            if (c == 20) PlayMapBgm?.Invoke(6);   // the victory fanfare
            if (c < 500) _telopTick++;
            return true;
        }
        if (c == 20) PlayMapBgm?.Invoke(_telop == "TELOP1.BMP" ? 1 : 2);   // the phase's music (FUN_0040e5a8)
        if (++_telopTick < 40) return true;
        _telop = null;
        var then = _afterTelop;
        _afterTelop = null;
        then?.Invoke();
        return true;
    }

    /// <summary>COMPLETE!! (FUN_0041083c): every unit's acted mark goes, the caption comes in, the fanfare from tick 20.</summary>
    void Win()
    {
        _state_ = State.Ended;
        foreach (var u in _s.Field.Units) u.Acted = false;
        _telop = CompleteTelop;
        _telopTick = _telopFrame = 0;
    }

    /// <summary>map_complete "lose": no caption; the screen fades out, 5 % a tick, then the next map (FUN_0040d18c).</summary>
    void Lose()
    {
        _state_ = State.Ended;
        _ending = MapOutcome.Lost;
        _fx.FadeOut(5);
    }

    readonly ScreenEffects _fx;

    public BattleController(MapSession s, EventRunner runner, GameState state, Database db, Ui.Ui ui, Textures tex, ScreenEffects fx)
        => (_s, _runner, _state, _db, _ui, _tex, _fx) = (s, runner, state, db, ui, tex, fx);

    string Word(int i) => i >= 0 && i < _db.Words.Length ? _db.Words[i] : "";

    // ---------------- events (FORMAT_NOTES §16: FUN_0040c24c and the triggers around it) ----------------
    //
    // One event at a time, as the original runs them: when one ends, the checkpoint looks again from the top, so what
    // an event changes decides what comes next. A fired flag per object slot keeps most kinds to a single run; switch,
    // pass-by and stop-on slots have none and run again whenever their condition holds again (the colosseum and
    // bonus-map menus, the 開発室 warp tiles rely on it).

    /// <summary>The unit whose action just ended: the checkpoint looks at its contacts first.</summary>
    BattleUnit? _actor;
    /// <summary>Pass-by / stop-on marks on "Other" objects' slots: the unit that went over or stopped on the tile.</summary>
    readonly Dictionary<(int Obj, int Slot), BattleUnit> _passMarks = new(), _stopMarks = new();
    /// <summary>What continues once the running event ends: a fight waiting on its "before" events...</summary>
    PendingFight? _beforeFight;
    /// <summary>...or the rest of a fight's "after" events (<see cref="_after"/>); otherwise the checkpoint.</summary>
    bool _afterFightNext;
    /// <summary>An attack on <paramref name="Target"/> or a cast of <paramref name="Spell"/>, as rolled.</summary>
    sealed record PendingFight(BattleUnit Actor, BattleUnit? Target, Magic? Spell, ExchangeResult Result);
    bool _opening;
    /// <summary>The fight whose "after" (cond 11) events are still to be looked at: the attacker and the opposing units it met.</summary>
    (BattleUnit Attacker, List<BattleUnit> Others)? _after;

    public void Start(string? firstEvent)
    {
        _opening = true;
        if (_s.Map.Scripts.TryGetValue(firstEvent ?? "OPENING", out var sc)) { StartScript(sc); return; }
        OpeningDone(ran: false);
    }

    /// <summary>
    /// After the opening (FUN_0040d18c's end in state 0): the camera goes to the first player unit, then the PLAYER TURN
    /// caption, and only then the checkpoint (turn-1 events). Without an opening the caption comes straight away.
    /// </summary>
    void OpeningDone(bool ran)
    {
        _opening = false;
        if (ran && _s.FirstPlayerUnit() is { } u) _s.ShowTile(u.X, u.Y);
        _state_ = State.Idle;
        TurnTelop(1, () => { if (!CheckPoint()) _state_ = State.Idle; });
    }

    int _eventsRun;

    void StartScript(EventScript sc)
    {
        _eventsRun++;
        _runner.Start(sc);
        _state_ = State.Events;
    }

    MapObject Obj(BattleUnit u) => _s.Map.Objects.First(x => x.Index == u.MapObject);

    bool Fired(MapObject o, int slot) => _fired.Contains(MapObject.ScriptName(o.Index, slot));

    /// <summary>FUN_0040cfe0: starts a slot's script; a slot without one doesn't count and the search goes on.</summary>
    bool StartSlot(MapObject o, int slot, bool once)
    {
        string name = MapObject.ScriptName(o.Index, slot);
        if (!_s.Map.Scripts.TryGetValue(name, out var sc)) return false;
        if (once) _fired.Add(name);
        StartScript(sc);
        return true;
    }

    bool SwitchOn(int sw) => sw >= 0 && sw < GameState.SwitchCount && _state.Switches[sw];

    /// <summary>
    /// FUN_0040c24c, the checkpoint: (1) contacts after movement (cond 9), the unit that just acted first, then every
    /// unit; (2) each shown "Other" object, by index; (3) each unit on the map, by record number, a fallen one being
    /// taken off once its own slots had their chance; then (4) lost? (5) won? (victory slots, then COMPLETE!!).
    /// Every object's slots are tried from 5 down to 1. Returns true when something took over.
    /// </summary>
    bool CheckPoint()
    {
        if (_actor is { } a && Contact(a, 9)) return true;
        foreach (var u in _s.InRecordOrder.ToList())
            if (Contact(u, 9)) return true;
        foreach (var o in _s.Others.Where(_s.OtherVisible).OrderBy(o => o.Index).ToList())
            if (SlotEvents(o, null)) return true;
        foreach (var u in _s.InRecordOrder.Where(u => !u.Cleared).ToList())
        {
            if (u.MapObject >= 0 && SlotEvents(Obj(u), u)) return true;
            if (u.Dead) u.Cleared = true;
        }
        if (_s.Field.Lost()) { End(MapOutcome.GameOver); return true; }
        if (!_s.Field.Won()) return false;
        foreach (var o in _s.Others.Where(_s.OtherVisible).OrderBy(o => o.Index).ToList())
            for (int slot = 5; slot >= 1; slot--)
                if (o.Slots[slot - 1].Condition == 8 && !Fired(o, slot) && StartSlot(o, slot, once: true)) return true;
        Win();
        return true;
    }

    /// <summary>
    /// FUN_0040c584: one object's slots, 5 down to 1. A unit's object first sets its Specific pattern's switch when the
    /// unit stands on the pattern's tile. Switch (1) while ON; turn (2) on its phase; contact any time (4) while the
    /// unit lives; death (5) once it has fallen; pass-by (6) / stop-on (7) on "Other" objects after a unit's move.
    /// </summary>
    bool SlotEvents(MapObject o, BattleUnit? u)
    {
        if (u is { Dead: false }) ApplySpecificSwitch(u);
        for (int slot = 5; slot >= 1; slot--)
        {
            var sl = o.Slots[slot - 1];
            if (sl.Condition == 0 || Fired(o, slot)) continue;
            switch (sl.Condition)
            {
                case 1:
                    if (SwitchOn(sl.Switch) && StartSlot(o, slot, once: false)) return true;
                    break;
                case 2:
                    if (sl.Turn == _s.Field.Phase && StartSlot(o, slot, once: true)) return true;
                    break;
                case 4:
                    if (u is { Dead: false } && InContact(u, sl.TargetUnit) && StartSlot(o, slot, once: true)) return true;
                    break;
                case 5:
                    if (u is { Dead: true } && (sl.TargetUnit < 0 || sl.TargetUnit == u.KilledBy) && StartSlot(o, slot, once: true)) return true;
                    break;
                case 6:
                case 7:
                    var marks = sl.Condition == 6 ? _passMarks : _stopMarks;
                    if (u is null && marks.TryGetValue((o.Index, slot), out var by) && MarkMatches(sl, by) && StartSlot(o, slot, once: false))
                    {
                        marks.Remove((o.Index, slot));
                        return true;
                    }
                    break;
            }
        }
        return false;
    }

    /// <summary>A pass-by / stop-on slot fires for its target unit, or (target "Nothing") for any player unit that isn't neutral.</summary>
    static bool MarkMatches(EventSlot sl, BattleUnit by) => sl.TargetUnit >= 0 ? by.Unit == sl.TargetUnit : by.Side == Side.Ally;

    /// <summary>A Specific move pattern's "switch to apply" turns ON while the unit stands on the pattern's tile.</summary>
    void ApplySpecificSwitch(BattleUnit u)
    {
        if (u.MovePatterns.Length == 0) return;
        var p = EnemyAi.ActivePattern(u, _state.Switches);
        if (p.Action != 2 || (u.X, u.Y) != (p.X, p.Y) || p.SwitchToApply < 0 || p.SwitchToApply >= GameState.SwitchCount) return;
        _state.Switches[p.SwitchToApply] = true;
        _s.RefreshSpawns();
    }

    /// <summary>
    /// FUN_0040cad8: a living unit's contact slots (4 any time / 9 after movement), 5 down to 1, fire when a unit stands
    /// next to it: its target unit, or (target "Nothing") a unit of the other side (allies and enemies against each
    /// other; a neutral unit and the player's units count as each other's other side too).
    /// </summary>
    bool Contact(BattleUnit owner, int cond)
    {
        if (owner.Dead || owner.Hidden || owner.MapObject < 0) return false;
        var o = Obj(owner);
        for (int slot = 5; slot >= 1; slot--)
        {
            var sl = o.Slots[slot - 1];
            if (sl.Condition == cond && !Fired(o, slot) && InContact(owner, sl.TargetUnit) && StartSlot(o, slot, once: true)) return true;
        }
        return false;
    }

    bool InContact(BattleUnit owner, int target) =>
        _s.Field.Alive.Any(v => v != owner && Adjacent(v, owner) && (target >= 0 ? v.Unit == target : Opposed(owner, v)));

    static bool Opposed(BattleUnit a, BattleUnit b)
    {
        bool ca = a.Side == Side.Enemy, cb = b.Side == Side.Enemy, na = a.Side == Side.Neutral, nb = b.Side == Side.Neutral;
        return (ca != cb && !na && !nb) || (nb && !na && !ca) || (na && !nb && !cb);
    }

    /// <summary>
    /// FUN_0040ecf0, a move's end (any side): "Other" objects on every tile walked onto get a pass-by mark, the one
    /// on the final tile a stop-on mark (staying put marks both); then contacts (cond 4), the mover first. A contact
    /// event ends the mover's action. Returns true if one started.
    /// </summary>
    bool MoveEnded(BattleUnit mover, IReadOnlyList<(int X, int Y)> path)
    {
        var tiles = path.Count > 0 ? path : new List<(int X, int Y)> { (mover.X, mover.Y) };
        foreach (var t in tiles)
            foreach (var o in _s.Others.Where(o => (o.X, o.Y) == t))
                for (int slot = 1; slot <= 5; slot++) _passMarks[(o.Index, slot)] = mover;
        foreach (var o in _s.Others.Where(o => (o.X, o.Y) == (mover.X, mover.Y)))
            for (int slot = 1; slot <= 5; slot++) _stopMarks[(o.Index, slot)] = mover;
        if (Contact(mover, 4) || _s.InRecordOrder.ToList().Any(u => Contact(u, 4)))
        {
            mover.Acted = true;
            return true;
        }
        return false;
    }

    /// <summary>The units a fight or spell meets on the other side, as the exe lists them for its first-attack slots (enemies vs. everyone else).</summary>
    static List<BattleUnit> OpposingOf(BattleUnit actor, IEnumerable<BattleUnit> met) =>
        met.Where(t => (t.Side == Side.Enemy) != (actor.Side == Side.Enemy)).Distinct().ToList();

    /// <summary>
    /// FUN_0040cd98: first-attack slots (10 before / 11 after) between the attacker and one unit it meets: the
    /// attacker's own slots first, then the other's; a slot's owner must still be alive.
    /// </summary>
    bool AttackTrigger(BattleUnit attacker, BattleUnit other, int cond)
    {
        bool Check(BattleUnit owner, BattleUnit with)
        {
            if (owner.Dead || owner.Hp <= 0 || owner.MapObject < 0) return false;
            var o = Obj(owner);
            for (int slot = 5; slot >= 1; slot--)
            {
                var sl = o.Slots[slot - 1];
                if (sl.Condition == cond && !Fired(o, slot) && (sl.TargetUnit < 0 || sl.TargetUnit == with.Unit) && StartSlot(o, slot, once: true)) return true;
            }
            return false;
        }
        return Check(attacker, other) || Check(other, attacker);
    }

    /// <summary>
    /// FUN_00410388: before a fight or spell, its "before" events one at a time; the fight follows straight after
    /// them (no checkpoint in between). Returns true if one started.
    /// </summary>
    bool BeforeFight(BattleUnit attacker, List<BattleUnit> met, PendingFight fight)
    {
        foreach (var t in met)
            if (AttackTrigger(attacker, t, 10)) { _beforeFight = fight; return true; }
        return false;
    }

    void Fight(PendingFight f)
    {
        if (f.Spell is { } m) DoCast(f.Actor, m, f.Result);
        else DoAttack(f.Actor, f.Target!, f.Result);
    }

    /// <summary>
    /// FUN_0041093c / FUN_00410900: after a fight's EXP, its "after" events one at a time, then the checkpoint.
    /// </summary>
    void AfterFight()
    {
        _action = null;
        if (_after is { } a)
            foreach (var t in a.Others)
                if (AttackTrigger(a.Attacker, t, 11)) { _afterFightNext = true; return; }
        _after = null;
        if (!CheckPoint()) Continue();
    }

    /// <summary>Back to whoever's phase it is once nothing else is going on.</summary>
    void Continue()
    {
        _sel = null;
        _state_ = _s.Field.AllyPhase ? State.Idle : State.EnemyPhase;
    }

    static bool Adjacent(BattleUnit a, BattleUnit b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) == 1;

    /// <summary>
    /// A map's end. Won: COMPLETE!! and the fanfare first. Lost through an event (map_complete "lose"): the original uses
    /// it to move on quietly — the same way to the next map, the army saved, without the caption.
    /// </summary>
    void End(MapOutcome o)
    {
        Outcome = o;
        _state_ = State.Ended;
        if (o is MapOutcome.Won or MapOutcome.Lost) _s.SaveRoster();
    }

    // ---------------- update ----------------

    public void Update(BattleInput input, EventInput ev)
    {
        UpdateState(input, ev);
        // a menu that opened or came back puts the pointer on its row once the view has stopped (FUN_00413100)
        if (_pointMenu && !_s.Panning)
        {
            _pointMenu = false;
            if (MenuRow() is { } row) WarpPointer?.Invoke(row.Center);
        }
    }

    void UpdateState(BattleInput input, EventInput ev)
    {
        if (input.MouseTile is { } mt && input.MouseMoved) _cursor = mt;
        // where the map is read, a click on the frame (outside the view) picks no tile (FUN_0040dee0 leaves +0x74 clear) —
        // but a spell that covers the whole map is cast by any click (FUN_00410050 doesn't look at +0x74)
        if (input.Click && input.MouseTile is null && _state_ is State.Idle or State.Selected or State.Targeting or State.Aiming or State.MedicineTarget
            && !(_state_ == State.Aiming && _aimSpell is { EffectWholeMap: true }))
            input = input with { Click = false, Confirm = false };
        // the action menu and the targets wait for the view to stop (FUN_00413100 / FUN_004144f4 idle while +0x79 is set)
        if (_s.Panning && _state_ is State.ActionMenu or State.Targeting) return;
        if (_ending != MapOutcome.None)
        {
            // the map's end: once the screen is dark, on to the next map (FUN_0041083c → FUN_0041d5e4)
            UpdateTelop(input);
            if (_fx.Dark) { var o = _ending; _ending = MapOutcome.None; End(o); }
            return;
        }
        if (UpdateTelop(input)) return;
        switch (_state_)
        {
            case State.Leaving:
                if (!_fx.Dark) break;
                var leave = _afterLeave;
                _afterLeave = null;
                leave?.Invoke();
                break;
            case State.EndTurnConfirm: UpdateEndTurnConfirm(input); break;
            case State.Events: UpdateEvents(ev); break;
            case State.Idle: UpdateIdle(input); break;
            case State.Selected: UpdateSelected(input); break;
            case State.Moving: if (!_s.MovesRunning) MoveDone(); break;
            case State.ActionMenu: UpdateActionMenu(input); break;
            case State.Targeting: UpdateTargeting(input); break;
            case State.Showing: UpdateShowing(input); break;
            case State.TurnMenu: UpdateTurnMenu(input); break;
            case State.EnemyPhase: UpdateEnemyPhase(input); break;
            case State.Status: UpdateStatus(input); break;
            case State.SpellList: UpdateSpellList(input); break;
            case State.ItemList: UpdateItemList(input); break;
            case State.EquipList: UpdateEquipList(input); break;
            case State.EquipSlots: UpdateEquipSlots(input); break;
            case State.UseConfirm: UpdateUseConfirm(input); break;
            case State.Aiming: UpdateAiming(input); break;
            case State.MedicineTarget: UpdateMedicineTarget(input); break;
            case State.Acting: UpdateActing(); break;
            case State.Scene: UpdateScene(input); break;
            case State.Files: UpdateFiles(input); break;
            // FUN_00410b28: a click (CLICK) or a right click (CANCEL, played by the game) goes back to the map
            case State.WholeMap:
                if (input.Confirm || input.Click) { Sfx.Click(); _state_ = State.Idle; }
                else if (input.Cancel || input.RightClick) _state_ = State.Idle;
                break;
        }
    }

    void UpdateEvents(EventInput ev)
    {
        if (_runner.Running)
        {
            _runner.Update(ev);
            if (_runner.Running) return;
        }
        // how the event ended counts wherever it ended: here, or all at once in the cutscene skip (EventRunner.Skip, from
        // the game's update) — a story map that ends in map_complete must go on to the next map either way
        switch (_runner.End)
        {
            case EventEnd.MapWon: Win(); return;
            case EventEnd.MapLost: Lose(); return;
            case EventEnd.GameOver: End(MapOutcome.GameOver); return;
            case EventEnd.ReturnToTitle: End(MapOutcome.ReturnToTitle); return;
        }
        // the event is over: the opening hands over to the first turn; a waiting fight or "after" check goes on;
        // otherwise the checkpoint looks again (FUN_0040d18c's end → state 2)
        if (_opening) { OpeningDone(ran: true); return; }
        if (_beforeFight is { } f) { _beforeFight = null; Fight(f); return; }
        if (_afterFightNext) { _afterFightNext = false; AfterFight(); return; }
        if (CheckPoint()) return;
        Continue();
    }

    void MoveCursor(BattleInput i)
    {
        int dx = (i.Right ? 1 : 0) - (i.Left ? 1 : 0), dy = (i.Down ? 1 : 0) - (i.Up ? 1 : 0);
        if (dx == 0 && dy == 0) return;
        _cursor = new Point(Math.Clamp(_cursor.X + dx, 0, _s.Map.Width - 1), Math.Clamp(_cursor.Y + dy, 0, _s.Map.Height - 1));
        // keep the cursor on screen
        var c = new Point(_cursor.X * MapSession.Tile, _cursor.Y * MapSession.Tile);
        if (c.X < _s.Camera.X) _s.Camera.X = c.X;
        if (c.Y < _s.Camera.Y) _s.Camera.Y = c.Y;
        if (c.X + MapSession.Tile > _s.Camera.X + BreezyGame.ViewWidth) _s.Camera.X = c.X + MapSession.Tile - BreezyGame.ViewWidth;
        if (c.Y + MapSession.Tile > _s.Camera.Y + BreezyGame.ViewHeight) _s.Camera.Y = c.Y + MapSession.Tile - BreezyGame.ViewHeight;
        _s.ClampCamera();
    }

    bool Pick(BattleInput i) => i.Confirm || i.Click;

    /// <summary>
    /// FUN_0040e658, the player's phase: a click on a unit that can act shows its movement range — the player's own
    /// to move it, anyone else's just to look (FUN_0040ebcc), an enemy's with its attack range around it (modern,
    /// <see cref="EnemyAi.Hits"/>) — while acted or sleeping units do nothing; a right
    /// click (or the info key) opens the status window; a click on an empty tile opens the turn menu (a right click there too, modern).
    /// </summary>
    /// <summary>
    /// The status window (FUN_00412448 shows it, a click or cancel closes it). Modern: ← / →, the wheel or the confirm key
    /// (not a click) turn to the other page; on the stats page the status key steps through the equipment's boxes, on the
    /// abilities page it (and ↑ ↓) steps through the abilities, as the pointer resting on one does.
    /// </summary>
    void UpdateStatus(BattleInput i)
    {
        if (i.Cancel || i.Click || i.RightClick) { _state_ = State.Idle; return; }
        if (i.Left || i.Right || i.Wheel != 0 || i.Confirm)
        {
            _statusPage = 1 - _statusPage;
            _statusItem = -1;
            Sfx.Click();
            return;
        }
        if (_statusPage == 0)
        {
            if (i.Info) { _statusItem = Ui.StatusWindow.NextShown(_statusUnit!, _statusItem); Sfx.Click(); }
            return;
        }
        int count = Ui.StatusWindow.Skills(_s.Field, _statusUnit!).Count;
        if (count == 0) return;
        int was = _statusSkill;
        if (i.Info || i.Down) _statusSkill = (_statusSkill + 1) % count;
        if (i.Up) _statusSkill = (_statusSkill + count - 1) % count;
        if (i.MouseMoved && Ui.StatusWindow.SkillRowAt(Ui.StatusWindow.Default, MouseVirtual) is var row and >= 0 && _statusTop + row < count)
            _statusSkill = _statusTop + row;
        if (_statusSkill != was && (i.Info || i.Down || i.Up)) Sfx.Click();
        _statusTop = Math.Clamp(_statusTop, Math.Max(0, _statusSkill - Ui.StatusWindow.SkillRows + 1), _statusSkill);
    }

    /// <summary>
    /// Every unit the player moves has acted this turn (an asleep one can't, and counts as done), and at least one did: an
    /// army that only slept, or a map without one, doesn't end turn after turn by itself.
    /// </summary>
    bool TurnIsOver()
    {
        var mine = _s.Field.Alive.Where(u => u.Side == Side.Ally).ToList();
        return mine.Any(u => u.Acted) && mine.All(u => u.Acted || u.SleepTurns > 0);
    }

    void UpdateIdle(BattleInput i)
    {
        // the auto end turn option: no ターン終了 question, straight to the computer's phase once the map is waiting again
        if (AutoEndTurn && _s.Field.AllyPhase && TurnIsOver()) { EndPlayerTurn(); return; }
        MoveCursor(i);
        if (i.CycleUnits != 0) CycleUnits(i.CycleUnits);
        var u = _s.Field.UnitAt(_cursor.X, _cursor.Y);
        if ((i.RightClick || i.Info) && u is not null) { _statusUnit = u; (_statusItem, _statusPage, _statusSkill, _statusTop) = (-1, 0, 0, 0); _state_ = State.Status; return; }
        // a right click on an empty tile opens the turn menu too (modern; the original does nothing there)
        if (i.RightClick && i.MouseTile is not null) { Sfx.Click(); OpenTurnMenu(); return; }
        if (!Pick(i)) return;
        Sfx.Click();
        if (u is null) { OpenTurnMenu(); return; }
        if (u.Acted || u.SleepTurns > 0) return;
        _sel = u;
        _selFrom = (u.X, u.Y);
        _reach = _s.Field.Reachable(u);
        _viewOnly = u.Side != Side.Ally;
        // shown as if its friends weren't there (modern): the blue takes in the tiles of units it could walk through
        // within its movement (the other side's still block its way, so what lies past them stays out), and around it the
        // red is what it could hit from where it can stop — its weapon's reach and its harmful spells' areas — whoever
        // stands there
        _reachShown = _s.Field.Flood(u, _s.Field.Stats(u).Movement).Keys.ToHashSet();
        _reachHits = EnemyAi.Hits(_s.Field, u, _reach.Keys).Where(t => !_reachShown.Contains(t)).ToList();
        _state_ = State.Selected;
    }

    /// <summary>
    /// The previous / next unit keys (modern): the cursor to the player's previous or next unit, in their order, that can
    /// still act this turn (not asleep), the view brought round to it as after the opening.
    /// </summary>
    void CycleUnits(int step)
    {
        var ready = _s.InRecordOrder.Where(u => !u.Dead && u.Side == Side.Ally && !u.Acted && u.SleepTurns == 0).ToList();
        if (ready.Count == 0) return;
        int at = ready.FindIndex(u => u.X == _cursor.X && u.Y == _cursor.Y);
        var next = ready[at < 0 ? (step > 0 ? 0 : ready.Count - 1) : ((at + step) % ready.Count + ready.Count) % ready.Count];
        _cursor = new Point(next.X, next.Y);
        _s.ShowTile(next.X, next.Y);
    }

    /// <summary>The range shown belongs to an enemy or neutral unit: any click closes it.</summary>
    bool _viewOnly;

    void UpdateSelected(BattleInput i)
    {
        MoveCursor(i);
        if (i.Cancel || i.RightClick) { _state_ = State.Idle; _sel = null; return; }
        if (!Pick(i) || _sel is null || _reach is null) return;
        if (_viewOnly) { _state_ = State.Idle; _sel = null; return; }
        var t = (_cursor.X, _cursor.Y);
        if (!_reach.ContainsKey(t)) { _state_ = State.Idle; _sel = null; return; }
        var path = Battlefield.PathTo(_reach, t);
        Sfx.Click();
        _s.Walk(_sel, path);
        _pendingPath = path;
        _state_ = State.Moving;
    }
    List<(int X, int Y)> _pendingPath = new();

    /// <summary>
    /// FUN_0040ecf0, the player's move is over: tile marks and contacts first (a contact event ends the unit's action);
    /// then the pointer's place is kept (slot 1), the view brings the unit into its middle box (FUN_00416fb0) and, with an
    /// enemy in the weapon's reach (FUN_00411708), its targets come up at once (FUN_00411620) — else the action menu.
    /// </summary>
    void MoveDone()
    {
        var u = _sel!;
        if (MoveEnded(u, _pendingPath)) { _actor = u; _sel = null; return; }
        SavePointer(1);
        _attackFromMenu = false;
        _s.ShowTile(u.X, u.Y);
        _targets = _s.Field.TargetsFrom(u, u.X, u.Y).ToList();
        if (_targets.Count > 0) { _forecastArmed = null; _state_ = State.Targeting; }
        else OpenActionMenu(OpeningRow);
    }

    /// <summary>Option (modern): the action menu opens on its first entry (攻撃) instead of 行動終了 (the original's).</summary>
    public bool ActionMenuFromTop { get; set; } = true;

    /// <summary>Where the pointer starts when the action menu first opens after a move: 行動終了 for a sleeping unit, which can do nothing else.</summary>
    int OpeningRow => ActionMenuFromTop && _sel is { SleepTurns: 0 } ? 0 : 4;

    /// <summary>
    /// The action menu (FUN_004171e0), the view first bringing the unit into its middle box, the pointer on
    /// <paramref name="row"/>: 行動終了 when it opens (攻撃 with the option for it), the entry it came back from otherwise. WORD.DAT: 16 攻撃, 17 必殺技,
    /// 89 装備, 18 アイテム, 15 行動終了 — the original's order is 行動終了 / 攻撃 / 必殺技 / 装備 / アイテム; 行動終了 is moved to
    /// the bottom here (player request). A sleeping unit can only end its action; otherwise 必殺技 needs a known spell and
    /// アイテム something in the inventory, while 攻撃 and 装備 are always there (攻撃 shows the weapon's reach even with
    /// nobody in it).
    /// </summary>
    void OpenActionMenu(int row)
    {
        var u = _sel!;
        _s.ShowTile(u.X, u.Y);
        _targets = _s.Field.TargetsFrom(u, u.X, u.Y).ToList();
        bool awake = u.SleepTurns == 0;
        _menu = new[] { Word(16), Word(17), Word(89), Word(18), Word(15) };
        _menuEnabled = new[] { awake, awake && _s.Field.KnownSpells(u).Any(), awake, awake && _state.Items.Count > 0, true };
        _menuIndex = row;
        _state_ = State.ActionMenu;
        _pointMenu = true;
    }

    // ---------------- magic, items and equipment ----------------

    List<Magic> _spells = new();
    List<Item> _items = new();
    Magic? _aimSpell;
    Item? _aimItem;
    HashSet<(int X, int Y)> _aimRange = new();
    /// <summary>The units the spell would reach from the aim now (<see cref="AimTargets"/>), and the one whose forecast shows, chosen at that aim.</summary>
    List<BattleUnit> _aimTargets = new();
    BattleUnit? _aimFocus;
    (int X, int Y)? _aimFocusAt;
    List<BattleUnit> _medTargets = new();
    HashSet<(int X, int Y)> _medRange = new();

    /// <summary>The inventory's items in its own order, the order the original's lists show them in.</summary>
    IEnumerable<Item> Inventory() =>
        _state.Items.Where(kv => kv.Value > 0 && kv.Key >= 0 && kv.Key < _db.Items.Length).Select(kv => _db.Items[kv.Key]);

    /// <summary>"%-ns": padded with spaces to <paramref name="bytes"/> Shift-JIS bytes (a space is half a character).</summary>
    static string Pad(string s, int bytes) => s + new string(' ', Math.Max(0, bytes - Bin.Sjis.GetByteCount(s)));

    /// <summary>An item row, 「%-22s×%2d」 (FUN_00418108, FUN_00417d9c).</summary>
    string ItemRow(Item it) => $"{Pad(it.Name, 22)}×{_state.ItemCount(it.Index),2}";

    /// <summary>
    /// A list by the acting unit (FUN_00417548 spells, FUN_004177e8 equipment slots, FUN_00417b08 equipment,
    /// FUN_00417f54 items, FUN_00418308 the use question): its corner <paramref name="dx"/> px from the unit's tile, at
    /// the tile's top, kept inside the frame (FUN_0041855c) — as <paramref name="placedW"/> wide when the exe widens the
    /// panel only after placing it — and, for the lists, not above y 96.
    /// </summary>
    Rectangle UnitPanel(int dx, int w, int h, bool list = true, int? placedW = null)
    {
        var t = _s.ScreenPos(_sel!.X, _sel.Y, ViewportRect).ToPoint();
        int x = Math.Max(16, Math.Min(t.X + dx, 624 - (placedW ?? w))), y = Math.Max(16, Math.Min(t.Y, 464 - h));
        return new Rectangle(x, list ? Math.Max(96, y) : y, w, h);
    }

    int _equipSlot;
    List<Item?> _equipRows = new();

    /// <summary>
    /// 装備 (FUN_004177e8): 武器 / 防具 / 装飾品 with what's held (「%-8s：」 and its name), 204 px wide, 64 px from the unit's
    /// tile; a slot is there if something is held in it or the inventory has something of its kind the class may use.
    /// </summary>
    void OpenEquipSlots()
    {
        var u = _sel!;
        int[] held = { u.Weapon, u.Armor, u.Accessory };
        _menu = new string[3];
        _menuEnabled = new bool[3];
        for (int s = 0; s < 3; s++)
        {
            string name = held[s] >= 0 && held[s] < _db.Items.Length ? _db.Items[held[s]].Name : "";
            _menu[s] = $"{Pad(Word(12 + s), 8)}：{name}";   // 武器 防具 装飾品
            _menuEnabled[s] = held[s] >= 0 || Inventory().Any(it => (int)it.Type == s + 1 && UnitChanges.Usable(it, u.ClassId));
        }
        _menuIndex = 0;
        _state_ = State.EquipSlots;
        _pointMenu = true;
    }

    /// <summary>A right click goes back to the action menu's 装備 (FUN_0040f4c4).</summary>
    void UpdateEquipSlots(BattleInput i)
    {
        if (i.Cancel || i.RightClick) { OpenActionMenu(2); return; }
        int pick = ListNavigate(i);
        if (pick >= 0) OpenEquipItems(pick);
    }

    /// <summary>
    /// The slot's choices (FUN_00417b08): 「（装備をはずす）」 if something is held, then the inventory's items of that kind
    /// the class may use, 「%-22s×%2d」, 12 a page. Picking one (FUN_0040f650) swaps it in — the old item back to the
    /// inventory (FUN_0040af4c / FUN_0040b038) — and ends the unit's action.
    /// </summary>
    void OpenEquipItems(int slot)
    {
        var u = _sel!;
        _equipSlot = slot;
        int held = slot switch { 0 => u.Weapon, 1 => u.Armor, _ => u.Accessory };
        _items = Inventory().Where(it => (int)it.Type == slot + 1 && UnitChanges.Usable(it, u.ClassId)).ToList();
        _equipRows = (held >= 0 ? new Item?[] { null } : Array.Empty<Item?>()).Concat(_items.Select(it => (Item?)it)).ToList();
        _menu = _equipRows.Select(it => it is null ? Word(32) : ItemRow(it)).ToArray();
        _menuEnabled = _equipRows.Select(_ => true).ToArray();
        _menuIndex = 0;
        _itemPage = 0;
        _state_ = State.EquipList;
        _pointMenu = true;
    }

    void UpdateEquipList(BattleInput i)
    {
        if (i.Cancel || i.RightClick) { int slot = _equipSlot; OpenEquipSlots(); _menuIndex = slot; return; }
        int pick = ListNavigate(i);
        if (pick < 0) return;
        var u = _sel!;
        int now = _equipRows[pick]?.Index ?? -1;
        int old = _equipSlot switch { 0 => u.Weapon, 1 => u.Armor, _ => u.Accessory };
        if (now >= 0) _state.AddItem(now, -1);
        if (old >= 0) _state.AddItem(old, 1);
        switch (_equipSlot)
        {
            case 0: u.Weapon = now; break;
            case 1: u.Armor = now; break;
            default: u.Accessory = now; break;
        }
        FinishAction();
    }

    /// <summary>
    /// 必殺技 (FUN_00417548): every known spell, 「%-22sＴＰ%4d／%2d」 (cost and TP left), 80 px left of the unit's tile, the
    /// pointer on the first; a spell can be picked whenever its cost is covered — where it can land is the aim's business.
    /// </summary>
    void OpenSpellList()
    {
        var u = _sel!;
        _spells = _s.Field.KnownSpells(u).ToList();
        _menu = _spells.Select(m => $"{Pad(m.Name, 22)}{Word(3)}{m.MpCost,4}／{u.Mp,2}").ToArray();
        _menuEnabled = _spells.Select(m => Battlefield.CanCast(u, m)).ToArray();
        _menuIndex = 0;
        _state_ = State.SpellList;
        _pointMenu = true;
    }

    /// <summary>
    /// アイテム (FUN_00417f54, FUN_00418108): every item in the inventory, 12 a page, from the first page's first row;
    /// medicine, stat-up and magic items the class may use and class-change items for its level and class can be picked,
    /// the rest are greyed.
    /// </summary>
    void OpenItemList()
    {
        var u = _sel!;
        _items = Inventory().ToList();
        _menu = _items.Select(ItemRow).ToArray();
        _menuEnabled = _items.Select(it => _s.Field.CanUseInBattle(u, it)).ToArray();
        _menuIndex = 0;
        _itemPage = 0;
        _state_ = State.ItemList;
        _pointMenu = true;
    }

    // The lists open by the acting unit (UnitPanel); the hovered row gets the flashing light-green / white bar, and a
    // description window sits at (80,32) 480×48 (FUN_004142d4). Items and equipment choices: 12 rows a page and a
    // "ページ n／m" footer.
    const int ItemRows = 12;
    int _itemPage;

    /// <summary>
    /// The current list: spells ((WORD 3's bytes + 1) / 2 + 17) units wide — 228 px for ＴＰ — 80 px left of the unit's
    /// tile (FUN_00417548); equipment slots 204 × 64, 64 left, placed as if 180 wide (FUN_004177e8 widens it after the
    /// clamp, so by the right edge it reaches into the frame); items and equipment choices 180 × 240 (15 rows), 48 left.
    /// </summary>
    Rectangle ListRect() => _state_ switch
    {
        State.SpellList => UnitPanel(-80, ((Bin.Sjis.GetByteCount(Word(3)) + 1) / 2 + 17) * 12, (_menu.Length + 1) * 16),
        State.EquipSlots => UnitPanel(-64, 204, 64, placedW: 180),
        _ => UnitPanel(-48, 180, 240),
    };

    bool Paged => _state_ is State.ItemList or State.EquipList;

    /// <summary>A row's bar (FUN_004144f4): 6 px in from the panel's sides, 16-px rows from y + 8.</summary>
    Rectangle RowRect(int row)
    {
        var r = ListRect();
        return new Rectangle(r.X + 6, r.Y + 8 + (Paged ? row % ItemRows : row) * 16, r.Width - 12, 16);
    }

    int ListNavigate(BattleInput i)
    {
        int n = _menu.Length;
        if (n == 0) return -1;
        if (i.Up) _menuIndex = (_menuIndex + n - 1) % n;
        if (i.Down) _menuIndex = (_menuIndex + 1) % n;
        if (Paged)
        {
            int pages = (n + ItemRows - 1) / ItemRows;
            if (i.Left || i.Right) _menuIndex = Math.Min(n - 1, ((_menuIndex / ItemRows + (i.Right ? 1 : pages - 1)) % pages) * ItemRows);
            _itemPage = _menuIndex / ItemRows;
        }
        int first = Paged ? _itemPage * ItemRows : 0;
        int count = Paged ? Math.Min(ItemRows, n - first) : n;
        int hover = -1;
        for (int r = 0; r < count; r++)
            if (RowRect(first + r).Contains(MouseVirtual)) hover = first + r;
        if (i.MouseMoved && hover >= 0) _menuIndex = hover;
        // a click counts on a row only (FUN_00413100: none is chosen while the pointer is off the rows); keys pick the bar's
        if ((i.Confirm && !i.Click) || (i.Click && hover >= 0))
        {
            if (_menuEnabled[_menuIndex]) { Sfx.Click(); return _menuIndex; }
            Sfx.Buzzer();
        }
        return -1;
    }

    /// <summary>A right click goes back to the action menu's 必殺技 (FUN_0040f308); a spell aims from the unit's tile.</summary>
    void UpdateSpellList(BattleInput i)
    {
        if (i.Cancel || i.RightClick) { OpenActionMenu(1); return; }
        int pick = ListNavigate(i);
        if (pick < 0) return;
        _spellRow = pick;
        BeginAim(_spells[pick], null);
    }

    /// <summary>
    /// A picked item (FUN_0040f930): a medicine waits for a click on a unit within its range (FUN_00411ba0; range 0, the
    /// user itself), a magic item aims its spell — both with the pointer on the user — and a stat-up or class-change item
    /// asks first (FUN_00418308). A right click goes back to the action menu's アイテム.
    /// </summary>
    void UpdateItemList(BattleInput i)
    {
        if (i.Cancel || i.RightClick) { OpenActionMenu(3); return; }
        int pick = ListNavigate(i);
        if (pick < 0) return;
        var it = _items[pick];
        if (it.Type is ItemType.StatUp or ItemType.ClassChange) { OpenUseConfirm(it); return; }
        if (_s.Field.ItemSpell(it) is { } m) { BeginAim(m, it); return; }
        _aimItem = it;
        _medRange = _s.Field.MedicineRange(_sel!, it).ToHashSet();
        _medTargets = _s.Field.MedicineTargets(_sel!, it).ToList();
        PointAt(_sel!);
        _state_ = State.MedicineTarget;
    }

    Item? _useItem;
    readonly Camp.MenuList _useMenu = new() { ItemX = 54, ItemWidth = 36 };

    /// <summary>
    /// 「〈item〉 / を使います / ― / はい / いいえ」 (FUN_00418308): a green panel 144 × 96, 32 px left of the unit's tile at its
    /// top, inside the frame; はい / いいえ at x + 54, 36 px wide. はい uses it (FUN_0040fc00), いいえ goes back to the list.
    /// </summary>
    void OpenUseConfirm(Item it)
    {
        _useItem = it;
        _useMenu.Rect = UnitPanel(-32, 144, 96, list: false);
        _useMenu.Header = new[] { it.Name, Word(58), "-" };   // を使います
        _useMenu.Set(new[] { Word(45), Word(46) });
        _useMenu.Index = 0;
        _state_ = State.UseConfirm;
        _pointMenu = true;
    }

    void UpdateUseConfirm(BattleInput i)
    {
        if (i.Cancel || i.RightClick) { ReopenItemList(); return; }
        if (_useMenu.Navigate(i, MouseVirtual) is var c and >= 0)
        {
            if (c == 0 && _useItem is { } it) DoSelfItem(_sel!, it);
            else ReopenItemList();
        }
    }

    /// <summary>Back to the item list on the same row (FUN_0040fc00, FUN_0040fda8 restore its page and row).</summary>
    void ReopenItemList()
    {
        int keep = _menuIndex;
        OpenItemList();
        _menuIndex = Math.Clamp(keep, 0, Math.Max(0, _menu.Length - 1));
        _itemPage = _menuIndex / ItemRows;
    }

    /// <summary>A spell's aim (FUN_004117d0), the pointer on the caster (FUN_00417188).</summary>
    void BeginAim(Magic m, Item? fromItem)
    {
        var u = _sel!;
        _aimSpell = m;
        _aimItem = fromItem;
        _aimRange = _s.Field.SpellRange(m, u.X, u.Y).ToHashSet();
        _aimTargets = new();
        _aimFocus = null;
        _aimFocusAt = null;
        PointAt(u);
        _state_ = State.Aiming;
    }

    /// <summary>A right click goes back to the spell's row (FUN_00410050) or the item's (FUN_0040fda8).</summary>
    void UpdateAiming(BattleInput i)
    {
        if (i.Cancel || i.RightClick)
        {
            if (_aimItem is not null) ReopenItemList();
            else { OpenSpellList(); _menuIndex = Math.Clamp(_spellRow, 0, Math.Max(0, _menu.Length - 1)); }
            return;
        }
        MoveCursor(i);
        FocusAim(i);
        if (!Pick(i) || _aimSpell is null) return;
        var a = (_cursor.X, _cursor.Y);
        // nothing unless the aim is in range (any aim for a spell covering the whole map) and meets someone (FUN_00410050)
        if ((!_aimSpell.EffectWholeMap && !_aimRange.Contains(a)) || _s.Field.SpellTargets(_sel!, _aimSpell, a.X, a.Y).Count == 0) return;
        Sfx.Click();
        Cast(_sel!, _aimSpell, a, _aimItem);
    }

    /// <summary>
    /// The spell forecast's unit (modern): whenever the aim moves, the first of the units the spell would reach from
    /// there — the nearest it's meant for; then the info key steps on through them all, the wheel either way.
    /// </summary>
    void FocusAim(BattleInput i)
    {
        var aim = (_cursor.X, _cursor.Y);
        _aimTargets = AimTargets(aim);
        if (aim != _aimFocusAt || _aimFocus is null || !_aimTargets.Contains(_aimFocus)) { _aimFocus = _aimTargets.FirstOrDefault(); _aimFocusAt = aim; }
        int step = (i.Info ? 1 : 0) - Math.Sign(i.Wheel);
        if (step == 0 || _aimTargets.Count < 2) return;
        _aimFocus = _aimTargets[(_aimTargets.IndexOf(_aimFocus!) + step + _aimTargets.Count) % _aimTargets.Count];
        Sfx.Click();
    }

    /// <summary>
    /// The units the spell would reach aimed at <paramref name="aim"/> (none out of its range, unless it covers the whole
    /// map), in the order its forecast goes through them: those it's meant for first — the other side for a harmful
    /// spell, the caster's own for a support one, sides as <see cref="Battlefield.Affects"/> counts them — each group
    /// nearest the aim first, then in the map's row order.
    /// </summary>
    List<BattleUnit> AimTargets((int X, int Y) aim)
    {
        if (_aimSpell is not { } m || _sel is not { } c || (!m.EffectWholeMap && !_aimRange.Contains(aim))) return new();
        bool Meant(BattleUnit t) => ((t.Side == Side.Enemy) != (c.Side == Side.Enemy)) == (m.Type is 0 or 1);
        return _s.Field.SpellTargets(c, m, aim.X, aim.Y)
            .OrderBy(t => Meant(t) ? 0 : 1).ThenBy(t => Math.Abs(t.X - aim.X) + Math.Abs(t.Y - aim.Y)).ToList();
    }

    void UpdateMedicineTarget(BattleInput i)
    {
        if (i.Cancel || i.RightClick) { ReopenItemList(); return; }   // back to its row (FUN_0040fda8)
        if ((i.Left || i.Up || i.Right || i.Down) && _medTargets.Count > 0)
        {
            int k = _medTargets.FindIndex(t => t.X == _cursor.X && t.Y == _cursor.Y);
            k = (k + ((i.Right || i.Down) ? 1 : _medTargets.Count - 1) + _medTargets.Count) % _medTargets.Count;
            _cursor = new Point(_medTargets[k].X, _medTargets[k].Y);
        }
        if (!Pick(i)) return;
        var target = _medTargets.FirstOrDefault(t => t.X == _cursor.X && t.Y == _cursor.Y);
        if (target is not null && _aimItem is not null) { Sfx.Click(); DoMedicine(_sel!, _aimItem, target); }
    }

    // ---------------- an action under way (state 0x12) ----------------

    /// <summary>
    /// What an attack or a spell leaves on the map until its scene begins (FUN_00410190, FUN_00410050 and FUN_0040fda8 for
    /// the player's click, FUN_00416e04 and FUN_00416e98 for the computer's): the range cleared, the actor's tile cyan, the
    /// tiles it reaches in its colour and the units it meets solid.
    /// </summary>
    sealed record ActionOverlay((int X, int Y) Actor, int Colour, IReadOnlySet<(int X, int Y)> Area, IReadOnlyList<(int X, int Y)> Met);
    ActionOverlay? _action;
    Action? _acting;
    int _actingFrames;

    /// <summary>
    /// State 0x12 (FUN_00410388): its counter starts at 5 on the player's click and at 0 on the computer's decision and
    /// goes up a tick at a time; at 5 the "before" events get their turn — a tick after the click, 6 after the decision —
    /// and the fight follows, its scene's lead-in counting on (<see cref="BattleScene"/>).
    /// </summary>
    void BeginAction(ActionOverlay overlay, Action fight)
    {
        _action = overlay;
        if (Quiet) { fight(); return; }
        _acting = fight;
        _actingFrames = (_s.Field.AllyPhase ? 1 : 6) * 2;
        _state_ = State.Acting;
    }

    void UpdateActing()
    {
        if (--_actingFrames > 0) return;
        var fight = _acting!;
        _acting = null;
        fight();
    }

    /// <summary>
    /// An attack chosen (FUN_00410190 on the player's click, FUN_00416e04 on the computer's decision): the exchange is
    /// rolled at once (FUN_0041b4b0), so whatever the lead-in's "before" events do, the outcome stands.
    /// </summary>
    void Attack(BattleUnit atk, BattleUnit def)
    {
        var r = _s.Field.RollAttack(atk, def);
        BeginAction(new((atk.X, atk.Y), 2, new HashSet<(int X, int Y)> { (def.X, def.Y) }, new[] { (def.X, def.Y) }), () => DoAttack(atk, def, r));
    }

    /// <summary>
    /// A spell cast (FUN_00410050; FUN_0040fda8 for a magic item, which is one less and its user done at once;
    /// FUN_00416e98 for the computer's): rolled at once (FUN_0041c8c0), then the lead-in.
    /// </summary>
    void Cast(BattleUnit caster, Magic m, (int X, int Y) aim, Item? item)
    {
        if (item is not null) { _state.AddItem(item.Index, -1); caster.Acted = true; }   // FUN_0040ae68, and +8 at the click
        var r = _s.Field.RollCast(caster, m, aim.X, aim.Y, fromItem: item is not null);
        BeginAction(new((caster.X, caster.Y), m.Type is 0 or 1 ? 2 : 3, _s.Field.SpellArea(m, aim.X, aim.Y).ToHashSet(),
            r.Effects.Select(e => (e.Target.X, e.Target.Y)).ToList()), () => DoCast(caster, m, r));
    }

    void DoCast(BattleUnit caster, Magic m, ExchangeResult r)
    {
        // the units the spell meets on the other side get their first-attack slots (FUN_00410388)
        var met = OpposingOf(caster, r.Effects.Select(e => e.Target));
        if (BeforeFight(caster, met, new PendingFight(caster, null, m, r))) return;
        if (caster.Dead || caster.Hidden) { caster.Acted = true; _actor = caster; AfterFight(); return; }
        if (SceneFor(caster) && r.Effects.Count > 0) QueueSpellScenes(caster, m, r);
        else
        {
            Battlefield.ApplyCost(caster, r);
            foreach (var e in r.Effects) _s.Field.ApplyEffect(caster, m, e);
            if (!Quiet) ResultPopups(caster, r);
            if (r.Effects.Count > 0) PhaseMusic();   // no scene shown (an option): the music the original's would leave
        }
        _fight = r;
        _after = (caster, met);
        _actor = caster;
        RunScenes();
    }

    /// <summary>
    /// A stat-up or class-change item on its user (FUN_0040b074). Stat-ups add their positive values (max HP/MP up to 60,
    /// the rest 30, current HP/MP with them): 「〜がｎポイント上昇した」 for each (FUN_0040b8f0). A class change (FUN_0040be00 →
    /// FUN_0040bb0c) restarts the level at 1, moves Str/Mag/Skl/Sta by the classes' initial-stat difference (1–30) and
    /// sends unusable equipment to the inventory: 「〈class〉に昇格」, a line per stat that moved, LEVELUP.WAV. The lines come
    /// in one panel at (160,64), 240 wide (FUN_004157a4), and the action ends.
    /// </summary>
    void DoSelfItem(BattleUnit user, Item it)
    {
        _state.AddItem(it.Index, -1);
        var lines = new List<string>();
        Action? sound = null;
        if (it.Type == ItemType.StatUp)
        {
            var (b, gained) = UnitChanges.StatUp(user.Base, it.S.StatUp);
            Array.Copy(b, user.Base, 6);
            user.Hp += gained[0];
            user.Mp += gained[1];
            int[] words = { 2, 3, 8, 9, 10, 11 };   // ＨＰ ＴＰ 攻撃補正値 特殊補正値 命中回避値 耐久力
            for (int k = 0; k < 6; k++)
                if (StatLine(words[k], gained[k]) is { } s) lines.Add(s);
        }
        else
        {
            int to = UnitChanges.ClassChangeTarget(it, user.ClassId);
            var from = _db.Classes[Math.Clamp(user.ClassId, 0, _db.Classes.Length - 1)];
            var before = user.Base.ToArray();
            Array.Copy(UnitChanges.ClassChangeStats(user.Base, from, _db.Classes[to]), user.Base, 6);
            user.ClassId = to;
            user.Level = 1;
            int Keep(int item) { if (item >= 0 && !UnitChanges.Usable(_db.Items[item], to)) { _state.AddItem(item, 1); return -1; } return item; }
            (user.Weapon, user.Armor, user.Accessory) = (Keep(user.Weapon), Keep(user.Armor), Keep(user.Accessory));
            lines.Add($"{_db.Classes[to].Name}{Word(72)}");   // 「に昇格」
            int[] words = { 8, 9, 10, 11 };                    // 攻撃補正値 特殊補正値 命中回避値 耐久力
            for (int k = 0; k < 4; k++)
                if (StatLine(words[k], user.Base[2 + k] - before[2 + k]) is { } s) lines.Add(s);
            sound = Sfx.LevelUp;
        }
        ShowPanel(lines, 160, 20, sound);
        _after = null;
        Finish(user);
    }

    /// <summary>
    /// A medicine given to a unit (FUN_0040fda8 → FUN_0040b188): its results in one panel at (160,64), 240 wide (FUN_004157a4)
    /// — 「ＨＰ / ＴＰがｎポイント回復した」 with the item's amounts, 「物理防御 / 特殊防御がｎポイント強化された」, 「毒が消えた」,
    /// 「目を覚ました」, 「攻撃力 / 物理防御が回復した」 — then the action ends; no panel when nothing came of it.
    /// </summary>
    void DoMedicine(BattleUnit user, Item it, BattleUnit target)
    {
        _state.AddItem(it.Index, -1);
        var r = _s.Field.UseMedicine(user, it, target);
        string Points(int word, int v, int verb) => $"{Word(word)}{Word(64)}{Wide(v)}{Word(65)}{Word(verb)}";
        ShowPanel(r.Effects.Select(e => e.Kind switch
        {
            EffectKind.Heal => Points(2, e.Amount, 66),
            EffectKind.MpHeal => Points(3, e.Amount, 66),
            EffectKind.DefUp => Points(6, e.Amount, 67),
            EffectKind.MdefUp => Points(7, e.Amount, 67),
            EffectKind.CurePoison => Word(74),
            EffectKind.WakeUp => Word(76),
            EffectKind.AttackRestored => $"{Word(4)}{Word(64)}{Word(66)}",
            EffectKind.DefenseRestored => $"{Word(6)}{Word(64)}{Word(66)}",
            _ => "",
        }).Where(s => s.Length > 0), 160, 20);
        _after = null;
        Finish(user);
    }

    void Finish(BattleUnit actor)
    {
        actor.Acted = true;
        _actor = actor;
        RunScenes();
    }

    bool HasDeathMessage(BattleUnit u) => u.Unit >= 0 && u.Unit < _db.Units.Length && _db.Units[u.Unit].DeathMessage.Length > 0;

    /// <summary>
    /// A spell's scene (FUN_0041c8c0), as rolled at the cast: the caster alone plays its class's cast animation —
    /// magic-to-enemy for damage and status spells, magic-to-ally for the rest — its TP going at the step's end
    /// (FUN_0041b6ec); then each unit it lands on alone in turn plays the spell's animation (ANIME 80 + its animation,
    /// FUN_0041ba0c), its share landing at the step's end: damage and recovery with a number and the bar (the damage as
    /// rolled, 0 for a damage spell's miss, a recovery as far as the HP goes), other spells — status spells' misses too —
    /// without.
    /// </summary>
    void QueueSpellScenes(BattleUnit caster, Magic m, ExchangeResult r)
    {
        var cls = _s.Field.Class(caster);
        bool harmful = m.Type is 0 or 1;
        var cast = _db.Animations[Math.Clamp(harmful ? cls.AnimMagicEnemy : cls.AnimMagicAlly, 0, _db.Animations.Length - 1)];
        var spell = _db.Animations[Math.Clamp(m.AnimeIndex, 0, _db.Animations.Length - 1)];
        string spellSheet = BattleScene.SheetFor(_db, _s.Field, caster, spell);
        var steps = new List<BattleScene.Step>
        {
            new(BattleScene.StepKind.Cast, caster, null, cast, BattleScene.SheetFor(_db, _s.Field, caster, cast), true)
            {
                MpAfter = r.MpAfter ?? caster.Mp, Apply = () => Battlefield.ApplyCost(caster, r),
            },
        };
        foreach (var e in r.Effects)
        {
            var t = e.Target;
            var step = new BattleScene.Step(BattleScene.StepKind.Spell, null, t, spell, spellSheet, e.Kind is not (EffectKind.Miss or EffectKind.Resisted))
            {
                Apply = () => _s.Field.ApplyEffect(caster, m, e),
            };
            steps.Add(e.Kind switch
            {
                EffectKind.Damage => step with
                {
                    Numbered = true, Amount = e.Amount, Moved = e.Moved, HpAfter = e.HpAfter, DeathMessage = e.Killed && HasDeathMessage(t),
                },
                EffectKind.Miss when m.Type == 0 => step with { Numbered = true, HpAfter = e.HpAfter },
                EffectKind.Heal => step with { Numbered = true, Recovery = true, Amount = e.Amount, Moved = e.Moved, HpAfter = e.HpAfter },
                _ => step,
            });
        }
        var scene = new BattleScene(_db, _tex, _ui, _s.Field, steps, HpSnapshot(r.Effects.Select(e => e.Target).Append(caster)))
        {
            StepStarted = st => BattleMusic(caster, st.Target),
        };
        scene.ShowMpBefore(caster, caster.Mp);
        _scenes.Enqueue(scene);
    }

    /// <summary>
    /// A spell's outcome in the client's own one-line messages, when the battle scenes are off (the original shows it
    /// only in the scene, then the rewards and level-up panels).
    /// </summary>
    void ResultPopups(BattleUnit actor, ExchangeResult r)
    {
        foreach (var e in r.Effects)
        {
            var n = e.Target.Name;
            // the client's own wording, in the style of the original's medicine messages
            string Points(string what, int v, int verb) => $"{what}{Word(64)}{Wide(v)}{Word(65)}{Word(verb)}";
            Popup(e.Kind switch
            {
                EffectKind.Damage => $"{actor.Name} → {n}：{e.Amount}{(e.Killed ? "　撃破" : "")}",
                EffectKind.Miss => $"{actor.Name} → {n}：ミス",
                EffectKind.Resisted => $"{n}：無効",
                EffectKind.Heal => Points(Word(2), e.Amount, 66),
                EffectKind.MpHeal => Points(Word(3), e.Amount, 66),
                EffectKind.PowerUp => Points(Word(4), e.Amount, 67),
                EffectKind.DefUp => Points(Word(6), e.Amount, 67),
                EffectKind.MdefUp => Points(Word(7), e.Amount, 67),
                EffectKind.AttackDown => Points(Word(4), e.Amount, 69),
                EffectKind.DefenseDown => Points(Word(6), e.Amount, 69),
                EffectKind.Poison => $"{n}は毒を受けた",
                EffectKind.Sleep => $"{n}は眠った",
                EffectKind.CurePoison => $"{n}の{Word(74)}",                                   // "毒が消えた"
                EffectKind.WakeUp => $"{n}は{Word(76)}",                                       // "目を覚ました"
                EffectKind.Cleanse => $"{n}の状態異常が回復した",
                EffectKind.AttackRestored => $"{Word(4)}{Word(64)}{Word(66)}",
                EffectKind.DefenseRestored => $"{Word(6)}{Word(64)}{Word(66)}",
                _ => $"{n}は再行動できる",
            }, e.Killed ? e.Target : null);
            if (e.Killed) _s.Ghosts.Add(e.Target);
        }
    }

    /// <summary>Numbers as the original's messages print them (FUN_004051a0): full-width digits and minus sign.</summary>
    static string Wide(int v) => string.Concat(v.ToString().Select(c => c is >= '0' and <= '9' ? (char)('０' + (c - '0')) : c == '-' ? '－' : c));

    /// <summary>「〜がｎポイント上昇した / 下降した」 (FUN_0040b800); nothing for 0.</summary>
    string? StatLine(int word, int change) =>
        change == 0 ? null : $"{Word(word)}{Word(64)}{Wide(change)}{Word(65)}{Word(change > 0 ? 68 : 69)}";

    /// <summary>
    /// After a fight or a spell (FUN_0041093c): for a player unit that gained EXP and lives, the rewards panel at
    /// (200,64), 240 wide — 「ｎの経験値を獲得」, 「ｎ,肉を獲得」, each item 「〜を獲得」 — with the gold and items counted in;
    /// then, if it gained a level, the level-up panel at (140,64), 360 wide, with LEVELUP.WAV (FUN_00414d40): 「〜はレベルｎに
    /// なった」, each raised stat 「〜がｎポイント上昇した」, each spell learned 「〜を習得した」.
    /// </summary>
    void ShowGrowth(ExchangeResult r)
    {
        foreach (var (u, exp) in r.ExpGained)
        {
            if (u.Dead) continue;
            var lines = new List<string> { $"{Wide(exp)}{Word(60)}" };
            if (r.RewardGold > 0) lines.Add($"{Wide(r.RewardGold)}{Word(61)}");
            lines.AddRange(r.RewardItems.Where(it => it >= 0 && it < _db.Items.Length).Select(it => $"{_db.Items[it].Name}{Word(62)}"));
            ShowPanel(lines, 200, 20);
            _state.Gold = Math.Min(9_999_999, _state.Gold + r.RewardGold);
            foreach (var item in r.RewardItems) _state.AddItem(item, 1);
            if (!r.LevelUps.TryGetValue(u, out var up)) continue;
            var lv = new List<string> { $"{u.Name}{Word(70)}{Wide(u.Level)}{Word(71)}" };
            int[] words = { 2, 3, 8, 9, 10, 11 };   // ＨＰ ＴＰ 攻撃補正値 特殊補正値 命中回避値 耐久力
            for (int k = 0; k < 6; k++)
                if (up[k] > 0 && StatLine(words[k], up[k]) is { } s) lv.Add(s);
            if (r.Learned.TryGetValue(u, out var spells)) lv.AddRange(spells.Select(m => $"{m.Name}{Word(73)}"));
            ShowPanel(lv, 140, 30, Sfx.LevelUp);
        }
    }

    Point _turnMenuAt;

    /// <summary>
    /// The action and turn menus' panels: (the longest entry's bytes + 1) / 2 (at least 2) + 2 units of 12 px wide, a 16-px
    /// row per entry plus one high. The action menu (FUN_004171e0) one tile right of the unit's, at its top: past x 552 it
    /// goes 104 px left (the unit's other side), past y 368 64 px up. The turn menu (FUN_00418614) 7 units wide, one tile
    /// left of the clicked tile at its top, kept inside the frame (FUN_0041855c).
    /// </summary>
    Rectangle MenuRect()
    {
        int h = (_menu.Length + 1) * 16;
        if (_sel is null)
        {
            const int tw = 7 * 12;
            return new Rectangle(Math.Clamp(_turnMenuAt.X - 32, 16, 624 - tw), Math.Clamp(_turnMenuAt.Y, 16, 464 - h), tw, h);
        }
        int units = Math.Max(4, _menu.Max(m => Bin.Sjis.GetByteCount(m)) + 1) / 2 + 2;
        var t = _s.ScreenPos(_sel.X, _sel.Y, ViewportRect).ToPoint();
        int x = t.X + MapSession.Tile, y = t.Y;
        if (x > 624 - 6 * 12) x -= 6 * 12 + 32;   // the exe checks against its first guess of the width, 6 units
        if (y > 464 - h) y -= h - 32;
        return new Rectangle(x, y, units * 12, h);
    }

    /// <summary>A menu row's bar (FUN_004144f4): from 6 px left of the text (x + 12) to 6 px past the entries' width.</summary>
    static Rectangle MenuBarRect(Rectangle menu, int row) => new(menu.X + 6, menu.Y + 8 + row * 16, menu.Width - 12, 16);

    static readonly Rectangle ViewportRect = new(BreezyGame.ViewX, BreezyGame.ViewY, BreezyGame.ViewWidth, BreezyGame.ViewHeight);

    /// <summary>The row under the pointer (FUN_00413100) or the keys' row; a click counts on a row only.</summary>
    int MenuNavigate(BattleInput i, Point mouseVirtual)
    {
        if (i.Up) _menuIndex = (_menuIndex + _menu.Length - 1) % _menu.Length;
        if (i.Down) _menuIndex = (_menuIndex + 1) % _menu.Length;
        var r = MenuRect();
        int hover = Enumerable.Range(0, _menu.Length).FirstOrDefault(k => MenuBarRect(r, k).Contains(mouseVirtual), -1);
        if (i.MouseMoved && hover >= 0) _menuIndex = hover;
        if ((i.Confirm && !i.Click) || (i.Click && hover >= 0))
        {
            if (_menuEnabled[_menuIndex]) { Sfx.Click(); return _menuIndex; }
            Sfx.Buzzer();
        }
        return -1;
    }

    public Point MouseVirtual { get; set; }

    /// <summary>
    /// FUN_0040f0ac. A right click undoes the move (allowed until the unit acts), every pass-by / stop-on mark going too,
    /// and goes back to the map with the pointer where the move ended. 攻撃 shows the weapon's reach with the pointer on
    /// the unit (FUN_00417188, FUN_00411620), even when nobody is in it; 行動終了 puts the pointer back where the move ended.
    /// </summary>
    void UpdateActionMenu(BattleInput i)
    {
        if (i.Cancel || i.RightClick)
        {
            _sel!.X = _selFrom.X;
            _sel.Y = _selFrom.Y;
            _passMarks.Clear();
            _stopMarks.Clear();
            _sel = null;
            _state_ = State.Idle;
            RestorePointer(1);
            return;
        }
        int pick = MenuNavigate(i, MouseVirtual);
        if (pick == 0)
        {
            _attackFromMenu = true;
            _forecastArmed = null;
            PointAt(_sel!);
            _state_ = State.Targeting;
        }
        else if (pick == 1) OpenSpellList();
        else if (pick == 2) OpenEquipSlots();
        else if (pick == 3) OpenItemList();
        else if (pick == 4) { RestorePointer(1); FinishAction(); }
    }

    /// <summary>A right click goes back to the action menu (FUN_00410190): to 攻撃 if that's where the targets came from, else 行動終了.</summary>
    void UpdateTargeting(BattleInput i)
    {
        if (i.Cancel || i.RightClick) { OpenActionMenu(_attackFromMenu ? 0 : OpeningRow); return; }
        if ((i.Left || i.Up || i.Right || i.Down) && _targets.Count > 0)
        {
            int k = _targets.FindIndex(t => t.X == _cursor.X && t.Y == _cursor.Y);
            k = (k + ((i.Right || i.Down) ? 1 : _targets.Count - 1)) % _targets.Count;
            _cursor = new Point(_targets[k].X, _targets[k].Y);
        }
        if (!Pick(i)) return;
        var target = _targets.FirstOrDefault(t => t.X == _cursor.X && t.Y == _cursor.Y);
        if (target is null) return;
        // a click first brings up that target's forecast, a second click attacks (keys: the forecast is already up)
        if (i.Click && !i.Confirm && _forecastArmed != target) { _forecastArmed = target; Sfx.Click(); return; }
        _forecastArmed = null;
        Sfx.Click();
        Attack(_sel!, target);
    }

    BattleUnit? _forecastArmed;

    /// <summary>
    /// Terrain window (modern addition, Fire Emblem style) in the map's bottom-right corner for the tile under the
    /// cursor: its name, 地形効果 (hit chance taken off attacks on a unit there), the damage it takes off (effect/10)
    /// and the HP recovered each turn, and the move cost of entering it (the selected unit's, else each move type's).
    /// Moves to the bottom-left while the cursor is in that corner.
    /// </summary>
    void DrawTerrainInfo(SpriteBatch sb)
    {
        var ter = _s.Field.TerrainAt(_cursor.X, _cursor.Y);
        // signed from the point of view of a unit standing there: 地形効果 +20% = attacks on it 20% less likely to hit
        string Signed(int v) => v > 0 ? $"+{v}" : $"{v}";
        var lines = new List<(string Label, string Value)> { (Word(75), $"{Signed(ter.DefenseBonus)}%") };
        if (ter.DefenseBonus / 10 != 0) lines.Add(("被ダメージ", Signed(-(ter.DefenseBonus / 10))));
        if (ter.HpRecovery != 0) lines.Add(("HP回復", $"{ter.HpRecovery}"));
        // the move cost of entering the tile: the selected unit's (its class's own cost for the terrain, if it has one),
        // else each move type's — walking, flying, special; × where it can't go
        string Cost(int c) => c <= 0 ? "×" : $"{c}";
        if (_sel is { } mover && _state_ is State.Selected)
            lines.Add((Ui.Lang.T("Move cost", "移動コスト"), Cost(_s.Field.MoveCost(mover, _cursor.X, _cursor.Y))));
        else if (ter.MoveCost.Length >= 3)
            lines.Add((Ui.Lang.T("Move", "移動"), Ui.Lang.T($"W{Cost(ter.MoveCost[0])} F{Cost(ter.MoveCost[1])} S{Cost(ter.MoveCost[2])}",
                $"歩{Cost(ter.MoveCost[0])} 飛{Cost(ter.MoveCost[1])} 特{Cost(ter.MoveCost[2])}")));
        const int w = 136;
        int h = 30 + lines.Count * 16 + 4;
        var p = _s.ScreenPos(_cursor.X, _cursor.Y, ViewportRect);
        int right = BreezyGame.ViewX + BreezyGame.ViewWidth - 8, bottom = BreezyGame.ViewY + BreezyGame.ViewHeight - 8;
        bool inCorner = p.X + MapSession.Tile > right - w - 8 && p.Y + MapSession.Tile > bottom - h - 8;
        var r = new Rectangle(inCorner ? BreezyGame.ViewX + 8 : right - w, bottom - h, w, h);
        _ui.Panel(sb, r);
        void T(string s, int x, int y, Color c, bool alignRight = false)
        {
            if (_ui.SmallText.Get(s) is not { } t) return;
            t.Draw(sb, new Vector2(alignRight ? x - t.Width : x, y), c);
        }
        T(ter.Name, r.X + 10, r.Y + 8, Ui.Ui.PanelText);
        if (Ui.Ui.Gilded) _ui.GoldRule(sb, r.X + 8, r.Y + 25, w - 16);
        else _ui.Fill(sb, new Rectangle(r.X + 8, r.Y + 25, w - 16, 1), Ui.Ui.Modern ? Ui.Ui.ModernBorder : Ui.Ui.PanelText * 0.6f);
        for (int k = 0; k < lines.Count; k++)
        {
            T(lines[k].Label, r.X + 10, r.Y + 30 + k * 16, Ui.Ui.PanelText);
            T(lines[k].Value, r.Right - 10, r.Y + 30 + k * 16, Ui.Ui.StatText, alignRight: true);
        }
    }

    /// <summary>
    /// Battle forecast (modern addition, Fire Emblem style) while choosing a target: both sides' HP before → after
    /// if every strike hits, damage per hit (×n for several strikes), hit and critical chances; "－" where there's no counter.
    /// The attacker is on the right, as the battle scenes put it.
    /// </summary>
    void DrawForecast(SpriteBatch sb, BattleUnit atk, BattleUnit def)
    {
        var (fa, fd) = _s.Field.Forecast(atk, def);
        int defAfter = Math.Max(0, def.Hp - fa.Damage * fa.Strikes);
        int atkAfter = defAfter == 0 ? atk.Hp : Math.Max(0, atk.Hp - fd.Damage * fd.Strikes);
        string Strikes(Battlefield.StrikeForecast f) => f.Strikes == 0 ? "－" : f.Strikes > 1 ? $"{f.Damage} ×{f.Strikes}" : $"{f.Damage}";
        string Pct(int v, Battlefield.StrikeForecast f) => f.Strikes == 0 ? "－" : $"{v}%";
        ForecastPanel(sb, def, atk, new[]
        {
            (Word(2), $"{def.Hp} → {defAfter}", $"{atk.Hp} → {atkAfter}"),
            ("威力", Strikes(fd), Strikes(fa)),
            ("命中", Pct(fd.Hit, fd), Pct(fa.Hit, fa)),
            ("必殺", Pct(fd.Crit, fd), Pct(fa.Crit, fa)),
        });
    }

    /// <summary>
    /// The forecast for a spell (modern) on one of the units it reaches (FocusAim): the unit's HP before → after, what
    /// the spell does to it and its chance to land; the caster's TP before → after (no counter to spells).
    /// </summary>
    void DrawSpellForecast(SpriteBatch sb, BattleUnit caster, Magic m, BattleUnit t, bool fromItem, string? count = null)
    {
        var (kind, amount, hit) = _s.Field.SpellForecast(caster, m, t);
        int after = kind switch { EffectKind.Damage => Math.Max(0, t.Hp - amount), EffectKind.Heal => t.Hp + amount, _ => t.Hp };
        string effect = kind switch
        {
            EffectKind.Damage => $"{amount}", EffectKind.Heal => $"{amount}",
            EffectKind.Poison => "毒", EffectKind.Sleep => "睡眠",
            EffectKind.AttackDown => $"{Word(4)}－{amount}", EffectKind.DefenseDown => $"{Word(6)}－{amount}",
            EffectKind.PowerUp => $"{Word(4)}＋{amount}", EffectKind.DefUp => $"{Word(6)}＋{amount}",
            EffectKind.CurePoison => "解毒", EffectKind.Cleanse => "状態回復", EffectKind.ExtraTurn => "再行動",
            _ => "無効",
        };
        int mpAfter = fromItem ? caster.Mp : Math.Max(0, caster.Mp - m.MpCost);
        ForecastPanel(sb, t, caster, new[]
        {
            (Word(2), $"{t.Hp} → {after}", $"{caster.Hp}"),
            (Word(3), $"{t.Mp}", $"{caster.Mp} → {mpAfter}"),
            (kind == EffectKind.Heal ? "回復" : kind == EffectKind.Damage ? "威力" : "効果", effect, "－"),
            ("命中", kind == EffectKind.Resisted ? "－" : $"{hit}%", "－"),
        }, count);
    }

    /// <summary>The unit whose spell forecast shows, of several the spell reaches: a frame around its tile, its brightness pulsing.</summary>
    void DrawFocusFrame(SpriteBatch sb, BattleUnit u, int frame)
    {
        var p = _s.ScreenPos(u.X, u.Y, ViewportRect).ToPoint();
        const int T = MapSession.Tile;
        var c = Color.White * (0.7f + 0.3f * MathF.Sin(frame * MathF.Tau / 40));
        _ui.Fill(sb, new Rectangle(p.X, p.Y, T, 2), c);
        _ui.Fill(sb, new Rectangle(p.X, p.Y + T - 2, T, 2), c);
        _ui.Fill(sb, new Rectangle(p.X, p.Y + 2, 2, T - 4), c);
        _ui.Fill(sb, new Rectangle(p.X + T - 2, p.Y + 2, 2, T - 4), c);
    }

    /// <summary>
    /// The forecast's panel: <paramref name="left"/> (the one acted on) and <paramref name="right"/> (who acts), named over
    /// strips in their sides' colours, then the rows; <paramref name="count"/> ("2/3") between the names where it fits.
    /// </summary>
    void ForecastPanel(SpriteBatch sb, BattleUnit left, BattleUnit right, (string Label, string Left, string Right)[] rows, string? count = null)
    {
        const int w = 264, h = 122;
        // up and to the right of the two units' tiles; up-left when that would run off the right edge
        var a = _s.ScreenPos(right.X, right.Y, ViewportRect).ToPoint();
        var d = _s.ScreenPos(left.X, left.Y, ViewportRect).ToPoint();
        int lx = Math.Min(a.X, d.X), rx = Math.Max(a.X, d.X) + MapSession.Tile, top = Math.Min(a.Y, d.Y);
        int viewRight = BreezyGame.ViewX + BreezyGame.ViewWidth;
        int x = rx + 8 + w <= viewRight - 4 ? rx + 8 : lx - 8 - w;
        x = Math.Clamp(x, BreezyGame.ViewX + 4, viewRight - w - 4);
        int py = Math.Clamp(top - h - 8, BreezyGame.ViewY + 4, BreezyGame.ViewY + BreezyGame.ViewHeight - h - 4);
        var r = new Rectangle(x, py, w, h);
        _ui.Panel(sb, r);
        var white = Ui.Ui.PanelText;
        var dim = new Color(200, 210, 200);
        void T(string s, int cx, int y, Color c, int align)   // align: -1 left, 0 centre, 1 right
        {
            if (_ui.SmallText.Get(s) is not { } t) return;
            float px = align < 0 ? cx : align > 0 ? cx - t.Width : cx - t.Width / 2f;
            t.Draw(sb, new Vector2(r.X + px, r.Y + y), c);
        }
        Color SideColor(BattleUnit u) => u.Side == Side.Ally ? new Color(64, 120, 248) : u.Side == Side.Enemy ? new Color(232, 56, 56) : new Color(232, 192, 48);
        // names over coloured strips
        _ui.Fill(sb, new Rectangle(r.X + 8, r.Y + 26, 108, 2), SideColor(left));
        _ui.Fill(sb, new Rectangle(r.Right - 116, r.Y + 26, 108, 2), SideColor(right));
        T(left.Name, 10, 8, white, -1);
        T(right.Name, w - 10, 8, white, 1);
        if (count is not null && _ui.SmallText.Get(count) is { } ct && Math.Max(Width(left.Name), Width(right.Name)) + 10 + 8 <= (w - ct.Width) / 2f)
            T(count, w / 2, 8, dim, 0);
        float Width(string s) => _ui.SmallText.Get(s)?.Width ?? 0;
        for (int k = 0; k < rows.Length; k++)
        {
            int y = 36 + k * 20;
            T(rows[k].Label, w / 2, y, dim, 0);
            T(rows[k].Left, 92, y, white, 1);
            T(rows[k].Right, w - 92, y, white, -1);
        }
    }

    void DoAttack(BattleUnit atk, BattleUnit def, ExchangeResult r)
    {
        var met = OpposingOf(atk, new[] { def });
        if (BeforeFight(atk, met, new PendingFight(atk, def, null, r))) return;
        // a "before" event can change things (rarely): no fight if either side is gone
        if (atk.Dead || atk.Hidden || def.Dead || def.Hidden) { atk.Acted = true; _actor = atk; AfterFight(); return; }
        if (SceneFor(atk))
        {
            // a strike a step (FUN_0041b4b0 → FUN_0041a9c4) as rolled at the click: the damage as rolled for the number,
            // the bar moving as far as the HP went, the HP set at the step's end
            var steps = r.Strikes.Select(st =>
            {
                var a = BattleScene.WeaponAnim(_db, _s.Field, st.Attacker);
                return new BattleScene.Step(BattleScene.StepKind.Strike, st.Attacker, st.Target, a, BattleScene.SheetFor(_db, _s.Field, st.Attacker, a), st.Hit)
                {
                    Critical = st.Critical, Numbered = true, Amount = st.Damage, Moved = st.Moved, HpAfter = st.HpAfter,
                    DeathMessage = st.Killed && HasDeathMessage(st.Target), Apply = () => _s.Field.ApplyStrike(st),
                };
            }).ToList();
            _scenes.Enqueue(new BattleScene(_db, _tex, _ui, _s.Field, steps, HpSnapshot(new[] { atk, def }))
            {
                StepStarted = st => BattleMusic(st.Actor!, st.Target),
            });
        }
        else
        {
            foreach (var st in r.Strikes)
            {
                _s.Field.ApplyStrike(st);
                if (Quiet) continue;
                Popup(st.Hit
                    ? $"{st.Attacker.Name} → {st.Target.Name}：{st.Damage}{(st.Critical ? "（会心）" : "")}{(st.Killed ? "　撃破" : "")}"
                    : $"{st.Attacker.Name} → {st.Target.Name}：ミス", st.Killed ? st.Target : null);
                if (st.Killed) _s.Ghosts.Add(st.Target);
            }
            PhaseMusic();   // no scene shown (an option): the music the original's would leave
        }
        _fight = r;
        _after = (atk, met);
        _actor = atk;
        RunScenes();
    }

    /// <summary>A one-line message of the client's own (strike results with the battle scenes off).</summary>
    void Popup(string text, BattleUnit? kill = null, Action? sound = null) =>
        _panels.Enqueue(new Panel(new[] { text }, 0, Math.Max(20, (_ui.Measure12(text) + 32 + 11) / 12), kill,
            sound ?? (kill is not null ? Sfx.Down : null), Auto: true));

    /// <summary>One of the original's message panels (nothing when there are no lines).</summary>
    void ShowPanel(IEnumerable<string> lines, int x, int units, Action? sound = null)
    {
        var l = lines.ToArray();
        if (l.Length > 0) _panels.Enqueue(new Panel(l, x, units, null, sound, Auto: false));
    }

    void NextPopup()
    {
        if (_panel?.Kill is { } k) _s.Ghosts.Remove(k);
        if (_panels.Count > 0) { _panel = _panels.Dequeue(); _panel.Sound?.Invoke(); _panelFrames = 0; _state_ = State.Showing; }
        else
        {
            // everything shown: the fight's "after" events, then the checkpoint (FUN_0041093c)
            _panel = null;
            _sel = null;
            AfterFight();
        }
    }

    /// <summary>The original's panels wait for a click (FUN_0041093c, FUN_00410a88, FUN_0040fd7c); fast-forward moves them on.</summary>
    void UpdateShowing(BattleInput i)
    {
        _panelFrames++;
        if (i.Confirm || i.Click || (i.FastForward && _panelFrames > 4) || (_panel?.Auto == true && _panelFrames > 60)) NextPopup();
    }

    void FinishAction()
    {
        _sel!.Acted = true;
        _actor = _sel;
        _sel = null;
        if (CheckPoint()) return;
        _state_ = State.Idle;   // the turn only ends from the menu (T19a)
    }

    /// <summary>
    /// The turn menu (FUN_00418614) on a click on an empty tile, the pointer's place kept (slot 0) and the pointer on the
    /// first entry. WORD.DAT: 19 ターン終了, 20 全体マップ, 21 セーブする, 22 ロードする, 23 ゲーム終了 (the original's test
    /// play adds デバッグ, which isn't part of the game); the client adds 環境設定 (Options) and leaves ゲーム終了 out (the
    /// window's close button quits).
    /// </summary>
    void OpenTurnMenu()
    {
        SavePointer(0);
        _menu = new[] { Word(19), Word(20), Word(21), Word(22), Ui.Lang.T("Options", "環境設定") };
        _menuEnabled = new[] { true, true, true, true, true };
        _menuIndex = 0;
        _turnMenuAt = _s.ScreenPos(_cursor.X, _cursor.Y, ViewportRect).ToPoint();
        _sel = null;
        _state_ = State.TurnMenu;
        _pointMenu = true;
    }

    /// <summary>Back to the turn menu from one of its questions or lists, the pointer on <paramref name="row"/> (FUN_00418614 again).</summary>
    void ReopenTurnMenu(int row)
    {
        _menuIndex = row;
        _state_ = State.TurnMenu;
        _pointMenu = true;
    }

    /// <summary>A right click closes it, the pointer going back where it was opened (FUN_004104e4).</summary>
    void UpdateTurnMenu(BattleInput i)
    {
        if (i.Cancel || i.RightClick) { _state_ = State.Idle; RestorePointer(0); return; }
        switch (MenuNavigate(i, MouseVirtual))
        {
            case 0: OpenConfirm(52, State.EndTurnConfirm); break;   // ターンを終了します (FUN_00418824)
            case 1: _state_ = State.WholeMap; break;
            case 2: OpenFiles(save: true); break;
            case 3: OpenFiles(save: false); break;
            case 4: OptionsRequested?.Invoke(); break;              // the client's options; the menu stays up under them
        }
    }

    /// <summary>
    /// A はい／いいえ question from the turn menu (FUN_00418824, FUN_00418a00): a green panel (the larger of 3 and half the
    /// question's bytes, plus 2) × 12 px wide and 5 rows of 16 px high, its corner 48 px left of the clicked tile's, kept
    /// inside the frame (FUN_0041855c). The question, an etched rule, then はい / いいえ 36 px wide in the middle.
    /// </summary>
    void OpenConfirm(int word, State state)
    {
        int units = Math.Max(3, (Bin.Sjis.GetByteCount(Word(word)) + 1) / 2) + 2, w = units * 12, h = 5 * 16;
        _quit.Rect = new Rectangle(Math.Clamp(_turnMenuAt.X - 48, 16, 624 - w), Math.Clamp(_turnMenuAt.Y, 16, 464 - h), w, h);
        _quit.ItemX = w / 2 - 18;
        _quit.Header = new[] { Word(word), "-" };
        _quit.Set(new[] { Word(45), Word(46) });   // はい / いいえ
        _quit.Index = 0;
        _state_ = state;
        _pointMenu = true;
    }

    /// <summary>FUN_00410618: はい ends the turn, the pointer going back where the turn menu was opened; いいえ or a right click go back to ターン終了.</summary>
    void UpdateEndTurnConfirm(BattleInput i)
    {
        if (i.Cancel || i.RightClick) { ReopenTurnMenu(0); return; }
        if (_quit.Navigate(i, MouseVirtual) is var c and >= 0)
        {
            if (c == 0) { RestorePointer(0); EndPlayerTurn(); }
            else ReopenTurnMenu(0);
        }
    }

    /// <summary>Scripted runs ("turnmenu"): the turn menu, as a click on an empty tile opens it, when the player's phase waits on the map.</summary>
    public bool ScriptTurnMenu()
    {
        if (_state_ != State.Idle || !_s.Field.AllyPhase) return false;
        OpenTurnMenu();
        return true;
    }

    /// <summary>Scripted runs ("endturn"): ターン終了 as the turn menu gives it, when the player's phase waits on the map.</summary>
    public bool ScriptEndTurn()
    {
        if (_state_ != State.Idle || !_s.Field.AllyPhase) return false;
        EndPlayerTurn();
        return true;
    }

    /// <summary>
    /// ターン終了 (FUN_00410618 → FUN_00410b90): the phase changes (upkeep for the enemies), ENEMY TURN, then the
    /// checkpoint before the first enemy moves.
    /// </summary>
    void EndPlayerTurn()
    {
        _s.Field.NextPhase();          // enemy phase
        _state_ = State.EnemyPhase;
        _plan = null;
        (_group, _aiSlot) = (1, 0);
        TurnTelop(2, () => { if (!CheckPoint()) _state_ = State.EnemyPhase; });
    }

    bool _planWalking;
    /// <summary>Who moves now in the computer's phase: 1 the enemies, then 2 the neutral units (FUN_0040e658).</summary>
    int _group = 1;
    /// <summary>The record number the computer's phase has come to (+0x51c0): the unit taking its turn, or the last that did.</summary>
    int _aiSlot;

    /// <summary>Option: the enemy phase resolves without walking, panning, battle scenes or result popups (events still play).</summary>
    public bool SkipEnemyTurn { get; set; }
    /// <summary>Option (modern): the player's turn ends by itself once every unit of theirs has acted (<see cref="TurnIsOver"/>).</summary>
    public bool AutoEndTurn { get; set; }
    /// <summary>Option (modern): off, a fight plays the phase's music instead of the map's battle BGM (BattleMusic).</summary>
    public bool CombatMusic { get; set; } = true;
    bool Quiet => SkipEnemyTurn && !_s.Field.AllyPhase;

    /// <summary>
    /// The computer's phase (FUN_0040e658): a counter goes up the record numbers, giving each enemy that hasn't acted
    /// its turn; past the last record it goes round again from the first while an enemy still hasn't (one given an extra
    /// action by a later one acts then), and then the neutral units get one round. Each one's AI decides, the camera
    /// shows it and where it's headed (FUN_004170a0) while it walks, it acts, and the checkpoint runs before the next. A
    /// unit that's asleep or has nowhere to go is done at once (no camera); one whose way is blocked, or that's already
    /// where it's going, still takes its turn without a step. Then the camera goes to the first player unit and the
    /// player's phase begins.
    /// </summary>
    void UpdateEnemyPhase(BattleInput i)
    {
        if (_plan is { } p)
        {
            if (!_planWalking)
            {
                if (Quiet) (p.Unit.X, p.Unit.Y) = p.Destination;
                else
                {
                    // an enemy's turn shows the unit and what it's after: the unit it attacks, the spell's aim (a spell over
                    // the whole map: where it casts from), else where it stops (FUN_00416334); a neutral unit's, the unit
                    (int X, int Y)? focus = _group == 2 ? null
                        : p.Target is { } aimed ? (aimed.X, aimed.Y) : p.Spell is { EffectWholeMap: false } ? p.Aim : p.Destination;
                    _s.ShowTile(p.Unit.X, p.Unit.Y, focus);
                    _s.Walk(p.Unit, p.Path);
                }
                _planWalking = true;
                return;
            }
            if (_s.MovesRunning) return;
            _plan = null;
            _actor = p.Unit;
            // the move's end: tile marks and contacts; a contact event takes the unit's action away (and it has acted)
            if (MoveEnded(p.Unit, p.Path)) return;
            // a spell or an attack: the unit has acted once its scene is over (FUN_00419574); one that only walked, now
            if (p.Spell is { } sp && Battlefield.CanCast(p.Unit, sp) && _s.Field.SpellTargets(p.Unit, sp, p.Aim.X, p.Aim.Y).Count > 0)
            {
                _cursor = new Point(p.Aim.X, p.Aim.Y);
                Cast(p.Unit, sp, p.Aim, null);
                return;
            }
            if (p.Target is { Dead: false } t && _s.Field.InRange(p.Unit, p.Unit.X, p.Unit.Y, t)) { Attack(p.Unit, t); return; }
            // the next unit's turn comes the next tick, no pause between them (FUN_0040ecf0, FUN_0040e658)
            p.Unit.Acted = true;
            CheckPoint();
            return;
        }
        while (EnemyAi.NextInPhase(_s.Field, ref _group, ref _aiSlot) is { } u)
        {
            var plan = EnemyAi.Plan(_s.Field, u, _state.Switches);
            if (!plan.Acts)
            {
                // asleep, or nowhere to go: done at once (FUN_0041589c returns 0); with nothing to do, an extra action
                // would be wasted on it (+0x30)
                u.Acted = true;
                if (u.SleepTurns == 0) u.NoMoreTurns = true;
                continue;
            }
            _plan = plan;
            _planWalking = false;
            return;
        }
        // the computer's phase is over: the camera to the first player unit, the player's phase (upkeep), PLAYER TURN
        _group = 1;
        if (!Quiet && _s.FirstPlayerUnit() is { } f) _s.ShowTile(f.X, f.Y);
        _s.Field.NextPhase();
        _state_ = State.Idle;
        TurnTelop(1, () => { if (!CheckPoint()) _state_ = State.Idle; });
    }

    // ---------------- drawing ----------------

    /// <summary>
    /// The map's overlays (FUN_004132a8): a tile with an area value (+0x848) shows that, else its range value (+0x208), as
    /// SYSTEM.BMP's range block of that number less one, in stripes — 1 cyan: a move, or the actor of an action under
    /// way; 2 salmon: the reach of an attack, a spell or a medicine; 3 orange: the area of an attack or a harmful spell;
    /// 4 blue: a support spell's or a medicine's — and solid (the value + 4) where a unit the action reaches stands. The
    /// area follows the cursor over the reach: its tile for an attack (FUN_00410190) or a medicine (FUN_00411c90), the
    /// spell's effect grid around it (FUN_00411a48), the whole map for a spell that covers it. A clicked enemy's attack
    /// range shows salmon around its cyan movement (modern).
    /// </summary>
    public void DrawOverlays(SpriteBatch sb, Rectangle viewport, int frame)
    {
        var at = (_cursor.X, _cursor.Y);
        void Draw(IEnumerable<(int X, int Y)> reach, int reachColour, int colour, IReadOnlySet<(int X, int Y)> area, IEnumerable<(int X, int Y)> met)
        {
            _s.DrawRange(sb, viewport, reach.Where(t => !area.Contains(t)), reachColour, frame);
            _s.DrawRange(sb, viewport, area, colour, frame);
            _s.DrawRange(sb, viewport, met, colour, frame, solid: true);
        }
        IReadOnlySet<(int X, int Y)> Cursor(bool on) => on ? new HashSet<(int X, int Y)> { at } : new HashSet<(int X, int Y)>();
        switch (_state_)
        {
            case State.Selected when _reach is not null:
                _s.DrawRange(sb, viewport, _reachShown ?? _reach.Keys.ToHashSet(), 0, frame);
                if (_reachHits is not null) _s.DrawRange(sb, viewport, _reachHits, 1, frame);
                break;
            case State.Targeting when _sel is not null:
            {
                var reach = AttackReach(_sel);
                bool on = reach.Contains(at);
                Draw(reach, 1, 2, Cursor(on), _targets.Where(t => on && (t.X, t.Y) == at).Select(t => (t.X, t.Y)));
                break;
            }
            case State.Aiming when _aimSpell is { } m && _sel is not null:
            {
                (int X, int Y)? aim = m.EffectWholeMap ? (0, 0) : _aimRange.Contains(at) ? at : null;
                var area = aim is { } a ? _s.Field.SpellArea(m, a.X, a.Y).ToHashSet() : new HashSet<(int X, int Y)>();
                var met = aim is { } b ? _s.Field.SpellTargets(_sel, m, b.X, b.Y).Select(u => (u.X, u.Y)) : Enumerable.Empty<(int X, int Y)>();
                Draw(_aimRange, 1, m.Type is 0 or 1 ? 2 : 3, area, met);
                break;
            }
            case State.MedicineTarget when _sel is not null:
            {
                bool on = _medRange.Contains(at);
                Draw(_medRange, 1, 3, Cursor(on), _medTargets.Where(t => on && (t.X, t.Y) == at).Select(t => (t.X, t.Y)));
                break;
            }
            case State.Acting or State.Events or State.Scene when _action is { } o && _scene?.OverMap != false:
                Draw(new[] { o.Actor }, 0, o.Colour, o.Area, o.Met);
                break;
        }
    }

    /// <summary>The enemy ranges key (a modern aid): every enemy's reach for its coming phase, shown as one area — blue where they can move, red where they can only hit.</summary>
    public bool ShowThreat { get; set; }
    HashSet<(int X, int Y)> _threat = new(), _threatStops = new(), _threatAll = new();
    int? _threatKey;

    /// <summary>
    /// The union of the enemies' threats (<see cref="EnemyAi.Threat"/>): the tiles they could stop on, those they could
    /// hit, and both together — worked out again only when something they depend on changes.
    /// </summary>
    (HashSet<(int X, int Y)> Stops, HashSet<(int X, int Y)> Hits, HashSet<(int X, int Y)> All) Threat()
    {
        var key = new HashCode();
        foreach (var u in _s.Field.Units)
        {
            key.Add(u.X); key.Add(u.Y); key.Add(u.Dead); key.Add(u.Hidden); key.Add(u.SleepTurns);
            key.Add(u.Mp); key.Add(u.Weapon); key.Add(u.ClassId); key.Add(u.Level); key.Add(u.Accessory);
        }
        foreach (bool sw in _state.Switches) key.Add(sw);
        int k = key.ToHashCode();
        if (k == _threatKey) return (_threatStops, _threat, _threatAll);
        _threatKey = k;
        _threat = new HashSet<(int X, int Y)>();
        _threatStops = new HashSet<(int X, int Y)>();
        foreach (var e in _s.Field.Alive.Where(u => u.Side == Side.Enemy))
        {
            var (stops, hits) = EnemyAi.Threat(_s.Field, e, _state.Switches);
            _threatStops.UnionWith(stops);
            _threat.UnionWith(hits);
        }
        _threatAll = _threat.Union(_threatStops).ToHashSet();
        return (_threatStops, _threat, _threatAll);
    }

    /// <summary>
    /// The enemies' threat as one block: a translucent fill — blue where an enemy could stop, red where it could only
    /// hit — with a bright red line along the block's outer edge and a light blue one around the blue inside it.
    /// </summary>
    public void DrawThreat(SpriteBatch sb, Rectangle viewport)
    {
        if (!ShowThreat) return;
        var (stops, _, tiles) = Threat();
        var fill = new Color(208, 24, 32) * 0.36f;
        var edge = new Color(255, 84, 84) * 0.95f;
        // the movement range's own shades, the game's SYSTEM.BMP range block 0 (cyan in most games, green in some): the
        // darker one fills, the lighter one draws the line
        var (dark, light) = _s.RangeShades(0);
        var moveFill = dark * 0.6f;
        var moveEdge = light * 0.95f;
        const int T = MapSession.Tile;
        foreach (var (x, y) in tiles)
        {
            var p = _s.ScreenPos(x, y, viewport).ToPoint();
            if (p.X + T <= viewport.X || p.Y + T <= viewport.Y || p.X >= viewport.Right || p.Y >= viewport.Bottom) continue;
            bool move = stops.Contains((x, y));
            _ui.Fill(sb, new Rectangle(p.X, p.Y, T, T), move ? moveFill : fill);
            // each side: the block's outer edge red; inside it, the blue's edge where it meets the red
            Color? Edge((int X, int Y) n) => !tiles.Contains(n) ? edge : move && !stops.Contains(n) ? moveEdge : null;
            if (Edge((x, y - 1)) is { } top) _ui.Fill(sb, new Rectangle(p.X, p.Y, T, 2), top);
            if (Edge((x, y + 1)) is { } bottom) _ui.Fill(sb, new Rectangle(p.X, p.Y + T - 2, T, 2), bottom);
            if (Edge((x - 1, y)) is { } left) _ui.Fill(sb, new Rectangle(p.X, p.Y, 2, T), left);
            if (Edge((x + 1, y)) is { } right) _ui.Fill(sb, new Rectangle(p.X + T - 2, p.Y, 2, T), right);
        }
    }

    /// <summary>The tiles a unit's weapon reaches from where it stands (its range's distances).</summary>
    List<(int X, int Y)> AttackReach(BattleUnit u)
    {
        var st = _s.Field.Stats(u);
        var tiles = new List<(int X, int Y)>();
        for (int y = u.Y - st.RangeMax; y <= u.Y + st.RangeMax; y++)
            for (int x = u.X - st.RangeMax; x <= u.X + st.RangeMax; x++)
                if (Math.Abs(x - u.X) + Math.Abs(y - u.Y) is var d && d >= st.RangeMin && d <= st.RangeMax && _s.Field.InBounds(x, y)) tiles.Add((x, y));
        return tiles;
    }

    public void DrawUi(SpriteBatch sb, Rectangle viewport, int frame)
    {
        if (CursorShown())
        {
            // SYSTEM.BMP's dashed square, its dashes moving on every 2 ticks: (96,0), (64,32), (96,32) (FUN_004132a8)
            var p = _s.ScreenPos(_cursor.X, _cursor.Y, viewport).ToPoint();
            int phase = frame / 4 % 3;
            if (_tex.Get("SYSTEM.BMP") is { } sys)
                sb.Draw(sys, p.ToVector2(), new Rectangle(phase == 1 ? 64 : 96, phase == 0 ? 0 : 32, 32, 32), Color.White);
            if (_state_ == State.Idle && _s.Field.UnitAt(_cursor.X, _cursor.Y) is { } u) DrawHoverLabel(sb, u, p);
        }
        bool terrain = _state_ is State.Idle or State.Selected or State.Aiming or State.MedicineTarget;
        if (terrain && _s.Field.InBounds(_cursor.X, _cursor.Y)) DrawTerrainInfo(sb);   // modern addition (FE-style)
        if (_state_ is State.ActionMenu or State.TurnMenu && !_s.Panning) DrawMenu(sb, frame);
        if (_state_ == State.Targeting && _sel is not null && _targets.FirstOrDefault(t => t.X == _cursor.X && t.Y == _cursor.Y) is { } ft)
            DrawForecast(sb, _sel, ft);
        // a spell's aim: the forecast for one of the units the spell would reach from there (FocusAim) — with more than
        // one, that unit framed and the count in the forecast's top line
        if (_state_ == State.Aiming && _sel is not null && _aimSpell is { } spell && _aimFocus is { } st && _aimTargets.IndexOf(st) is var k and >= 0)
        {
            bool several = _aimTargets.Count > 1;
            if (several) DrawFocusFrame(sb, st, frame);
            DrawSpellForecast(sb, _sel, spell, st, _aimItem is not null, several ? $"{k + 1}/{_aimTargets.Count}" : null);
        }
        if (_state_ == State.WholeMap)
            _s.DrawWholeMap(sb, _ui, _s.Field.Alive.Where(u => !u.Hidden).Select(u => (u.X, u.Y, u.Side switch { Side.Enemy => 0, Side.Ally => 1, _ => 2 })), frame);
        if (_state_ == State.Files)
        {
            if (_saveMenu is not null) _saveMenu.Draw(sb, frame);
            else _files.Draw(sb, _ui, true, frame);
        }
        if (_state_ is State.EndTurnConfirm) _quit.Draw(sb, _ui, true, frame);
        if (_state_ is State.SpellList or State.ItemList or State.EquipList or State.EquipSlots) DrawList(sb, frame);
        if (_state_ == State.UseConfirm) _useMenu.Draw(sb, _ui, true, frame);
        if (_state_ == State.Showing && _panel is not null) DrawPanel(sb, _panel);
        if (_state_ == State.Status && _statusUnit is not null)
            Ui.StatusWindow.Draw(sb, _ui, _tex, _db, _s.Field, _statusUnit, shown: _statusItem, mouse: MouseVirtual,
                page: _statusPage, skill: _statusSkill, top: _statusTop, arrows: true, frame: frame);
        if (_scene is not null) { _scene.Draw(sb); if (_state_ == State.Showing && _panel is not null) DrawPanel(sb, _panel); }
        WarmProfile(sb, frame);
        // the caption over everything, the screen frame included (FUN_0041421c draws it at y 192 on the whole screen)
        if (_telop is not null && _tex.Get(_telop) is { } t) sb.Draw(t, new Vector2(TelopX(_telopTick), 192), Color.White);
    }

    // profiles made ahead while the map waits for the player (StatusWindow.Warm): one unit looked at a frame, made again
    // when anything its window shows has changed, so opening one never stalls on making its text and pictures
    readonly Dictionary<BattleUnit, int> _warmed = new();
    int _warmNext;

    void WarmProfile(SpriteBatch sb, int frame)
    {
        if (_state_ is not (State.Idle or State.Selected) || _scene is not null) return;
        var units = _s.Field.Units;
        if (units.Count == 0) return;
        var u = units[_warmNext = (_warmNext + 1) % units.Count];
        if (u.Dead || u.Hidden) return;
        var sig = HashCode.Combine(HashCode.Combine(u.Hp, u.MaxHp, u.Mp, u.MaxMp, u.Level, u.Exp, u.Weapon, u.Armor),
            HashCode.Combine(u.Accessory, _s.Field.Stats(u), _s.Field.Class(u).Name, _s.Field.KnownSpells(u).Count()),
            HashCode.Combine(Ui.Ui.Skin, Ui.Ui.Font, Ui.Ui.RenderScale, Ui.StatusWindow.Orbs, Ui.Lang.Japanese));
        if (_warmed.TryGetValue(u, out var had) && had == sig) return;
        _warmed[u] = sig;
        Ui.StatusWindow.Warm(sb, _ui, _tex, _db, _s.Field, u, frame);
    }

    /// <summary>
    /// The tile cursor (FUN_004132a8): on the map while it waits for the player; with a unit chosen only on the tiles it
    /// can move to (not for a unit of the computer's), while choosing a target or an aim only on tiles in range.
    /// </summary>
    bool CursorShown()
    {
        if (!_s.Field.InBounds(_cursor.X, _cursor.Y)) return false;
        var c = (_cursor.X, _cursor.Y);
        return _state_ switch
        {
            State.Idle => true,
            State.Selected => _reach is not null && _reach.ContainsKey(c) && _sel is { Side: not Side.Enemy },
            State.Targeting => _sel is not null && _s.Field.Stats(_sel) is var st
                && Math.Abs(_cursor.X - _sel.X) + Math.Abs(_cursor.Y - _sel.Y) is var d && d >= st.RangeMin && d <= st.RangeMax,
            State.Aiming => _aimRange.Contains(c),
            State.MedicineTarget => _medRange.Contains(c),
            _ => false,
        };
    }

    /// <summary>
    /// The hovered unit's name and "HP／max" while the map waits (FUN_00413918): 12-px text in its side's light colour
    /// (ally blue, enemy red, neutral yellow) with a 1-px outline in the dark one; the name centred 34 px above the tile,
    /// the numbers ("%2d／%2d") 20 px above it, both kept inside the frame and put below the tile near the top.
    /// </summary>
    void DrawHoverLabel(SpriteBatch sb, BattleUnit u, Point tile)
    {
        var (light, dark) = Ui.Ui.Restyled ? (Color.White, Color.Black)
            : Ui.Ui.EdgeColors(u.Side switch { Side.Enemy => Ui.Ui.PanelColor.Red, Side.Neutral => Ui.Ui.PanelColor.Yellow, _ => Ui.Ui.PanelColor.Blue });
        void Outlined(string s, int x, int y)
        {
            int w = Bin.Sjis.GetByteCount(s) * 6;
            x = Math.Min(Math.Max(x, 16), 624 - w);
            if (y < 14) y += 68;
            foreach (var (dx, dy) in new[] { (1, 0), (0, 1), (-1, 0), (0, -1) }) _ui.Text12(sb, s, x + dx, y + dy, dark);
            _ui.Text12(sb, s, x, y, light);
        }
        Outlined(u.Name, tile.X + 14 - Bin.Sjis.GetByteCount(u.Name) * 6 / 2, tile.Y - 34);
        Outlined($"{u.Hp,2}／{u.MaxHp,2}", tile.X - 1, tile.Y - 20);
    }

    /// <summary>The action or turn menu (FUN_00414344, FUN_004144f4): entries at x + 12, 16-px rows from y + 8, the bar on the chosen one.</summary>
    void DrawMenu(SpriteBatch sb, int frame)
    {
        var r = MenuRect();
        _ui.Panel(sb, r);
        for (int i = 0; i < _menu.Length; i++)
        {
            var c = _ui.MenuRow(sb, MenuBarRect(r, i), i == _menuIndex, _menuEnabled[i], frame);
            _ui.Text12(sb, _menu[i], r.X + 12, r.Y + 8 + i * 16, c);
        }
    }

    /// <summary>The item or spell a list row stands for (the description window's subject), if any.</summary>
    (string Name, string Desc, Item? Item)? RowSubject(int i)
    {
        if (i < 0 || i >= _menu.Length) return null;
        switch (_state_)
        {
            case State.SpellList: return (_spells[i].Name, _spells[i].Desc, null);
            case State.ItemList: return (_items[i].Name, _items[i].Desc, _items[i]);
            case State.EquipList: return _equipRows[i] is { } e ? (e.Name, e.Desc, e) : null;   // （装備をはずす）: nothing
            case State.EquipSlots:
                int held = i switch { 0 => _sel?.Weapon ?? -1, 1 => _sel?.Armor ?? -1, _ => _sel?.Accessory ?? -1 };
                return held >= 0 && held < _db.Items.Length ? (_db.Items[held].Name, _db.Items[held].Desc, _db.Items[held]) : null;
        }
        return null;
    }

    /// <summary>
    /// A list (FUN_00414344 / FUN_004144f4): each row's text at the panel's x + 12, 16-px rows from y + 8; greyed rows can't be
    /// picked. Item and equipment lists have a footer below their 12th row (always for items, for equipment when there's
    /// more than one page): an etched rule, 「ページ ｎ／ｍ」 and the arrows from SYSTEM.BMP (y 304: ◁ x 0, ▷ x 32). The hovered
    /// entry's 「name：」 and description fill the window at (80,32) 480×48 (FUN_004142d4).
    /// </summary>
    void DrawList(SpriteBatch sb, int frame)
    {
        var r = ListRect();
        _ui.Panel(sb, r);
        int first = Paged ? _itemPage * ItemRows : 0;
        int count = Paged ? Math.Min(ItemRows, _menu.Length - first) : _menu.Length;
        for (int k = 0; k < count; k++)
        {
            int i = first + k;
            var row = RowRect(i);
            _ui.Text12(sb, _menu[i], r.X + 12, row.Y, _ui.MenuRow(sb, row, i == _menuIndex, _menuEnabled[i], frame));
        }
        int pages = Math.Max(1, (_menu.Length + ItemRows - 1) / ItemRows);
        if (_state_ == State.ItemList || (_state_ == State.EquipList && pages > 1))
            Camp.MenuList.PageFooter(sb, _ui, r, r.Y + 8 + ItemRows * 16, _itemPage, pages, -1);
        // the description window (FUN_004142d4) stays while the list is open, empty over a row with nothing to describe
        var d = new Rectangle(80, 32, 480, 48);
        _ui.Panel(sb, d);
        if (RowSubject(_menuIndex) is { } sub)
        {
            _ui.Text12(sb, $"{sub.Name}：", d.X + 12, d.Y + 8, Ui.Ui.PanelText);
            if (sub.Item is { } it && _ui.SmallText.Get(Ui.ItemInfo.Summary(it, _db)) is { } st)   // modern addition
                st.Draw(sb, new Vector2(d.Right - 16 - st.Width, d.Y + 10), Ui.Ui.StatText);
            _ui.Text12(sb, sub.Desc, d.X + 12, d.Y + 24, Ui.Ui.PanelText);
        }
    }

    /// <summary>A message panel (see <see cref="Panel"/>); its lines at GDI's (+12, +8), 16 px apart.</summary>
    void DrawPanel(SpriteBatch sb, Panel p)
    {
        int w = p.Units * 12;
        var r = new Rectangle(p.Auto ? 320 - w / 2 : p.X, 64, w, (p.Lines.Length + 1) * 16);
        _ui.Panel(sb, r);
        for (int k = 0; k < p.Lines.Length; k++) _ui.Text12(sb, p.Lines[k], r.X + 12, r.Y + 8 + k * 16, Ui.Ui.PanelText);
    }
}
