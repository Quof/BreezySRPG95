using System.Text.Json;
using System.Text.Json.Serialization;
using BreezySRPG95.Config;
using BreezySRPG95.Ui;
using Microsoft.Xna.Framework.Input;

namespace Srpg95.Data.Tests;

/// <summary>The gamepad: its bindings as settings.json keeps them, their defaults, and how a controller is named and drawn.</summary>
public class GamepadTests
{
    static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public void Gamepad_bindings_are_kept_by_name_and_old_settings_get_the_defaults()
    {
        var s = new Settings();
        s.Pad[GameAction.Confirm] = new() { Buttons.B, Buttons.RightTrigger };
        string json = JsonSerializer.Serialize(s, Json);
        Assert.Contains("\"RightTrigger\"", json);
        var back = JsonSerializer.Deserialize<Settings>(json, Json)!;
        Assert.Equal(new[] { Buttons.B, Buttons.RightTrigger }, back.Pad[GameAction.Confirm]);
        Assert.Equal(new[] { Buttons.DPadUp, Buttons.LeftThumbstickUp }, back.Pad[GameAction.Up]);
        // a settings.json from before the gamepad: the defaults
        Assert.Equal(Settings.DefaultPad()[GameAction.Cancel], JsonSerializer.Deserialize<Settings>("{\"Language\":\"English\"}", Json)!.Pad[GameAction.Cancel]);
    }

    [Fact]
    public void The_defaults_follow_SRPG_Studio_and_bind_every_button_once_at_most()
    {
        var pad = Settings.DefaultPad();
        Assert.Equal((Buttons.A, Buttons.B, Buttons.X), (pad[GameAction.Confirm][0], pad[GameAction.Cancel][0], pad[GameAction.Info][0]));
        Assert.Equal(Buttons.Start, pad[GameAction.SkipCutscene][0]);
        Assert.Equal(Buttons.RightThumbstickUp, pad[GameAction.CameraUp][0]);
        var all = pad.Values.SelectMany(b => b).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.All(all, b => Assert.Contains(b, Controls.PadButtons));
        Assert.All(Enum.GetValues<GameAction>(), a => Assert.True(pad.ContainsKey(a)));
    }

    [Fact]
    public void A_controller_is_named_without_MonoGames_wrapping_and_drawn_by_its_maker()
    {
        Assert.Equal("8BitDo Ultimate 2C Wireless (WUKONG)", Controls.CleanName("Controller (8BitDo Ultimate 2C Wireless (WUKONG))"));
        Assert.Equal("PS5 Controller", Controls.CleanName("PS5 Controller"));
        Assert.True(PadGlyphs.PlayStation("PS4 Controller"));
        Assert.True(PadGlyphs.PlayStation("DualSense Wireless Controller"));
        Assert.False(PadGlyphs.PlayStation("Xbox Series X Controller"));
        Assert.False(PadGlyphs.PlayStation(null));
        // a stick's direction takes two pictures, a button one
        Assert.Equal(2 * 9 + 9 + 4, PadGlyphs.Measure(new[] { Buttons.LeftThumbstickUp, Buttons.A }, false, 1, 4));
    }
}
