using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace BreezySRPG95.Ui;

/// <summary>
/// Gamepad buttons as small pictures, 9 × 8 pixels each (pad/PadGlyphs.png, from DT3's sKeyPressPad): an Xbox pad's (A B X
/// Y, LB RB LT RT, View Menu, LS RS) or a PlayStation pad's (✕ ○ □ △, L1 R1 L2 R2, Share Options, L3 R3) as the connected
/// controller's name says, the D-pad's four directions, and a stick's direction as its L or R with the D-pad's arrow after.
/// </summary>
public sealed class PadGlyphs(GraphicsDevice device)
{
    public const int Width = 9, Height = 8;
    const int Unknown = 30, DPadUp = 24, LStick = 28, RStick = 29;

    Texture2D? _sheet;

    Texture2D Sheet()
    {
        if (_sheet is null)
        {
            using var s = typeof(PadGlyphs).Assembly.GetManifestResourceStream("pad/PadGlyphs.png")!;
            _sheet = Texture2D.FromStream(device, s);
        }
        return _sheet;
    }

    /// <summary>Whether the controller's name is a PlayStation pad's (SDL names them PS3 / PS4 / PS5 Controller, DualSense, …).</summary>
    public static bool PlayStation(string? name) => name is not null &&
        new[] { "PS3", "PS4", "PS5", "PlayStation", "DualShock", "DualSense", "Sony" }.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>A button's pictures (frames of the sheet): one, or a stick's letter and the direction's arrow.</summary>
    static int[] Frames(Buttons b, bool ps) => b switch
    {
        Buttons.A => new[] { ps ? 12 : 0 }, Buttons.B => new[] { ps ? 13 : 1 }, Buttons.X => new[] { ps ? 14 : 2 }, Buttons.Y => new[] { ps ? 15 : 3 },
        Buttons.LeftShoulder => new[] { ps ? 16 : 4 }, Buttons.RightShoulder => new[] { ps ? 17 : 5 },
        Buttons.LeftTrigger => new[] { ps ? 18 : 6 }, Buttons.RightTrigger => new[] { ps ? 19 : 7 },
        Buttons.Back => new[] { ps ? 20 : 8 }, Buttons.Start => new[] { ps ? 21 : 9 },
        Buttons.LeftStick => new[] { ps ? 22 : 10 }, Buttons.RightStick => new[] { ps ? 23 : 11 },
        Buttons.DPadUp => new[] { DPadUp }, Buttons.DPadDown => new[] { DPadUp + 1 }, Buttons.DPadLeft => new[] { DPadUp + 2 }, Buttons.DPadRight => new[] { DPadUp + 3 },
        Buttons.LeftThumbstickUp => new[] { LStick, DPadUp }, Buttons.LeftThumbstickDown => new[] { LStick, DPadUp + 1 },
        Buttons.LeftThumbstickLeft => new[] { LStick, DPadUp + 2 }, Buttons.LeftThumbstickRight => new[] { LStick, DPadUp + 3 },
        Buttons.RightThumbstickUp => new[] { RStick, DPadUp }, Buttons.RightThumbstickDown => new[] { RStick, DPadUp + 1 },
        Buttons.RightThumbstickLeft => new[] { RStick, DPadUp + 2 }, Buttons.RightThumbstickRight => new[] { RStick, DPadUp + 3 },
        _ => new[] { Unknown },
    };

    /// <summary>The width <see cref="Draw"/> takes for some buttons at a scale, a gap between buttons.</summary>
    public static int Measure(IReadOnlyList<Buttons> buttons, bool ps, int scale, int gap) =>
        buttons.Sum(b => Frames(b, ps).Length * Width * scale) + Math.Max(0, buttons.Count - 1) * gap;

    /// <summary>Draws buttons left to right from (x, y), each picture <paramref name="scale"/> times its pixels.</summary>
    public void Draw(SpriteBatch sb, IReadOnlyList<Buttons> buttons, bool ps, int x, int y, int scale, int gap, Color tint)
    {
        var sheet = Sheet();
        foreach (var b in buttons)
        {
            foreach (int f in Frames(b, ps))
            {
                sb.Draw(sheet, new Rectangle(x, y, Width * scale, Height * scale), new Rectangle(f * Width, 0, Width, Height), tint);
                x += Width * scale;
            }
            x += gap;
        }
    }
}
