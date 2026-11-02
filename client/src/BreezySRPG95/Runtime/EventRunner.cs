using Srpg95.Data;

namespace BreezySRPG95.Runtime;

public enum EventEnd { None, EndEvent, MapWon, MapLost, GameOver, ReturnToTitle }

/// <summary>Per-frame input the interpreter cares about.</summary>
public readonly record struct EventInput(bool Confirm, bool Cancel, bool Up, bool Down, bool FastForward);

/// <summary>What the message window should show right now.</summary>
/// <param name="Small">in the compact text (a long translated message, Ui.FitMessage)</param>
public sealed record MessageView(int Window, IReadOnlyList<string> Lines, int Face, bool FaceRight, bool FaceFlip, bool Small = false);

/// <summary>
/// Runs one event script tree (FORMAT_NOTES §11.3 for the command semantics).
/// Blocking commands: message, character choice, wait, screen fades, shake,
/// execute movement and button input. Everything else runs instantly.
/// </summary>
public sealed class EventRunner
{
    readonly GameState _state;
    readonly IMapContext _map;
    readonly Audio _audio;
    readonly ScreenEffects _fx;
    /// <summary>The random numbers of variable operations; a scripted run seeds it, so its events draw the same each time.</summary>
    public Random Rng { get; set; } = new();
    readonly Stack<(List<ScriptNode> Nodes, int Pc)> _stack = new();

    int _face;
    double _waitMs, _titleMs;
    bool _faceRight, _faceFlip, _waitButton, _waitFx, _waitMoves, _waitPan;

    public string Name { get; private set; } = "";
    public MessageView? Message { get; private set; }
    /// <summary>The message window still up: under a character choice that came straight after its message (as in the original).</summary>
    public MessageView? LastMessage { get; private set; }
    public string[]? Choice { get; private set; }
    public int ChoiceIndex { get; private set; }
    public EventEnd End { get; private set; }
    public bool Running => _stack.Count > 0 || _titleMs > 0;
    /// <summary>Waiting on a "button input" command.</summary>
    public bool WaitingForButton => _waitButton;
    /// <summary>What the interpreter is waiting on (screenshot mode writes it next to the PNG).</summary>
    public string DebugState => $"msg={Message is not null} choice={Choice is not null} wait={_waitMs:0} button={_waitButton} fx={_waitFx}/{_fx.Busy} " +
        $"moves={_waitMoves} pan={_waitPan} title={_titleMs:0} stack={_stack.Count} end={End}";

    /// <summary>Mouse hover over a choice line.</summary>
    public void HoverChoice(int index)
    {
        if (Choice is not null && index >= 0 && index < Choice.Length) ChoiceIndex = index;
    }
    public List<string> Log { get; } = new();
    /// <summary>Holding fast-forward auto-advances messages (option).</summary>
    public bool FastForwardAdvancesText { get; set; } = true;
    /// <summary>Every message shown, for the backlog.</summary>
    public Action<MessageView>? Shown { get; set; }

    ChoiceNode? _choiceNode;
    int _messageAge;
    EventScript? _script;

    /// <summary>A message's translation (modern, Srpg95.Data.Translation), or null for the game's words.</summary>
    public Func<EventScript, MessageNode, IReadOnlyList<string>?>? TranslateMessage { get; set; }
    /// <summary>A character choice's options translated, or null.</summary>
    public Func<EventScript, ChoiceNode, string[]?>? TranslateChoice { get; set; }
    /// <summary>A translated message laid out in windows (Ui.FitMessage): the first shows, the rest follow a click apart.</summary>
    public Func<IReadOnlyList<string>, List<(IReadOnlyList<string> Lines, bool Small)>>? Fit { get; set; }
    /// <summary>The windows of the message shown still to come, and whether a character choice comes after the last.</summary>
    readonly Queue<MessageView> _pages = new();
    bool _choiceAfterPages;

    public EventRunner(GameState state, IMapContext map, Audio audio, ScreenEffects fx)
        => (_state, _map, _audio, _fx) = (state, map, audio, fx);

    public void Start(EventScript script)
    {
        _stack.Clear();
        _script = script;
        _pages.Clear();
        _choiceAfterPages = false;
        Name = script.Name;
        End = EventEnd.None;
        Message = null;
        Choice = null;
        _face = 0;   // every event starts without a face (FUN_0040cfe0 / FUN_0040d0a4 clear +0x5fbc)
        if (script.Error is not null)
        {
            Trace($"{script.Name}: malformed script not run ({script.Error})");
            return;
        }
        _stack.Push((script.Tree, 0));
    }

