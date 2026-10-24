using BreezySRPG95.Runtime;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Srpg95.Data;
using Srpg95.Rules;

namespace BreezySRPG95;

/// <summary>Per-unit animation state (the rules state lives in <see cref="BattleUnit"/>).</summary>
public sealed class UnitVisual
{
    /// <summary>The way it last stepped or turned, as its sprite-sheet row (FUN_00413918): 1 up, 2 right, 3 down, 4 left; 0 none yet.</summary>
    public int Facing;
    public Queue<byte> Pending { get; } = new();
    public Queue<byte> Running { get; } = new();
    /// <summary>The running steps come from an event's move command: a step onto an occupied or impassable tile is skipped.</summary>
    public bool Scripted;
    public Vector2 Offset;
    public Vector2 StepFrom;   // pixel offset at the start of the current step
    public int StepFrames;
    public int StepLength;     // the frames this step takes in all
}

/// <summary>Runtime state of one battle map: the rules battlefield plus drawing and animation.</summary>
public sealed class MapSession : IMapContext
{
    public const int Tile = Chipset.ChipSize;   // 32 px
    /// <summary>A step takes 8 ticks of the original's 30 Hz loop (FUN_0040ecf0, FUN_00424a68): 16 frames here.</summary>
    const int FramesPerTile = 16;

    /// <summary>Option (modern): how many times the original's pace units walk on the battle map (event moves keep the original's).</summary>
    public int WalkSpeed { get; set; } = 2;
    /// <summary>The view in tiles: 608×448 px.</summary>
    const int ViewTilesX = 19, ViewTilesY = 14;

    readonly Database _db;
    readonly GameState _state;
    readonly Textures _tex;
    readonly Texture2D? _chips;
    readonly Dictionary<int, Texture2D?> _unitSheets = new();

    public MapData Map { get; }
    public Battlefield Field { get; }
    public Dictionary<BattleUnit, UnitVisual> Visuals { get; } = new();
    /// <summary>Units already dead in the rules but still drawn until their defeat has been shown.</summary>
    public HashSet<BattleUnit> Ghosts { get; } = new();
    /// <summary>"Other" objects (event tiles, graphics).</summary>
    public List<MapObject> Others { get; }
    public Point Camera;
    public int Phase => Field.Phase;
    public int PixelWidth => Map.Width * Tile;
    public int PixelHeight => Map.Height * Tile;

    /// <summary>
    /// A map as FUN_0041dc34 sets it up: the army first (roster units with 出撃, in roster order, each on the first
    /// start position nobody holds yet), then the map's objects in index order wherever their switch condition holds
    /// and their tile is free; the camera starts on the first player unit.
    /// </summary>
    public MapSession(MapData map, Database db, GameState state, Textures tex, byte[]? chipTerrain, bool deploy = true)
    {
        Map = map;
        _db = db;
        _state = state;
        _tex = tex;
        _chips = tex.Get($"MAPC_{map.Tileset:000}.BMP", transparent: false);
        Field = new Battlefield(map, db, chipTerrain) { ChipOverride = OtherChipAt };
        Others = map.Objects.Where(o => o.InUse && o.Type == ObjectType.Other).ToList();
        if (deploy) DeployRoster();
        foreach (var o in map.Objects.Where(o => o.InUse && o.Type != ObjectType.Other).OrderBy(o => o.Index))
        {
            var u = ObjectUnit(o);
            u.Hidden = true;
            Add(u);
        }
        RefreshSpawns();
        if (FirstPlayerUnit() is { } first) StartCamera(first.X, first.Y);
    }

    void Add(BattleUnit u)
    {
        Field.Units.Add(u);
        Visuals[u] = new UnitVisual();
    }

    MapObject Object(int index) => Map.Objects.First(o => o.Index == index);

    bool SwitchOn(int sw) => sw >= 0 && sw < GameState.SwitchCount && _state.Switches[sw];

    /// <summary>
    /// A map object's unit, always from UNIT.DAT (FUN_00409658): level, stats and class as listed, and each piece of
    /// equipment only if it is of the right type and the class may use it.
    /// </summary>
    BattleUnit ObjectUnit(MapObject o)
    {
        var u = _db.Units[Math.Clamp(o.Unit, 0, _db.Units.Length - 1)];
        var (w, a, x) = UnitChanges.StartingEquipment(_db, u);
        return new BattleUnit
        {
            Unit = o.Unit, Side = SideOf(o), MapObject = o.Index, Name = u.Name,
            ClassId = u.ClassId, Level = u.Level, Exp = u.Exp, Base = (int[])u.BaseStats.Clone(), Growth = u.GrowthPct,
            Hp = u.BaseStats[0], Mp = u.BaseStats[1], Weapon = w, Armor = a, Accessory = x,
            X = o.X, Y = o.Y, Leader = o.Leader, Guest = o.Guest, Unattackable = o.Unattackable,
            MovePatterns = o.MovePatterns,
        };
    }

