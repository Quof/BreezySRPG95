using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework.Input;
using Srpg95.Data;

namespace BreezySRPG95.Config;

/// <summary>Things a key can be bound to (rebindable in the options screen).</summary>
public enum GameAction
{
    Confirm, Cancel, Info, Up, Down, Left, Right,
    CameraUp, CameraDown, CameraLeft, CameraRight,
    FastForward, Backlog, Options, Fullscreen, SkipCutscene, EnemyRanges,
}

public enum Scaling { Integer, Fit, Stretch }
/// <summary>The interface's look: the original's, the modern dark one, or the gilded one (gold-framed dark boxes).</summary>
public enum UiSkin { Original, Gilded, Modern }   // the options list them in this order (settings.json keeps the names)
/// <summary>
/// The restyled skins' font (the original skin keeps the original's bitmap fonts): a smooth face, or one of two pixel
/// fonts drawn dot for dot — DotGothic16 with PixelMplus12 for the small text, or the KH Dot font's 小伝馬町 16 and 12.
/// </summary>
public enum UiFont { NotoSans, NotoSerif, BizUd, YuGothic, MsGothic, PixelDot, KhDot }
/// <summary>The language of BreezySRPG95's own menus and messages (the game's text is the game's).</summary>
public enum UiLanguage { English, Japanese }
/// <summary>The save and load screens: the extended one (100 files with pictures) or the original's list of 8.</summary>
public enum SaveScreen { Extended, Original }
public enum MusicEngine { Windows, SoundFont }
public enum FastForwardMode { Hold, Toggle }
/// <summary>決定音, CLICK.WAV: everywhere, everywhere but advancing a message, or nowhere (saving and loading play it anyway).</summary>
public enum ConfirmSound { On, GameplayOnly, Off }
/// <summary>戦闘演出: every fight's battle scene, only those of a player-side unit's action (allies, guests and neutral units), or none (the result popups alone).</summary>
public enum BattleSceneMode { On, PlayerUnits, Simple }
/// <summary>The unit status window's weakness orbs: none, coloured orbs, or coloured orbs with their attribute's letter.</summary>
public enum WeaknessOrbs { Off, On, Colorblind }

/// <summary>
/// Player settings (modern features; none of this exists in the original), kept in %APPDATA%\BreezySRPG95\settings.json
/// (or a settings.json beside the executable).
/// </summary>
public sealed class Settings
{
    // display
    /// <summary>The window's size; 0 × 0 from the older <see cref="WindowScale"/>.</summary>
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    /// <summary>The window as a multiple of 640 × 480, as earlier versions kept it; read when <see cref="WindowWidth"/> is 0.</summary>
    public int WindowScale { get; set; } = 2;
    public bool Fullscreen { get; set; }
    public Scaling Scaling { get; set; } = Scaling.Integer;
    public bool Smooth { get; set; }
    public UiFont Font { get; set; } = UiFont.NotoSerif;
    public UiLanguage Language { get; set; } = UiLanguage.Japanese;   // BreezySRPG95's own menus and messages
    public SaveScreen SaveScreen { get; set; } = SaveScreen.Extended;

    /// <summary>The window's size, whichever way it was kept.</summary>
    [JsonIgnore] public (int Width, int Height) WindowSize =>
        WindowWidth >= 320 && WindowHeight >= 240 ? (WindowWidth, WindowHeight) : (640 * Math.Clamp(WindowScale, 1, 6), 480 * Math.Clamp(WindowScale, 1, 6));

    // audio
    public int MusicVolume { get; set; } = 80;            // 0–100
    public int SoundVolume { get; set; } = 80;
    public MusicEngine Music { get; set; } = MusicEngine.SoundFont;
    public string? SoundFont { get; set; }                // null = the built-in GeneralUser GS; else a file name in soundfonts/ or a full path
    public bool LoopMarkers { get; set; }                 // MIDI tracks repeat from their RPG Maker loop marker (SoundFont only)
    public ConfirmSound ConfirmSound { get; set; } = ConfirmSound.GameplayOnly;   // the CLICK.WAV "thunk" on confirms (at first not when a message is clicked away)
    /// <summary>The on/off confirm sound earlier versions saved (off = <see cref="ConfirmSound.Off"/>); read, never written.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? MessageSound { get; set; }