    void Trace(string s)
    {
        Log.Add(s);
        if (Log.Count > 200) Log.RemoveAt(0);
    }

    bool _skipping;

    /// <summary>
    /// Cutscene skip (modern option): runs the rest of the event at once. Every command still takes effect (switches,
    /// items, units, music, pictures, scripted moves land where they end) and messages go to the backlog; only
    /// character choices stop the skip, since the player has to answer them.
    /// </summary>
    public void Skip()
    {
        if (!Running || Choice is not null) return;
        _skipping = true;
        Update(new EventInput(false, false, false, false, false));
    }

    void SkipBlocks()
    {
        Message = LastMessage = null;
        _pages.Clear();
        _choiceAfterPages = false;
        _waitMs = 0;
        _waitButton = false;
        if (_waitPan) { _map.FinishPan(); _waitPan = false; }   // the view where the scroll was taking it
        if (_waitFx) { _fx.Complete(); _waitFx = false; }
        if (_waitMoves) { _map.FinishMoves(); _waitMoves = false; }
        if (_titleMs > 0) { _fx.Complete(); _titleMs = Math.Min(_titleMs, 1); }
    }

    // ---------------- saving in the middle of an event (modern, SceneSave) ----------------

    /// <summary>The event can be saved where it stands: running, not ending, not on its way back to the title.</summary>
    public bool CanSave => _stack.Count > 0 && End == EventEnd.None && _titleMs <= 0;

    /// <summary>The effects, moves and pans the script waits on have been jumped to their ends: it waits on them no more.</summary>
    public void Settle() => _waitFx = _waitMoves = _waitPan = false;

    /// <summary>Where the event stands (after <see cref="Settle"/>), or null when it can't be saved now.</summary>
    public RunnerSave? Save()
    {
        if (!CanSave) return null;
        var frames = _stack.Reverse().ToArray();   // the script's top level first
        var saved = new List<int[]>();
        for (int k = 0; k < frames.Length; k++)
        {
            int block = 0;
            if (k > 0)
            {
                // a block is the body of the command before the next one in the block below
                var (below, next) = frames[k - 1];
                if (next < 1 || next > below.Count) return null;
                var nodes = frames[k].Nodes;
                block = below[next - 1] switch
                {
                    BranchNode b when ReferenceEquals(b.Then, nodes) => -1,
                    ChoiceNode c when c.Cases.FindIndex(x => ReferenceEquals(x, nodes)) is var i and >= 0 => i,
                    ChoiceNode c when nodes.Count == 0 => c.Cases.Count,   // an option past the cases: nothing to run
                    _ => int.MinValue,
                };
                if (block == int.MinValue) return null;
            }
            saved.Add(new[] { block, frames[k].Pc });
        }
        return new RunnerSave(Name, saved, _face, _faceRight, _faceFlip, Message, LastMessage, Choice is not null, ChoiceIndex, Math.Max(0, _waitMs), _waitButton,
            _pages.Count > 0 ? _pages.ToList() : null, _choiceAfterPages);
    }

    /// <summary>Puts the event back where <paramref name="s"/> left it; false (and nothing running) when the script doesn't have that place.</summary>
    public bool Restore(EventScript script, RunnerSave s)
    {
        Start(script);
        if (_stack.Count == 0 || s.Frames.Count == 0) return Fail();
        _stack.Clear();
        List<ScriptNode>? nodes = null;
        int pc = 0;
        foreach (var f in s.Frames)
        {
            if (f.Length != 2) return Fail();
            if (nodes is not null)
            {
                if (pc < 1 || pc > nodes.Count) return Fail();
                nodes = (nodes[pc - 1], f[0]) switch
                {
                    (BranchNode b, -1) => b.Then,
                    (ChoiceNode c, var i) when i >= 0 && i < c.Cases.Count => c.Cases[i],
                    (ChoiceNode c, var i) when i == c.Cases.Count => new List<ScriptNode>(),
                    _ => null,
                };
                if (nodes is null) return Fail();
            }
            else nodes = script.Tree;
            pc = f[1];
            if (pc < 0 || pc > nodes.Count) return Fail();
            _stack.Push((nodes, pc));
        }
        (_face, _faceRight, _faceFlip) = (s.Face, s.FaceRight, s.FaceFlip);
        Message = s.Message;
        LastMessage = s.LastMessage;
        if (s.Choice)
        {
            // the choice is the command before the next one in the top block
            var (top, next) = _stack.Peek();
            if (next < 1 || top[next - 1] is not ChoiceNode c) return Fail();
            _choiceNode = c;
            Choice = Options(c);
            ChoiceIndex = Math.Clamp(s.ChoiceIndex, 0, Math.Max(0, c.Options.Length - 1));
        }
        (_waitMs, _waitButton) = (Math.Max(0, s.WaitMs), s.WaitButton);
        foreach (var p in s.Pages ?? new()) _pages.Enqueue(p);
        _choiceAfterPages = s.ChoiceAfterPages && _pages.Count > 0;
        _waitFx = _waitMoves = _waitPan = _skipping = false;
        _titleMs = 0;
        _messageAge = 0;
        if (Message is not null) Shown?.Invoke(Message);
        Trace($"{Name}: resumed from a save");
        return true;

        bool Fail()
        {
            _stack.Clear();
            Message = LastMessage = null;
            Choice = null;
            return false;
        }
    }

