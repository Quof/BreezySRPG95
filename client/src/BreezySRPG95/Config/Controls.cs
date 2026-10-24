using Microsoft.Xna.Framework.Input;

namespace BreezySRPG95.Config;

/// <summary>
/// The keyboard's and a gamepad's state per bound action: down, pressed this frame, and auto-repeat for held directions.
/// The gamepad is the first one connected (SDL's game controllers: Xbox, PlayStation and most others), its buttons, D-pad,
/// triggers and the sticks' four directions bindable as buttons (<see cref="Settings.Pad"/>).
/// </summary>
public sealed class Controls(Settings settings)
{
    KeyboardState _cur, _prev;
    GamePadState _pad, _padPrev;
    readonly Dictionary<GameAction, int> _held = new();
    const int RepeatDelay = 18, RepeatEvery = 4;

    /// <summary>What can be bound on a gamepad, in the order a list of them is shown.</summary>
    public static readonly Buttons[] PadButtons =
    {
        Buttons.A, Buttons.B, Buttons.X, Buttons.Y, Buttons.LeftShoulder, Buttons.RightShoulder, Buttons.LeftTrigger,
        Buttons.RightTrigger, Buttons.Back, Buttons.Start, Buttons.LeftStick, Buttons.RightStick,
        Buttons.DPadUp, Buttons.DPadDown, Buttons.DPadLeft, Buttons.DPadRight,
        Buttons.LeftThumbstickUp, Buttons.LeftThumbstickDown, Buttons.LeftThumbstickLeft, Buttons.LeftThumbstickRight,
        Buttons.RightThumbstickUp, Buttons.RightThumbstickDown, Buttons.RightThumbstickLeft, Buttons.RightThumbstickRight,
    };

    /// <summary>The gamepad in use (the first connected), or −1.</summary>
    public static int PadIndex()
    {
        for (int i = 0; i < GamePad.MaximumGamePadCount; i++)
            if (GamePad.GetCapabilities(i).IsConnected) return i;
        return -1;
    }

    /// <summary>The connected gamepad's name (SDL's), or null.</summary>
    public static string? PadName() => PadIndex() is var i and >= 0 ? CleanName(GamePad.GetCapabilities(i).DisplayName) : null;

    /// <summary>A controller's name without the "Controller (…)" MonoGame wraps some in.</summary>
    public static string CleanName(string name) =>
        name.StartsWith("Controller (", StringComparison.Ordinal) && name.EndsWith(')') ? name["Controller (".Length..^1] : name;

    /// <summary>The keyboard's state and the gamepad's (default: none) this frame.</summary>
    public void Update(KeyboardState k, GamePadState pad = default)
    {
        (_prev, _cur) = (_cur, k);
        (_padPrev, _pad) = (_pad, pad);
        foreach (var a in Enum.GetValues<GameAction>())
            _held[a] = Down(a) ? _held.GetValueOrDefault(a) + 1 : 0;
    }

    IEnumerable<Keys> KeysOf(GameAction a) => settings.Keys.TryGetValue(a, out var k) ? k : Enumerable.Empty<Keys>();
    IEnumerable<Buttons> ButtonsOf(GameAction a) => settings.Pad.TryGetValue(a, out var b) ? b : Enumerable.Empty<Buttons>();

    public bool Down(GameAction a) => KeysOf(a).Any(_cur.IsKeyDown) || ButtonsOf(a).Any(_pad.IsButtonDown);
    public bool Pressed(GameAction a) => KeysOf(a).Any(k => _cur.IsKeyDown(k) && !_prev.IsKeyDown(k))
        || ButtonsOf(a).Any(b => _pad.IsButtonDown(b) && !_padPrev.IsButtonDown(b));

    /// <summary>Pressed, or held long enough to auto-repeat (cursor and menu movement).</summary>
    public bool Repeat(GameAction a)
    {
        int h = _held.GetValueOrDefault(a);
        return Pressed(a) || (h > RepeatDelay && (h - RepeatDelay) % RepeatEvery == 0);
    }

    /// <summary>A key that went down this frame (for rebinding).</summary>
    public Keys? NewKey() => _cur.GetPressedKeys().Cast<Keys?>().FirstOrDefault(k => !_prev.IsKeyDown(k!.Value));

    /// <summary>A gamepad button (or stick direction) that went down this frame (for rebinding).</summary>
    public Buttons? NewButton() => PadButtons.Cast<Buttons?>().FirstOrDefault(b => _pad.IsButtonDown(b!.Value) && !_padPrev.IsButtonDown(b.Value));

    public static string Name(Keys k) => k switch
    {
        Keys.Enter => "Enter", Keys.Escape => "Esc", Keys.Back => "Backspace", Keys.Space => "Space",
        Keys.LeftShift => "L-Shift", Keys.RightShift => "R-Shift", Keys.LeftControl => "L-Ctrl", Keys.RightControl => "R-Ctrl",
        Keys.LeftAlt => "L-Alt", Keys.RightAlt => "R-Alt", Keys.Up => "↑", Keys.Down => "↓", Keys.Left => "←", Keys.Right => "→",
        >= Keys.D0 and <= Keys.D9 => ((int)k - (int)Keys.D0).ToString(),
        _ => k.ToString(),
    };
}