    // gameplay
    public BattleSceneMode BattleSceneMode { get; set; }
    /// <summary>The on/off battle scenes earlier versions saved (off = <see cref="BattleSceneMode.Simple"/>); read, never written.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? BattleScenes { get; set; }
    public bool CombatBgm { get; set; } = true;           // the map's battle BGM during battle scenes (off: the phase's music, as a fight leaves it)
    public bool ActionMenuFromTop { get; set; }           // after a move the pointer goes to the action menu's first entry (off, at first: 行動終了, as the original)
    public FastForwardMode FastForwardMode { get; set; } = FastForwardMode.Hold;
    public int FastForwardSpeed { get; set; } = 2;
    public bool FastForwardSkipsScenes { get; set; }
    public bool FastForwardAdvancesText { get; set; } = true;
    public bool FastForwardMouse { get; set; } = true;    // holding the left mouse button or the confirm key fast-forwards (after 0.4 s; 1 s in a cutscene)
    public bool MouseInput { get; set; } = true;          // off: the mouse does nothing (keyboard only)
    public bool EdgeScrollHover { get; set; } = true;     // the pointer on the screen frame scrolls the map, no button held
    public WeaknessOrbs WeaknessOrbs { get; set; }       // the class's attribute weaknesses as orbs on the status window's portrait
    public int UnitSpeed { get; set; } = 2;               // units walk on the battle map at this many times the original's pace
    public bool AutoEndTurn { get; set; }                 // the turn ends by itself once every unit of the player's has acted
    public bool SkipEnemyTurn { get; set; }               // resolve the enemy phase instantly (no walking, scenes or popups)
    public UiSkin Skin { get; set; }                      // the windows, menus and text box: the original look or a restyled one
    /// <summary>The on/off setting earlier versions saved (on = <see cref="UiSkin.Modern"/>); read, never written.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool ModernUi { get; set; }

    public Dictionary<GameAction, List<Keys>> Keys { get; set; } = DefaultKeys();
    /// <summary>The gamepad's bindings: its buttons, D-pad, triggers and the sticks' directions (<see cref="Controls.PadButtons"/>).</summary>
    public Dictionary<GameAction, List<Buttons>> Pad { get; set; } = DefaultPad();

    /// <summary>Keyboard layout in the spirit of SRPG Studio: arrows move the map cursor and menus, WASD scroll the
    /// camera (and move in menus), Enter/Z confirm, Esc/X/Backspace cancel, Space skips a cutscene.</summary>
    public static Dictionary<GameAction, List<Keys>> DefaultKeys() => new()
    {
        [GameAction.Confirm] = new() { Microsoft.Xna.Framework.Input.Keys.Enter, Microsoft.Xna.Framework.Input.Keys.Z },
        [GameAction.Cancel] = new() { Microsoft.Xna.Framework.Input.Keys.Escape, Microsoft.Xna.Framework.Input.Keys.X, Microsoft.Xna.Framework.Input.Keys.Back },
        [GameAction.Info] = new() { Microsoft.Xna.Framework.Input.Keys.C, Microsoft.Xna.Framework.Input.Keys.LeftShift },
        [GameAction.Up] = new() { Microsoft.Xna.Framework.Input.Keys.Up },
        [GameAction.Down] = new() { Microsoft.Xna.Framework.Input.Keys.Down },
        [GameAction.Left] = new() { Microsoft.Xna.Framework.Input.Keys.Left },
        [GameAction.Right] = new() { Microsoft.Xna.Framework.Input.Keys.Right },
        [GameAction.CameraUp] = new() { Microsoft.Xna.Framework.Input.Keys.W },
        [GameAction.CameraDown] = new() { Microsoft.Xna.Framework.Input.Keys.S },
        [GameAction.CameraLeft] = new() { Microsoft.Xna.Framework.Input.Keys.A },
        [GameAction.CameraRight] = new() { Microsoft.Xna.Framework.Input.Keys.D },
        [GameAction.FastForward] = new() { Microsoft.Xna.Framework.Input.Keys.Tab, Microsoft.Xna.Framework.Input.Keys.LeftControl },
        [GameAction.Backlog] = new() { Microsoft.Xna.Framework.Input.Keys.L },
        [GameAction.Options] = new() { Microsoft.Xna.Framework.Input.Keys.F1 },
        [GameAction.Fullscreen] = new() { Microsoft.Xna.Framework.Input.Keys.F4 },
        [GameAction.SkipCutscene] = new() { Microsoft.Xna.Framework.Input.Keys.Space },
        [GameAction.EnemyRanges] = new() { Microsoft.Xna.Framework.Input.Keys.R },
    };