    public void Update(EventInput input)
    {
        if (!Running) { _skipping = false; return; }
        if (_skipping) SkipBlocks();

        // --- blocked states ---
        if (Message is not null)
        {
            _messageAge++;
            bool auto = input.FastForward && FastForwardAdvancesText && _messageAge > 4;
            if (input.Confirm) Sfx.MessageAdvance();
            // a message clicked away takes its window with it (FUN_0040d18c clears +0x5e18); only one a choice follows
            // straight away stays up under it
            if (!input.Confirm && !auto) return;
            Message = LastMessage = null;
            if (_pages.TryDequeue(out var page))
            {
                // the message's next window; its last stays up under a character choice that follows
                Message = LastMessage = page;
                Shown?.Invoke(page);
                _messageAge = 0;
                if (_pages.Count > 0 || !_choiceAfterPages) return;
                _choiceAfterPages = false;
                Message = null;
            }
        }
        if (Choice is not null)
        {
            if (input.Up) ChoiceIndex = (ChoiceIndex + Choice.Length - 1) % Choice.Length;
            if (input.Down) ChoiceIndex = (ChoiceIndex + 1) % Choice.Length;
            if (!input.Confirm) return;
            Sfx.Click();
            var body = ChoiceIndex < _choiceNode!.Cases.Count ? _choiceNode.Cases[ChoiceIndex] : new List<ScriptNode>();
            Trace($"choice -> {Choice[ChoiceIndex]}");
            Choice = null;
            LastMessage = null;
            _stack.Push((body, 0));
        }
        if (_waitMs > 0) { _waitMs -= GameClock.FrameMs; if (_waitMs > 0) return; }
        if (_waitButton) { if (!input.Confirm) return; _waitButton = false; }
        if (_waitFx) { if (_fx.Busy) return; _waitFx = false; }
        if (_waitMoves) { if (_map.MovesRunning || _map.Panning) return; _waitMoves = false; }
        if (_waitPan) { if (_map.Panning) return; _waitPan = false; }
        if (_titleMs > 0)
        {
            if (_fx.Busy) return;   // the erase first
            _titleMs -= GameClock.FrameMs;
            if (_titleMs <= 500) _audio.PlayBgm("");
            if (_titleMs > 0) return;
            Finish(EventEnd.ReturnToTitle);
            return;
        }

        // --- run until something blocks ---
        while (_stack.Count > 0)
        {
            var (nodes, pc) = _stack.Pop();
            if (pc >= nodes.Count) continue;
            _stack.Push((nodes, pc + 1));
            if (!Execute(nodes[pc])) continue;
            // a message right before a character choice stays up under it: the choice comes at once (FUN_0040d18c)
            if (nodes[pc] is MessageNode && pc + 1 < nodes.Count && nodes[pc + 1] is ChoiceNode)
            {
                if (_pages.Count == 0) { Message = null; continue; }
                _choiceAfterPages = true;   // a message over several windows: the choice after the last
            }
            if (_titleMs > 0) return;
            if (_skipping && Choice is null) { SkipBlocks(); continue; }
            _skipping = false;
            return;
        }
        _skipping = false;
        if (End == EventEnd.None) Ended();
    }

    /// <summary>The end of a script (end_of_script / end_event): a picture still shown is taken away at once.</summary>
    void Ended()
    {
        End = EventEnd.EndEvent;
        _fx.ErasePicture(0);
    }