    static Side SideOf(MapObject o) => o.Type switch { ObjectType.Ally => Side.Ally, ObjectType.Enemy => Side.Enemy, _ => Side.Neutral };

    /// <summary>A unit coming back after leaving the map starts afresh from UNIT.DAT, as the exe rebuilds its record.</summary>
    void Respawn(BattleUnit u, MapObject o)
    {
        var fresh = ObjectUnit(o);
        u.Side = fresh.Side;
        u.ClassId = fresh.ClassId;
        u.Level = fresh.Level;
        u.Exp = fresh.Exp;
        Array.Copy(fresh.Base, u.Base, 6);
        (u.Hp, u.Mp, u.Weapon, u.Armor, u.Accessory) = (fresh.Hp, fresh.Mp, fresh.Weapon, fresh.Armor, fresh.Accessory);
        u.Guest = o.Guest;
        (u.PowerUp, u.DefUp, u.MdefUp, u.PoisonTurns, u.SleepTurns, u.Acted) = (0, 0, 0, 0, 0, false);
    }

    /// <summary>The army: roster units with 出撃, in roster order, on the first start position no one holds yet.</summary>
    void DeployRoster()
    {
        foreach (var (id, e) in _state.Roster)
        {
            if (!e.Sortie) continue;
            var pos = Map.StartPositions.Where(p => Field.UnitAt(p.X, p.Y) is null).Select(p => ((int X, int Y)?)p).FirstOrDefault();
            if (pos is not { } p || FreeSlot() > MaxRecord) continue;
            var u = _db.Units[Math.Clamp(id, 0, _db.Units.Length - 1)];
            var unit = new BattleUnit
            {
                Unit = id, Side = Side.Ally, Name = u.Name, ClassId = e.ClassId, Level = e.Level, Exp = e.Exp,
                Base = (int[])e.Base.Clone(), Growth = u.GrowthPct, Hp = e.Base[0], Mp = e.Base[1],
                Weapon = e.Weapon, Armor = e.Armor, Accessory = e.Accessory, X = p.X, Y = p.Y, Appeared = true, FromRoster = true,
            };
            unit.Slot = FreeSlot();
            Add(unit);
        }
    }

    /// <summary>The exe keeps 99 unit records (1–99): with all of them taken, no more units come onto the map.</summary>
    const int MaxRecord = Battlefield.MaxRecord;

    /// <summary>The lowest record number no unit on the map holds (fallen units keep theirs).</summary>
    int FreeSlot()
    {
        var used = Field.Units.Where(u => !u.Hidden && u.Slot > 0).Select(u => u.Slot).ToHashSet();
        int s = 1;
        while (used.Contains(s)) s++;
        return s;
    }

    /// <summary>
    /// FUN_00409658, run when a map starts and whenever a switch changes: units whose object's switch condition is now
    /// OFF leave the map (the object waits where they stood); objects whose condition holds come onto the map, in index
    /// order, if nobody alive stands on their tile and one of the 99 unit records is free (else it keeps waiting) — a
    /// unit that had been on the map before starts afresh.
    /// </summary>
    public void RefreshSpawns()
    {
        foreach (var u in Field.Units.Where(u => !u.Hidden && u.MapObject >= 0 && !u.ForceShown).ToList())
            if (Object(u.MapObject).SwitchCondition is var sc and >= 0 && !SwitchOn(sc))
            {
                u.Hidden = true;
                u.Slot = 0;
                Ghosts.Remove(u);
            }
        foreach (var u in Field.Units.Where(u => u.Hidden && u.MapObject >= 0).OrderBy(u => u.MapObject).ToList())
        {
            var o = Object(u.MapObject);
            if (o.SwitchCondition >= 0 && !SwitchOn(o.SwitchCondition)) continue;
            if (Field.UnitAt(u.X, u.Y) is not null) continue;
            int slot = FreeSlot();
            if (slot > MaxRecord) continue;
            if (u.Appeared) Respawn(u, o);
            (u.Hidden, u.Appeared, u.Dead, u.Cleared) = (false, true, false, false);
            u.Slot = slot;
        }
    }

    /// <summary>Units on the map in record order (the order the exe walks them in), fallen ones included.</summary>
    public IEnumerable<BattleUnit> InRecordOrder => Field.Units.Where(u => !u.Hidden).OrderBy(u => u.Slot);

    /// <summary>The first player-side unit (ally or neutral) in record order, as the camera looks for it.</summary>
    public BattleUnit? FirstPlayerUnit() => InRecordOrder.FirstOrDefault(u => !u.Dead && u.Side != Side.Enemy);