    /// <summary>
    /// The gamepad as SRPG Studio has it, read as an Xbox pad's buttons in their usual order (its game.ini numbers them:
    /// SELECT 5, CANCEL 6, OPTION 7, OPTION2 8, L_SWITCH 9, R_SWITCH 10, SYSTEM 11, SKIP 12 — the D-pad's four first):
    /// A confirms, B cancels, X shows the unit (OPTION, its C key), Y the options (OPTION2), View fast-forwards (SYSTEM),
    /// Menu skips a cutscene (SKIP); the bumpers, its unit switch, are the backlog and the enemy ranges here. The D-pad and
    /// the left stick move the cursor, the right stick the camera.
    /// </summary>
    public static Dictionary<GameAction, List<Buttons>> DefaultPad() => new()
    {
        [GameAction.Confirm] = new() { Buttons.A },
        [GameAction.Cancel] = new() { Buttons.B },
        [GameAction.Info] = new() { Buttons.X },
        [GameAction.Up] = new() { Buttons.DPadUp, Buttons.LeftThumbstickUp },
        [GameAction.Down] = new() { Buttons.DPadDown, Buttons.LeftThumbstickDown },
        [GameAction.Left] = new() { Buttons.DPadLeft, Buttons.LeftThumbstickLeft },
        [GameAction.Right] = new() { Buttons.DPadRight, Buttons.LeftThumbstickRight },
        [GameAction.CameraUp] = new() { Buttons.RightThumbstickUp },
        [GameAction.CameraDown] = new() { Buttons.RightThumbstickDown },
        [GameAction.CameraLeft] = new() { Buttons.RightThumbstickLeft },
        [GameAction.CameraRight] = new() { Buttons.RightThumbstickRight },
        [GameAction.FastForward] = new() { Buttons.Back },
        [GameAction.Backlog] = new() { Buttons.LeftShoulder },
        [GameAction.Options] = new() { Buttons.Y },
        [GameAction.Fullscreen] = new(),
        [GameAction.SkipCutscene] = new() { Buttons.Start },
        [GameAction.EnemyRanges] = new() { Buttons.RightShoulder },
    };

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    [JsonIgnore] public string? Path { get; private set; }