    /// <summary>Executes one node; returns true if the interpreter must wait.</summary>
    bool Execute(ScriptNode node)
    {
        switch (node)
        {
            case MessageNode m:
                _pages.Clear();
                if (_script is not null && TranslateMessage?.Invoke(_script, m) is { } translated)
                {
                    var windows = Fit?.Invoke(translated) ?? new() { (translated, false) };
                    foreach (var (lines, small) in windows.Skip(1)) _pages.Enqueue(new MessageView(m.Window, lines, _face, _faceRight, _faceFlip, small));
                    Message = LastMessage = new MessageView(m.Window, windows[0].Lines, _face, _faceRight, _faceFlip, windows[0].Small);
                }
                else Message = LastMessage = new MessageView(m.Window, m.Lines, _face, _faceRight, _faceFlip);
                Shown?.Invoke(Message);
                _messageAge = 0;
                return true;
            case BranchNode b:
                bool ok = Evaluate(b.Condition);
                Trace($"if {Describe(b.Condition)} -> {ok}");
                if (ok) _stack.Push((b.Then, 0));
                return false;
            case ChoiceNode c:
                _choiceNode = c;
                Choice = Options(c);
                ChoiceIndex = 0;
                return true;
            case CommandNode { Command: var cmd }:
                return Run(cmd);
        }
        return false;
    }

    string[] Options(ChoiceNode c) => _script is not null && TranslateChoice?.Invoke(_script, c) is { } t ? t : c.Options;

    /// <summary>The commands <see cref="Run"/> carries out (messages, branches and choices are tree nodes).</summary>
    public static readonly HashSet<Op> Handled = new()
    {
        Op.FaceGraphic, Op.SwitchOperation, Op.ItemManagement, Op.GoldManagement, Op.ScreenDisplay, Op.EraseScreen,
        Op.PictureDisplay, Op.ErasePicture, Op.ScrollScreen, Op.FlashTintScreen, Op.ShakeScreen, Op.ChangeAllyUnit,
        Op.UnitClassChange, Op.UnitEquipmentChange, Op.MoveUnit, Op.ExecuteMovement, Op.Bgm, Op.SoundEffect,
        Op.VariableOperation, Op.ButtonInput, Op.Wait, Op.SelectMap, Op.EndEvent, Op.MapComplete, Op.GameOver, Op.ReturnToTitle,
    };

    bool Run(Command c)
    {
        switch (c.Op)
        {
            case Op.FaceGraphic:
                _faceRight = c.Payload[0] == 1;
                _faceFlip = c.Payload[1] == 1;
                _face = c.U16(2);
                return false;
            case Op.SwitchOperation:
                if (c.U16(2) < GameState.SwitchCount) _state.Switches[c.U16(2)] = c.U16(0) == 1;
                Trace($"switch {c.U16(2)} = {(c.U16(0) == 1 ? "ON" : "OFF")}");
                _map.SwitchesChanged();   // units appear and leave at once, mid-cutscene (FUN_00409658)
                return false;
            case Op.ItemManagement:
                _state.AddItem(c.U16(2), c.U16(0) == 1 ? c.U16(4) : -c.U16(4));
                return false;
            case Op.GoldManagement:
                _state.Gold = Math.Clamp(_state.Gold + (c.U16(0) == 1 ? c.I32(2) : -c.I32(2)), 0, 9_999_999);   // FUN_0040aef0 / FUN_0040af20
                return false;
            case Op.ScreenDisplay:
                _fx.Display(c.Payload[0], c.Payload[1]);   // FUN_00422ecc
                _waitFx = _fx.Busy;
                return _waitFx;
            case Op.EraseScreen:
                _fx.Erase(c.Payload[0], c.Payload[1]);     // FUN_004231c0
                _waitFx = _fx.Busy;
                return _waitFx;
            case Op.PictureDisplay:
                // a wipe blocks the script while it runs, as the exe's loop does
                _fx.ShowPicture(c.Str(6), c.Payload[1], c.Payload[0], c.Payload[2] == 1);
                _waitFx = _fx.Busy;
                return _waitFx;
            case Op.ErasePicture:
                _fx.ErasePicture(c.U16(0));
                _waitFx = _fx.Busy;
                return _waitFx;
            case Op.ScrollScreen:
                // FUN_00423870: scrolls a tile per tick until the tile is in the view's middle box, and waits
                _map.ShowTile(c.U16(0), c.U16(2));
                _waitPan = true;
                return true;
            case Op.FlashTintScreen:
                // flashes and gradual tone changes hold the script until they're over (FUN_00423914)
                _fx.FlashTint(c.U16(0), c.I16(2), c.I16(4), c.I16(6), c.I16(8));
                _waitFx = _fx.Busy;
                return _waitFx;
            case Op.ShakeScreen:
                if (_skipping) return false;
                _fx.Shake(c.U16(0));
                _waitFx = true;
                return true;
            case Op.ChangeAllyUnit:
                if (c.U16(0) == 1) _map.PartyAdd(c.U16(2)); else _map.PartyRemove(c.U16(2));
                return false;
            case Op.UnitClassChange:
                _map.ChangeClass(c.U16(0), c.U16(2));
                return false;
            case Op.UnitEquipmentChange:
                _map.ChangeEquipment(c.U16(0), c.U16(2));
                return false;
            case Op.MoveUnit:
                _map.QueueMove(c.U16(0), c.Payload.Skip(4).Take(c.U16(2)).ToArray());
                return false;
            case Op.ExecuteMovement:
                _map.ExecuteMoves();
                _waitMoves = true;
                return true;
            case Op.Bgm:
                _audio.PlayBgm(c.Str(0));
                return false;
            case Op.SoundEffect:
                if (!_skipping) _audio.PlaySe(c.Str(0));
                return false;
            case Op.VariableOperation:
                RunVariable(c);
                return false;
            case Op.ButtonInput:
                _waitButton = true;
                return true;
            case Op.Wait:
                _waitMs = c.U16(0) * 100;   // tenths of a second, on the clock (FUN_004254f4)
                return _waitMs > 0;
            case Op.SelectMap:
                _state.NextMap = c.U16(0);   // save header 0x010
                Trace($"next map = {_state.NextMap}");
                return false;
            case Op.EndEvent: Finish(EventEnd.EndEvent); return true;
            case Op.MapComplete: Finish(c.U16(0) == 1 ? EventEnd.MapWon : EventEnd.MapLost); return true;
            case Op.GameOver: Finish(EventEnd.GameOver); return true;
            case Op.ReturnToTitle:
                // FUN_0040d18c: the screen is erased with the fade (effect 6); 1 s after that the music stops, and the
                // title comes 0.5 s later
                _stack.Clear();
                _fx.Erase(0, 6);
                _titleMs = 1500;
                return true;
            default:
                Trace($"unhandled {c.Op}");
                return false;
        }
    }