    /// <summary>
    /// Puts a battle save's units on the map: army units come from their records; units of map objects replace the
    /// object's own (by object index); objects the save never had on the map stay pending (hidden by their switch).
    /// </summary>
    public void RestoreFrom(SaveFile save)
    {
        foreach (var u in Field.Units.Where(u => u.MapObject < 0).ToList())
        {
            Field.Units.Remove(u);
            Visuals.Remove(u);
        }
        foreach (var u in Field.Units)
        {
            (u.Hidden, u.Slot) = (true, 0);
            // an object still waiting to come on: where the save's object table has it
            var (pending, ox, oy) = save.ObjectState(u.MapObject);
            if (pending) { u.Appeared = (ox, oy) != (Object(u.MapObject).X, Object(u.MapObject).Y); (u.X, u.Y) = (ox, oy); }
        }
        foreach (var r in save.MapUnitRecords())
        {
            var u = r.Object >= 0 ? Field.Units.FirstOrDefault(x => x.MapObject == r.Object) : null;
            if (u is null)
            {
                if (r.Object >= 0) continue;
                var d = _db.Units[Math.Clamp(r.Unit, 0, _db.Units.Length - 1)];
                u = new BattleUnit { Unit = r.Unit, Side = Side.Ally, Name = d.Name, Growth = d.GrowthPct, Base = new int[6] };
                Add(u);
            }
            u.ClassId = r.ClassId;
            u.Level = r.Level;
            u.Exp = r.Exp;
            Array.Copy(r.Base, u.Base, 6);
            (u.Hp, u.Mp, u.Weapon, u.Armor, u.Accessory) = (r.Hp, r.Mp, r.Weapon, r.Armor, r.Accessory);
            (u.X, u.Y, u.Acted, u.Dead, u.Cleared) = (r.X, r.Y, r.Acted, r.Hp <= 0, !r.Alive);
            u.Side = r.Computer ? Side.Enemy : r.Neutral ? Side.Neutral : Side.Ally;
            u.Guest = r.Guest;
            u.FromRoster = r.FromRoster;
            (u.PowerUp, u.DefUp, u.MdefUp, u.PoisonTurns, u.SleepTurns) = (r.PowerUp, r.DefUp, r.MdefUp, r.PoisonTurns, r.SleepTurns);
            u.NoMoreTurns = r.NoMoreTurns;
            (u.Hidden, u.Appeared, u.Slot) = (false, true, r.Record);
        }
        Field.SetPhase(Math.Max(1, save.Phase));
        RefreshSpawns();   // the exe runs FUN_00409658 after loading too
        // the camera where the save has it (header 0x008 / 0x00C: a load turns scrolling off and leaves it there, FUN_0040e334)
        var (cx, cy) = save.CameraTile;
        Camera = new Point(cx * Tile, cy * Tile);
        ClampCamera();
        _panTarget = null;
    }

    public bool OtherVisible(MapObject o) => o.SwitchCondition < 0 || SwitchOn(o.SwitchCondition);

    /// <summary>FUN_0040c1d4: an "Other" object shown on a tile puts its graphic's chip there (terrain, movement, recovery).</summary>
    int? OtherChipAt(int x, int y) =>
        Others.FirstOrDefault(o => o.X == x && o.Y == y && o.Graphic >= 0 && OtherVisible(o))?.Graphic;

    /// <summary>A map's end: the army goes back into the roster (FUN_0041d5e4).</summary>
    public void SaveRoster() => Party.SaveFromMap(_state, Field);

    // ---------------- IMapContext (event interpreter) ----------------

    /// <summary>
    /// FUN_00424ee0's unit conditions, record by record as the exe keeps them: 0 alive = on the map (a fallen unit
    /// still counts until the checkpoint takes it off), 1 dead = taken off after falling, 2 not present = no record at
    /// all (a fallen unit is still "present"), 3 player-controlled = an ally record or in the roster, 4 = neither.
    /// The roster lookup skips the first entry, as the exe's loop does.
    /// </summary>
    public bool UnitState(int unit, int state)
    {
        var records = Field.Units.Where(u => u.Unit == unit && !u.Hidden).ToList();
        bool present = records.Any(u => !(u.Dead && u.Cleared));
        bool player = records.Any(u => u.Side == Side.Ally) || _state.Roster.Keys.Skip(1).Contains(unit);
        return state switch
        {
            0 => present,
            1 => !present && records.Any(u => u.Dead && u.Cleared),
            2 => records.Count == 0,
            3 => player,
            4 => !player,
            _ => false,
        };
    }

    public void SwitchesChanged() => RefreshSpawns();

    public bool PartyHas(int item) => _state.ItemCount(item) > 0 ||
        Field.Units.Any(u => !u.Hidden && !(u.Dead && u.Cleared) && u.Side != Side.Enemy && (u.Weapon == item || u.Armor == item || u.Accessory == item));

    public void PartyAdd(int unit) => Party.Add(_state, _db, Field, unit);
    public void PartyRemove(int unit) => Party.Remove(_state, Field, unit);
    public void ChangeClass(int unit, int cls) => Party.ChangeClass(_state, _db, Field, unit, cls);
    public void ChangeEquipment(int unit, int item) => Party.ChangeEquipment(_state, _db, Field, unit, item);