    public static Settings Load(bool persistent)
    {
        if (!persistent)   // scripted runs: BREEZY_SKIN=original|modern|gilded (or the older BREEZY_MODERN=1), BREEZY_UIFONT, BREEZY_LANGUAGE, BREEZY_SAVESCREEN, BREEZY_COMBATBGM=off, BREEZY_AUTOENDTURN=on, BREEZY_WEAKNESS=on|colorblind, BREEZY_BATTLESCENES=on|playerunits|simple, BREEZY_FFSKIPSCENES=on, BREEZY_UNITSPEED=1|2|4, BREEZY_SKIPENEMY=on
            return new Settings
            {
                Skin = Enum.TryParse<UiSkin>(Environment.GetEnvironmentVariable("BREEZY_SKIN"), true, out var skin) ? skin
                    : Environment.GetEnvironmentVariable("BREEZY_MODERN") == "1" ? UiSkin.Modern : UiSkin.Original,
                Font = Enum.TryParse<UiFont>(Environment.GetEnvironmentVariable("BREEZY_UIFONT"), true, out var font) ? font : UiFont.NotoSerif,
                Language = Enum.TryParse<UiLanguage>(Environment.GetEnvironmentVariable("BREEZY_LANGUAGE"), true, out var lang) ? lang : UiLanguage.English,
                SaveScreen = Enum.TryParse<SaveScreen>(Environment.GetEnvironmentVariable("BREEZY_SAVESCREEN"), true, out var saves) ? saves : SaveScreen.Extended,
                CombatBgm = !string.Equals(Environment.GetEnvironmentVariable("BREEZY_COMBATBGM"), "off", StringComparison.OrdinalIgnoreCase),
                AutoEndTurn = string.Equals(Environment.GetEnvironmentVariable("BREEZY_AUTOENDTURN"), "on", StringComparison.OrdinalIgnoreCase),
                WeaknessOrbs = Enum.TryParse<WeaknessOrbs>(Environment.GetEnvironmentVariable("BREEZY_WEAKNESS"), true, out var orbs) ? orbs : WeaknessOrbs.Off,
                BattleSceneMode = Enum.TryParse<BattleSceneMode>(Environment.GetEnvironmentVariable("BREEZY_BATTLESCENES"), true, out var scenes) ? scenes : BattleSceneMode.On,
                FastForwardSkipsScenes = string.Equals(Environment.GetEnvironmentVariable("BREEZY_FFSKIPSCENES"), "on", StringComparison.OrdinalIgnoreCase),
                UnitSpeed = int.TryParse(Environment.GetEnvironmentVariable("BREEZY_UNITSPEED"), out var walk) ? walk : 2,
                SkipEnemyTurn = string.Equals(Environment.GetEnvironmentVariable("BREEZY_SKIPENEMY"), "on", StringComparison.OrdinalIgnoreCase),
                // as before these became the defaults, so that the runs' scripted keys still land where they did
                ActionMenuFromTop = true,
                ConfirmSound = ConfirmSound.On,
            };
        foreach (var path in Candidates())
        {
            if (!File.Exists(path)) continue;
            try
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new Settings();
                // Space moved from Confirm to the cutscene skip when that was added
                if (!s.Keys.ContainsKey(GameAction.SkipCutscene) && s.Keys.TryGetValue(GameAction.Confirm, out var ok))
                    ok.Remove(Microsoft.Xna.Framework.Input.Keys.Space);
                foreach (var (a, keys) in DefaultKeys()) s.Keys.TryAdd(a, keys);   // actions added later
                foreach (var (a, buttons) in DefaultPad()) s.Pad.TryAdd(a, buttons);
                if (s.ModernUi) { s.Skin = UiSkin.Modern; s.ModernUi = false; }   // the old on/off look setting
                if (s.MessageSound == false) s.ConfirmSound = ConfirmSound.Off;      // the old on/off confirm sound
                s.MessageSound = null;
                if (s.BattleScenes == false) s.BattleSceneMode = BattleSceneMode.Simple;   // the old on/off battle scenes
                s.BattleScenes = null;
                s.Path = OldSettings.Contains(path) ? UserSettings : path;   // saved to the new place from now on
                return s;
            }
            catch (Exception) { /* unreadable: fall back to defaults */ }
        }
        return new Settings { Path = UserSettings };
    }

    /// <summary>Where settings live: the user's AppData, shared by every game the client is dropped into.</summary>
    static string UserSettings => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BreezySRPG95", "settings.json");

    /// <summary>
    /// Read in this order: a settings.json next to the executable (a portable setup that keeps its own), the user's,
    /// then the ones from the client's earlier names (EasySRPG95, and Erumani before it).
    /// </summary>
    static IEnumerable<string> Candidates()
    {
        yield return System.IO.Path.Combine(AppContext.BaseDirectory, "settings.json");
        yield return UserSettings;
        foreach (var old in OldSettings) yield return old;
    }

    static string[] OldSettings => new[] { "EasySRPG95", "Erumani" }
        .Select(name => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), name, "settings.json")).ToArray();

    public void Save()
    {
        if (Path is null) return;
        // never back into an earlier name's folder
        foreach (var path in new[] { Path }.Concat(Candidates()).Distinct().Where(p => !OldSettings.Contains(p)))
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
                Path = path;
                return;
            }
            catch (Exception) { /* not writable: try the next place */ }
        }
    }

    /// <summary>
    /// SoundFonts the player can choose: *.sf2 in soundfonts/ next to the executable and in the game folder (the folder's
    /// name and the files' extension in any case, off Windows too).
    /// </summary>
    public static List<string> FindSoundFonts(string gameRoot)
    {
        var dirs = new[] { FileNames.In(AppContext.BaseDirectory, "soundfonts"), FileNames.In(gameRoot, "soundfonts"), gameRoot };
        return dirs.Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "*.sf2", FileNames.AnyCase)).Distinct().ToList();
    }

    /// <summary>Stands for the SoundFont embedded in the executable (GeneralUser GS).</summary>
    public const string BuiltIn = "GeneralUser GS (built-in)";

    /// <summary>The chosen SoundFont: a file path, or <see cref="BuiltIn"/> (the default, and the fallback when a chosen file is gone).</summary>
    public string ResolveSoundFont(string gameRoot)
    {
        if (SoundFont is null || SoundFont == BuiltIn) return BuiltIn;
        if (File.Exists(SoundFont)) return SoundFont;
        return FindSoundFonts(gameRoot).FirstOrDefault(f => System.IO.Path.GetFileName(f).Equals(SoundFont, StringComparison.OrdinalIgnoreCase)) ?? BuiltIn;
    }
}