    void Finish(EventEnd how)
    {
        End = how;
        _stack.Clear();
        _titleMs = 0;
        if (how == EventEnd.EndEvent) _fx.ErasePicture(0);
        Trace($"end: {how}");
    }

    void RunVariable(Command c)
    {
        int v = c.U16(0);
        if (v >= GameState.VariableCount) return;
        int operand = c.U16(2) switch
        {
            1 => c.U16(6) < GameState.VariableCount ? _state.Variables[c.U16(6)] : 0,
            2 => Rng.Next(0, c.U16(8) + 1),     // rand() % (max + 1)
            _ => c.I16(4),
        };
        ref int x = ref _state.Variables[v];
        switch (c.U16(10))
        {
            case 0: x = operand; break;
            case 1: x += operand; break;
            case 2: x -= operand; break;
            case 3: x *= operand; break;
            case 4: x = operand == 0 ? 0 : x / operand; break;   // dividing by 0 gives 0
        }
        x = Math.Clamp(x, -1000, 1000);   // FUN_0040d18c keeps variables within ±1000
    }

    bool Evaluate(Command c)
    {
        var p = c.Payload;
        switch (c.U16(0))
        {
            case 0: return _state.Switches[Math.Min((int)c.U16(2), GameState.SwitchCount - 1)] == (p[12] == 0);
            case 1:
                // FUN_00424ee0: After = at or after, Before = at or before, Exact = equal (phase counter)
                int t = c.U16(4);
                return p[13] switch { 0 => _map.Phase >= t, 1 => _map.Phase <= t, _ => _map.Phase == t };
            case 2: return _map.PartyHas(c.U16(6)) == (p[14] == 0);
            case 3: return _map.UnitState(c.U16(8), p[15]);
            case 4:
                int val = _state.Variables[Math.Min((int)c.U16(10), GameState.VariableCount - 1)], cmp = c.I16(16);
                return c.U16(18) switch { 0 => val >= cmp, 1 => val <= cmp, _ => val == cmp };
        }
        return false;
    }

    static string Describe(Command c) => c.U16(0) switch
    {
        0 => $"switch {c.U16(2)} {(c.Payload[12] == 0 ? "ON" : "OFF")}",
        1 => $"turn {c.U16(4)} mode {c.Payload[13]}",
        2 => $"item {c.U16(6)} {(c.Payload[14] == 0 ? "owned" : "not owned")}",
        3 => $"unit {c.U16(8)} state {c.Payload[15]}",
        4 => $"var {c.U16(10)} {(c.U16(18) switch { 0 => ">=", 1 => "<=", _ => "==" })} {c.I16(16)}",
        _ => "?",
    };
}