    /// <summary>Centres the view on a tile at once (developer jumps, loading).</summary>
    public void ScrollTo(int x, int y)
    {
        Camera = new Point(x * Tile + Tile / 2 - BreezyGame.ViewWidth / 2, y * Tile + Tile / 2 - BreezyGame.ViewHeight / 2);
        ClampCamera();
    }

    /// <summary>The camera a map starts with (FUN_0041dc34): the tile 9 left and 6 above the first player unit, kept on the map.</summary>
    void StartCamera(int x, int y)
    {
        Camera = new Point(Math.Clamp(x - 9, 0, Math.Max(0, Map.Width - ViewTilesX)) * Tile,
            Math.Clamp(y - 6, 0, Math.Max(0, Map.Height - ViewTilesY)) * Tile);
        _panTarget = null;
    }

    Point? _panTarget;
    /// <summary>The original's camera moves a tile per 30 Hz tick: 16 px a frame here.</summary>
    const int PanSpeed = 16;

    /// <summary>
    /// FUN_00416fb0: scrolls just enough to bring the tile into the view's middle box (columns 6–12, rows 5–8 of the
    /// 19×14 tiles), a tile per tick; used by events' scroll command, the enemy phase and after the opening.
    /// </summary>
    public void ShowTile(int x, int y) => ShowTile(x, y, null);

    /// <summary>
    /// FUN_004170a0, the camera for a computer unit's turn: the unit into the middle box as above, then, where what it's
    /// after (<paramref name="focus"/>) would still be outside the 19×14 view, on just far enough to bring it in at the edge.
    /// </summary>
    public void ShowTile(int x, int y, (int X, int Y)? focus)
    {
        int cx = Camera.X / Tile, cy = Camera.Y / Tile;
        var (tx, ty) = ViewFor((cx, cy), (x, y), focus, Map.Width, Map.Height);
        var target = new Point(tx == cx ? Camera.X : tx * Tile, ty == cy ? Camera.Y : ty * Tile);
        _panTarget = target == Camera ? null : target;
    }

    /// <summary>
    /// <see cref="ShowTile(int, int, ValueTuple{int, int}?)"/>'s arithmetic: the view's top-left tile, from
    /// <paramref name="view"/>, that brings <paramref name="tile"/> into the middle box and then the focus into the view,
    /// on a map of <paramref name="width"/> × <paramref name="height"/> tiles.
    /// </summary>
    public static (int X, int Y) ViewFor((int X, int Y) view, (int X, int Y) tile, (int X, int Y)? focus, int width, int height)
    {
        int maxX = Math.Max(0, width - ViewTilesX), maxY = Math.Max(0, height - ViewTilesY);
        var (cx, cy) = view;
        var (x, y) = tile;
        int tx = Math.Clamp(x - cx < 6 ? x - 6 : x - cx > 12 ? x - 12 : cx, 0, maxX);
        int ty = Math.Clamp(y - cy < 5 ? y - 5 : y - cy > 8 ? y - 8 : cy, 0, maxY);
        if (focus is { } f)
        {
            tx = Math.Clamp(f.X < tx ? f.X : f.X - tx > ViewTilesX - 1 ? f.X - (ViewTilesX - 1) : tx, 0, maxX);
            ty = Math.Clamp(f.Y < ty ? f.Y : f.Y - ty > ViewTilesY - 1 ? f.Y - (ViewTilesY - 1) : ty, 0, maxY);
        }
        return (tx, ty);
    }

    /// <summary>The enemy phase's "show this unit" (the same box rule).</summary>
    public void PanTo(int x, int y) => ShowTile(x, y);

    public bool Panning => _panTarget is not null;

    /// <summary>A pan under way ends at once where it was going.</summary>
    public void FinishPan()
    {
        if (_panTarget is { } t) Camera = t;
        _panTarget = null;
    }

    Point? _scrollTarget;

    /// <summary>
    /// FUN_00416f38 from FUN_0040dee0: while the left button is held with the pointer on the frame, the view scrolls a tile
    /// a tick that way (16 px a frame here, as the pans); a tile begun is finished once let go. Called once a frame with
    /// the held direction, (0, 0) when nothing is held.
    /// </summary>
    public void EdgeScroll(int dx, int dy)
    {
        if ((dx != 0 || dy != 0) && _scrollTarget is null)
        {
            int maxX = Math.Max(0, PixelWidth - BreezyGame.ViewWidth), maxY = Math.Max(0, PixelHeight - BreezyGame.ViewHeight);
            var t = new Point(dx == 0 ? Camera.X : Math.Clamp((Camera.X / Tile + dx) * Tile, 0, maxX),
                dy == 0 ? Camera.Y : Math.Clamp((Camera.Y / Tile + dy) * Tile, 0, maxY));
            if (t != Camera) _scrollTarget = t;
        }
        if (_scrollTarget is not { } target) return;
        Camera.X += Math.Clamp(target.X - Camera.X, -PanSpeed, PanSpeed);
        Camera.Y += Math.Clamp(target.Y - Camera.Y, -PanSpeed, PanSpeed);
        if (Camera == target) _scrollTarget = null;
    }

    void UpdatePan()
    {
        if (_panTarget is not { } t) return;
        Camera.X += Math.Clamp(t.X - Camera.X, -PanSpeed, PanSpeed);
        Camera.Y += Math.Clamp(t.Y - Camera.Y, -PanSpeed, PanSpeed);
        if (Camera == t) _panTarget = null;
    }

    public void ClampCamera()
    {
        Camera.X = Math.Clamp(Camera.X, 0, Math.Max(0, PixelWidth - BreezyGame.ViewWidth));
        Camera.Y = Math.Clamp(Camera.Y, 0, Math.Max(0, PixelHeight - BreezyGame.ViewHeight));
    }

    /// <summary>FUN_004249a4: every unit of that id on the map gets the steps (at most 16).</summary>
    public void QueueMove(int unit, IReadOnlyList<byte> steps)
    {
        foreach (var u in Field.Alive.Where(u => u.Unit == unit))
        {
            var v = Visuals[u];
            v.Pending.Clear();
            foreach (var s in steps.Take(16)) v.Pending.Enqueue(s);
        }
    }

    /// <summary>
    /// FUN_00424a68: the camera first goes to the units about to move (the last one wins), then every unit takes its
    /// steps together, one per 8 ticks; a step onto an occupied or impassable tile is skipped but still takes its time.
    /// </summary>
    public void ExecuteMoves()
    {
        BattleUnit? last = null;
        foreach (var (u, v) in Visuals)
        {
            if (v.Pending.Count == 0) continue;
            last = u;
            v.Scripted = true;
            while (v.Pending.Count > 0) v.Running.Enqueue(v.Pending.Dequeue());
        }
        if (last is not null) ShowTile(last.X, last.Y);
    }

    public bool MovesRunning => Visuals.Values.Any(v => v.Running.Count > 0 || v.StepFrames > 0);

    public void FinishMoves()
    {
        FinishPan();   // the camera goes where the moves sent it (ExecuteMoves), as when they play
        bool any = true;
        while (any)
        {
            any = false;
            foreach (var (u, v) in Visuals)
            {
                if (v.Running.Count == 0) continue;
                any = true;
                Step(u, v, v.Running.Dequeue());
            }
        }
        foreach (var v in Visuals.Values) { v.StepFrames = 0; v.Offset = Vector2.Zero; v.Scripted = false; }
    }

    /// <summary>Animates a unit along a path of tiles (battle movement).</summary>
    public void Walk(BattleUnit u, IReadOnlyList<(int X, int Y)> path)
    {
        int x = u.X, y = u.Y;
        foreach (var p in path)
        {
            byte dir = (p.X - x, p.Y - y) switch { (0, -1) => 0, (1, 0) => 1, (0, 1) => 2, _ => 3 };
            Visuals[u].Running.Enqueue(dir);
            (x, y) = p;
        }
    }

    static readonly int[] FacingRow = { 1, 2, 3, 4 };          // step dir up/right/down/left (+0xd0 8/6/2/4) -> sheet row
    static readonly Point[] StepDelta = { new(0, -1), new(1, 0), new(0, 1), new(-1, 0) };

    /// <summary>One step: turn, then move unless an event's step is blocked (out of the map, a living unit there, or no way through).</summary>
    bool Step(BattleUnit u, UnitVisual v, int s)
    {
        int dir = s % 4;
        v.Facing = FacingRow[dir];
        if (s >= 4) return false;
        int nx = u.X + StepDelta[dir].X, ny = u.Y + StepDelta[dir].Y;
        if (v.Scripted && (!Field.InBounds(nx, ny) || Field.UnitAt(nx, ny) is not null || Field.MoveCost(u, nx, ny) <= 0)) return false;
        (u.X, u.Y) = (nx, ny);
        return true;
    }

    public void Update()
    {
        UpdatePan();
        foreach (var (u, v) in Visuals)
        {
            if (v.StepFrames > 0)
            {
                v.StepFrames--;
                v.Offset = v.StepFrom * v.StepFrames / Math.Max(1, v.StepLength);
                if (v.StepFrames == 0 && v.Running.Count == 0) v.Scripted = false;
                continue;
            }
            if (v.Running.Count == 0) { v.Scripted = false; continue; }
            int s = v.Running.Dequeue();
            bool moved = Step(u, v, s);
            // a tile per 8 ticks (16 frames) as the original walks; faster with the movement speed option, but not an event's moves
            v.StepFrames = v.StepLength = v.Scripted ? FramesPerTile : Math.Max(1, FramesPerTile / Math.Max(1, WalkSpeed));
            v.Offset = v.StepFrom = moved ? new Vector2(-StepDelta[s % 4].X, -StepDelta[s % 4].Y) * Tile : Vector2.Zero;
        }
    }

    // ---------------- drawing ----------------

    static Rectangle ChipRect(int chip)
    {
        var (cx, cy) = Chipset.ChipCell(chip);
        return new(cx * Tile, cy * Tile, Tile, Tile);
    }

    Texture2D? UnitSheet(BattleUnit u)
    {
        int g = _db.Classes[Math.Clamp(u.ClassId, 0, _db.Classes.Length - 1)].UnitGraphic;
        if (!_unitSheets.TryGetValue(g, out var t))
            _unitSheets[g] = t = _tex.Get($"UNIT_{g:000}.BMP");
        return t;
    }

    public Vector2 ScreenPos(int x, int y, Rectangle viewport) =>
        new(viewport.X + x * Tile - Camera.X, viewport.Y + y * Tile - Camera.Y);

    public void DrawTiles(SpriteBatch sb, Rectangle viewport)
    {
        if (_chips is null) return;
        int x0 = Math.Max(0, Camera.X / Tile), y0 = Math.Max(0, Camera.Y / Tile);
        int x1 = Math.Min(Map.Width - 1, (Camera.X + viewport.Width) / Tile);
        int y1 = Math.Min(Map.Height - 1, (Camera.Y + viewport.Height) / Tile);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                sb.Draw(_chips, ScreenPos(x, y, viewport), ChipRect(Map.Tiles[y, x]), Color.White);
        foreach (var o in Others)
            if (o.Graphic >= 0 && OtherVisible(o)) sb.Draw(_chips, ScreenPos(o.X, o.Y, viewport), ChipRect(o.Graphic), Color.White);
    }

    /// <summary>
    /// 全体マップ (original screenshot): a 352×352 green panel in the middle of the screen, the whole map shrunk
    /// (8 px a tile, smaller if it wouldn't fit) with a white grid, units as coloured squares.
    /// </summary>
    /// <summary>
    /// 全体マップ (FUN_00415460, FUN_0041561c): a green 352×352 panel at (144,64), the map's own chips (not the "Other"
    /// objects') at 8 px a tile centred on (320,240), a white grid a pixel up-left of each tile, and every unit as one of
    /// SYSTEM.BMP's 8×8 dots — side 0 enemy red, 1 ally blue, 2 neutral yellow — in four shades changing every 4 ticks.
    /// </summary>
    public void DrawWholeMap(SpriteBatch sb, Ui.Ui ui, IEnumerable<(int X, int Y, int Side)> units, int frame)
    {
        ui.Panel(sb, new Rectangle(144, 64, 352, 352));
        if (_chips is null) return;
        const int s = 8;
        int w = Map.Width * s, h = Map.Height * s;
        int ox = 320 - w / 2, oy = 240 - h / 2;
        for (int y = 0; y < Map.Height; y++)
            for (int x = 0; x < Map.Width; x++)
                sb.Draw(_chips, new Rectangle(ox + x * s, oy + y * s, s, s), ChipRect(Map.Tiles[y, x]), Color.White);
        for (int y = 0; y <= Map.Height; y++) ui.Fill(sb, new Rectangle(ox - 1, oy + y * s - 1, w + 1, 1), Ui.Ui.PanelText);
        for (int x = 0; x <= Map.Width; x++) ui.Fill(sb, new Rectangle(ox + x * s - 1, oy - 1, 1, h + 1), Ui.Ui.PanelText);
        int t = frame / 8 % 4;
        if (_tex.Get("SYSTEM.BMP") is { } sys)
            foreach (var (x, y, side) in units)
                sb.Draw(sys, new Vector2(ox + x * s, oy + y * s), new Rectangle(240 + t % 2 * 8, 128 + side * 16 + t / 2 * 8, 8, 8), Color.White);
    }

    /// <summary>
    /// A range overlay (FUN_004132a8) from SYSTEM.BMP's 64 × 64 range blocks at y 64 (copied into a sheet by FUN_00411d74):
    /// 0 cyan, 1 salmon, 2 orange, 3 blue. Each block covers 2 × 2 map tiles: a tile shows the 32 × 32 window at its
    /// quarter of the block (odd columns and rows the right and lower half) plus the phase (tick / 2) × 2 px down and
    /// right, wrapping at 64, so the pattern drifts up-left. A plain checker (えるまに) repeats every 32 px and looks the same
    /// on every tile; a block with a picture in it (Kenjyutsu's 移動 / 射程 / 攻撃 / 同盟) reads across the tiles. In stripes
    /// only the tile's odd pixel rows are drawn (FUN_00406c38), the map showing between them, and "solid" uses every row
    /// (FUN_00406ad8); both leave out palette index 0.
    /// </summary>
    public void DrawRange(SpriteBatch sb, Rectangle viewport, IEnumerable<(int X, int Y)> tiles, int colour, int frame, bool solid = false)
    {
        var sys = _tex.Get("SYSTEM.BMP");
        if (sys is null) return;
        const int Block = 2 * Tile;
        int phase = frame / 4 * 2 % Block;
        foreach (var (x, y) in tiles)
        {
            var p = ScreenPos(x, y, viewport);
            int sx = (phase + (x & 1) * Tile) % Block, sy = (phase + (y & 1) * Tile) % Block;
            int left = Math.Min(Tile, Block - sx);   // the window runs off the block's right edge and wraps to its left
            for (int row = solid ? 0 : 1; row < Tile; row += solid ? 1 : 2)
            {
                int srcY = 64 + (sy + row) % Block;
                sb.Draw(sys, p + new Vector2(0, row), new Rectangle(colour * Block + sx, srcY, left, 1), Color.White);
                if (left < Tile)
                    sb.Draw(sys, p + new Vector2(left, row), new Rectangle(colour * Block, srcY, Tile - left, 1), Color.White);
            }
        }
    }

    readonly Dictionary<int, (Color Dark, Color Light)> _rangeShades = new();

    /// <summary>
    /// The two colours a range block is mostly made of, the darker first (えるまに's cyan checker: (128,176,176) and
    /// (176,216,248); Kenjyutsu's green 移動: (72,200,48) and (120,232,80)), for overlays drawn in the game's own shades.
    /// </summary>
    public (Color Dark, Color Light) RangeShades(int colour)
    {
        if (_rangeShades.TryGetValue(colour, out var shades)) return shades;
        var counts = new Dictionary<byte, int>();
        var bmp = _tex.Bitmap("SYSTEM.BMP");
        if (bmp is not null && bmp.Width >= (colour + 1) * 2 * Tile && bmp.Height >= 64 + 2 * Tile)
            for (int y = 64; y < 64 + 2 * Tile; y++)
                for (int x = colour * 2 * Tile; x < (colour + 1) * 2 * Tile; x++)
                    if (bmp.Pixels[y * bmp.Width + x] is var i and not 0)
                        counts[i] = counts.GetValueOrDefault(i) + 1;
        var top = counts.OrderByDescending(c => c.Value).Take(2)
            .Select(c => new Color((byte)(bmp!.Palette[c.Key] >> 16), (byte)(bmp.Palette[c.Key] >> 8), (byte)bmp.Palette[c.Key]))
            .OrderBy(c => c.R * 299 + c.G * 587 + c.B * 114).ToList();
        shades = top.Count switch
        {
            0 => (new Color(128, 176, 176), new Color(176, 216, 248)),   // no block: the RTP's cyan
            1 => (top[0], top[0]),
            _ => (top[0], top[1]),
        };
        return _rangeShades[colour] = shades;
    }

    /// <summary>A sheet's frame cycle (DAT_0043328c, DAT_004332a4): the column of each of 6 phases.</summary>
    static readonly int[] FrameCycle = { 0, 0, 1, 2, 2, 1 };

    /// <summary>
    /// The units (FUN_00413918): those whose tile is in the view (19 × 14 tiles; a unit just outside, or off the map,
    /// isn't drawn, so nothing of it overhangs into the view), the standing ones by their row on the screen, then the
    /// ones stepping (a sort key of y + 1000), each a 64 × 64 cell of its sheet centred on its tile. One stepping
    /// (walking, or an event's turn) shows its way's row and a new phase of the cycle every 2 ticks; <paramref name="facing"/>, the chosen unit, its way's row
    /// every 3 ticks; any other row 0 every 4 — or, once it has acted, frame 0 with SYSTEM.BMP's END on its tile — and a
    /// poisoned or (instead) sleeping one SYSTEM.BMP's PSN / SLP, a new frame every 4 ticks.
    /// </summary>
    /// <summary>
    /// A sprite cut to the map's view (modern: the original draws units over the screen frame, so a unit on the view's
    /// edge, or the camera moved, let the 64-px sprite spill onto the frame).
    /// </summary>
    static void Clipped(SpriteBatch sb, Texture2D tex, Vector2 at, Rectangle src, Rectangle view)
    {
        var dest = new Rectangle((int)MathF.Floor(at.X), (int)MathF.Floor(at.Y), src.Width, src.Height);
        var cut = Rectangle.Intersect(dest, view);
        if (cut.Width <= 0 || cut.Height <= 0) return;
        sb.Draw(tex, cut, new Rectangle(src.X + cut.X - dest.X, src.Y + cut.Y - dest.Y, cut.Width, cut.Height), Color.White);
    }

    /// <summary>The Map Unit HP option: the HP gauge (and number) along the bottom of each unit's tile.</summary>
    public static Config.MapHp HpShown { get; set; }

    static Texture2D? _pixel;

    /// <summary>A rectangle of one colour, cut to the view.</summary>
    static void Fill(SpriteBatch sb, Rectangle r, Color c, Rectangle viewport)
    {
        if (_pixel is null) { _pixel = new Texture2D(sb.GraphicsDevice, 1, 1); _pixel.SetData(new[] { Color.White }); }
        var cut = Rectangle.Intersect(r, viewport);
        if (cut.Width > 0 && cut.Height > 0) sb.Draw(_pixel, cut, c);
    }

    /// <summary>Digits 3 × 5 dots, a row a byte of three bits (4 the left column).</summary>
    static readonly byte[][] Digits =
    {
        new byte[] { 7, 5, 5, 5, 7 }, new byte[] { 2, 6, 2, 2, 7 }, new byte[] { 7, 1, 7, 4, 7 }, new byte[] { 7, 1, 7, 1, 7 },
        new byte[] { 5, 5, 7, 1, 1 }, new byte[] { 7, 4, 7, 1, 7 }, new byte[] { 7, 4, 7, 5, 7 }, new byte[] { 7, 1, 2, 2, 2 },
        new byte[] { 7, 5, 7, 5, 7 }, new byte[] { 7, 5, 7, 1, 7 },
    };

    /// <summary>Full HP blue, hurt green, a quarter or less red.</summary>
    static readonly Color HpFull = new(56, 176, 255), HpHurt = new(72, 216, 88), HpLow = new(240, 64, 56);

    /// <summary>
    /// The HP along the bottom of the 32 × 32 tile, nothing behind it, so the unit stays in sight: a 2-px gauge, and with
    /// the number, a 3-px gauge with the HP in 3 × 5 dots at its right, a 1-px shadow under the dots to read on any ground.
    /// </summary>
    static void DrawHp(SpriteBatch sb, BattleUnit u, Vector2 pos, Rectangle viewport)
    {
        int max = Math.Max(1, u.MaxHp), hp = Math.Clamp(u.Hp, 0, max);
        var c = hp >= max ? HpFull : hp * 4 <= max ? HpLow : HpHurt;
        int x = (int)MathF.Floor(pos.X), bottom = (int)MathF.Floor(pos.Y) + Tile - 1;
        if (HpShown == Config.MapHp.Gauge)
        {
            Fill(sb, new Rectangle(x + 4, bottom - 2, Math.Max(1, 24 * hp / max), 2), c, viewport);
            return;
        }
        string n = hp.ToString();
        int numX = x + 31 - 1 - (n.Length * 4 - 1), gx = x + 2, gw = x + 30 - 11 - 2 - gx;   // room for three digits, so every gauge is as long
        Fill(sb, new Rectangle(gx, bottom - 4, Math.Max(1, gw * hp / max), 3), c, viewport);
        foreach (var (dx, dy, ink) in new[] { (1, 1, Color.Black * 0.8f), (0, 0, Color.White) })
            for (int k = 0; k < n.Length; k++)
            {
                var rows = Digits[n[k] - '0'];
                for (int r = 0; r < 5; r++)
                    for (int b = 0; b < 3; b++)
                        if ((rows[r] >> (2 - b) & 1) != 0) Fill(sb, new Rectangle(numX + k * 4 + b + dx, bottom - 6 + r + dy, 1, 1), ink, viewport);
            }
    }

    public void DrawUnits(SpriteBatch sb, Rectangle viewport, int frame, BattleUnit? facing = null)
    {
        var sys = _tex.Get("SYSTEM.BMP");
        int tick = frame / 2;
        static bool Stepping(UnitVisual v) => v.StepFrames > 0 || v.Running.Count > 0;   // +0xd4 holds through a whole move
        foreach (var u in Field.Alive.Concat(Ghosts).OrderBy(u => (Stepping(Visuals[u]) ? 1000 : 0) + u.Y * Tile + Visuals[u].Offset.Y))
        {
            var v = Visuals[u];
            var tile = ScreenPos(u.X, u.Y, viewport);
            if (tile.X <= viewport.X - Tile || tile.X >= viewport.Right || tile.Y <= viewport.Y - Tile || tile.Y >= viewport.Bottom) continue;
            var pos = tile + v.Offset;
            var sheet = UnitSheet(u);
            if (sheet is null) continue;
            bool stepping = Stepping(v), chosen = !stepping && u == facing && v.Facing > 0;
            int row = stepping || chosen ? v.Facing : 0;
            int col = stepping ? FrameCycle[tick / 2 % 6] : chosen ? FrameCycle[tick / 3 % 6] : u.Acted ? 0 : FrameCycle[tick / 4 % 6];
            Clipped(sb, sheet, pos - new Vector2(16, 16), new Rectangle(col * 64, row * 64, 64, 64), viewport);
            if (HpShown != Config.MapHp.Off && !u.Dead) DrawHp(sb, u, pos, viewport);
            if (stepping || chosen || sys is null) continue;
            if (u.Acted) Clipped(sb, sys, pos + new Vector2(6, 19), new Rectangle(70, 19, 22, 10), viewport);   // END
            if (u.SleepTurns > 0 || u.PoisonTurns > 0)
                Clipped(sb, sys, pos, new Rectangle(tick / 4 % 4 * 32 + (u.SleepTurns > 0 ? 128 : 0), 192, 32, 32), viewport);
        }
    }
}
