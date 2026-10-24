using BreezySRPG95.Config;
using BreezySRPG95.Runtime;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using static BreezySRPG95.Ui.Lang;

namespace BreezySRPG95.Ui;

/// <summary>
/// The options menu (modern; from the title screen, F1 anywhere, or a right click during an event). It opens on a list
/// of categories (Display, Audio, Gameplay, Controls); each opens its own page. A toggle changes on a click or left /
/// right; a setting with several values steps through them with left / right and opens a drop-down list of them on a
/// click or Confirm; the mouse wheel turns a volume under the pointer up or down; on the Controls page Confirm on an
/// action waits for a key (pressing a key already bound to it removes it; right click cancels). Every word is in the
/// Language setting's language.
/// </summary>
public sealed class OptionsScreen
{
    /// <summary>
    /// A row: its label and value. <see cref="Change"/> steps a toggle or a choice (−1 / +1); a choice also lists its values
    /// (<see cref="Choices"/>, the current one <see cref="Chosen"/>) for the drop-down, which sets one with <see cref="Pick"/>;
    /// <see cref="Activate"/> opens a page or runs a command. <see cref="Wheel"/>: the wheel steps it too (a volume).
    /// <see cref="Level"/> / <see cref="SetLevel"/>: a 0–100 bar in the value column that a click sets and a drag slides.
    /// <see cref="Pad"/>: gamepad buttons shown as pictures in the value column.
    /// </summary>
    sealed record Row(string Label, Func<string> Value, Action<int>? Change = null, Action? Activate = null,
        Func<string[]>? Choices = null, Func<int>? Chosen = null, Action<int>? Pick = null, bool Wheel = false,
        Func<int>? Level = null, Action<int>? SetLevel = null, Func<IReadOnlyList<Buttons>>? Pad = null);

    static Row Choice(string label, Func<string[]> names, Func<int> chosen, Action<int> pick) =>
        new(label, () => names().ElementAtOrDefault(chosen()) ?? "",
            d => { int n = names().Length; pick(((Math.Max(0, chosen()) + d) % n + n) % n); },
            Choices: names, Chosen: chosen, Pick: pick);

    static Row Toggle(string label, Func<bool> get, Action<bool> set) => new(label, () => OnOff(get()), _ => set(!get()));

    enum Page { Main, Display, Audio, Gameplay, Controls, Keyboard, Gamepad }

    /// <summary>The page Back leads to, and the row it opens on there.</summary>
    static (Page Page, int Row) Parent(Page p) => p switch
    {
        Page.Keyboard => (Page.Controls, 0),
        Page.Gamepad => (Page.Controls, 1),
        _ => (Page.Main, 0),
    };

    readonly Settings _s;
    readonly Ui _ui;
    readonly string _gameRoot;
    readonly Action _apply;
    readonly Camp.MenuList _list = new() { Rect = new Rectangle(96, 40, 448, 400), ColumnX = new[] { 428 }, SkipDisabled = true };
    List<Row> _rows = new();
    Page _page;
    int _mainIndex;
    GameAction? _waiting, _waitingPad;
    /// <summary>The open drop-down list and the row it belongs to.</summary>
    Camp.MenuList? _drop;
    int _dropRow;

    public bool Done { get; private set; }

    /// <summary>In a game (not on the title): タイトルへ戻る and ゲーム終了 on the main page, each asking first.</summary>
    readonly Action? _toTitle, _quit;
    readonly string[] _yesNo;
    /// <summary>The はい / いいえ question up, and what はい does.</summary>
    Camp.MenuList? _confirm;
    Action? _onYes;

    public OptionsScreen(Settings settings, Ui ui, string gameRoot, Action apply, Action? toTitle = null, Action? quit = null, string[]? yesNo = null)
    {
        (_s, _ui, _gameRoot, _apply, _toTitle, _quit) = (settings, ui, gameRoot, apply, toTitle, quit);
        _yesNo = yesNo ?? new[] { "Yes", "No" };
        Open(Page.Main);
    }

    /// <summary>A はい / いいえ question in the screen's middle; はい does <paramref name="then"/> and closes the options.</summary>
    void Ask(string question, Action then)
    {
        int w = Math.Max(150, _ui.Measure12(question) + 36);
        _confirm = new Camp.MenuList { Rect = new Rectangle(320 - w / 2, 196, w, 80), Header = new[] { question, "-" }, ItemX = w / 2 - 18, ItemWidth = 36 };
        _confirm.Set(_yesNo);
        _confirm.Index = 1;   // いいえ first: a stray confirm doesn't leave the game
        _onYes = then;
        Sfx.Click();
    }

    void Open(Page page, int index = 0)
    {
        if (_page == Page.Main && page != Page.Main) _mainIndex = _list.Index;
        _page = page;
        _rows = page switch
        {
            Page.Display => DisplayRows(),
            Page.Audio => AudioRows(),
            Page.Gameplay => GameplayRows(),
            Page.Controls => new List<Row>
            {
                new(T("Keyboard", "キーボード"), () => "", Activate: () => Open(Page.Keyboard)),
                new(T("Gamepad", "ゲームパッド"), () => "", Activate: () => Open(Page.Gamepad)),
            },
            Page.Keyboard => ControlRows(),
            Page.Gamepad => PadRows(),
            _ => new List<Row>
            {
                new(T("Display", "画面"), () => "", Activate: () => Open(Page.Display)),
                new(T("Audio", "サウンド"), () => "", Activate: () => Open(Page.Audio)),
                new(T("Gameplay", "ゲーム"), () => "", Activate: () => Open(Page.Gameplay)),
                new(T("Controls", "操作"), () => "", Activate: () => Open(Page.Controls)),
            },
        };
        if (page == Page.Main)
        {
            if (_toTitle is { } title)
                _rows.Add(new(T("Return to title", "タイトルへ戻る"), () => "",
                    Activate: () => Ask(T("Return to the title screen?", "タイトルへ戻りますか？"), title)));
            if (_quit is { } quit)
                _rows.Add(new(T("Close the game", "ゲーム終了"), () => "",
                    Activate: () => Ask(T("Close the game?", "ゲームを終了しますか？"), quit)));
            _rows.Add(new(T("Back", "戻る"), () => "", Activate: () => Done = true));
        }
        if (page != Page.Main)
            _rows.AddRange(new[] { new Row("", () => ""), new Row(T("Back", "戻る"), () => "", Activate: () => Open(Parent(page).Page, Parent(page).Row)) });
        _list.Index = page == Page.Main && index == 0 ? _mainIndex : index;
        Refresh();
    }

    string PageTitle => _page switch
    {
        Page.Display => T("Display", "画面"), Page.Audio => T("Audio", "サウンド"), Page.Gameplay => T("Gameplay", "ゲーム"),
        Page.Controls => T("Controls", "操作"), Page.Keyboard => T("Controls: keyboard", "操作：キーボード"),
        Page.Gamepad => T("Controls: gamepad", "操作：ゲームパッド"), _ => T("Options", "環境設定"),
    };

    /// <summary>Window sizes to offer: 4:3 sizes the screen can hold (640 × 480 always), the whole multiples marked.</summary>
    static readonly (int W, int H)[] Sizes =
    {
        (640, 480), (800, 600), (960, 720), (1024, 768), (1152, 864), (1280, 960), (1400, 1050), (1440, 1080),
        (1600, 1200), (1920, 1440), (2048, 1536), (2560, 1920),
    };

    static List<(int W, int H)> FittingSizes()
    {
        var mode = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode;
        return Sizes.Where(s => s == (640, 480) || (s.W <= mode.Width && s.H <= mode.Height - 48)).ToList();
    }

    List<Row> DisplayRows()
    {
        var sizes = FittingSizes();
        string[] SizeNames() => sizes.Select(s => s.W % 640 == 0 && s.W / 640 * 480 == s.H ? $"{s.W} × {s.H}  (×{s.W / 640})" : $"{s.W} × {s.H}")
            .Append(T("Fullscreen", "全画面")).ToArray();
        int SizeChosen()
        {
            if (_s.Fullscreen) return sizes.Count;
            var now = _s.WindowSize;
            int i = sizes.IndexOf(now);
            return i >= 0 ? i : sizes.FindLastIndex(s => s.W <= now.Width);
        }
        void SizePick(int i)
        {
            if (i >= sizes.Count) { _s.Fullscreen = true; return; }
            _s.Fullscreen = false;
            (_s.WindowWidth, _s.WindowHeight) = sizes[i];
        }
        // the window, then the look, then the rest
        return new()
        {
            Choice(T("Window size", "画面解像度"), SizeNames, SizeChosen, SizePick),
            Choice(T("Scaling", "拡大方式"),
                () => new[] { T("Pixel-perfect (whole multiples)", "ドット等倍（整数倍）"), T("Fit (keep 4:3)", "画面に合わせる（4:3）"), T("Stretch", "引き伸ばし") },
                () => (int)_s.Scaling, i => _s.Scaling = (Scaling)i),
            Choice(T("Filter", "フィルター"), () => new[] { T("Sharp", "シャープ"), T("Smooth", "なめらか") }, () => _s.Smooth ? 1 : 0, i => _s.Smooth = i == 1),
            Gap(),
            Choice(T("Interface style", "インターフェース"), () => new[] { T("Original", "オリジナル"), T("Gilded", "ゴールド"), T("Modern", "モダン") },
                () => (int)_s.Skin, i => _s.Skin = (UiSkin)i),
            Choice(T("Font (Modern / Gilded)", "フォント（モダン・ゴールド）"),
                () => FontOrder.Select(f => TextFonts.Available(f) ? FontName(f) : FontName(f) + T(" (not on this PC)", "（このPCにありません）")).ToArray(),
                () => Math.Max(0, Array.IndexOf(FontOrder, _s.Font)), i => _s.Font = FontOrder[i]),
            Gap(),
            Choice(T("Language", "言語"), () => new[] { "English", "日本語" }, () => (int)_s.Language, i => _s.Language = (UiLanguage)i),
            Choice(T("Save / load screen", "セーブ・ロード画面"), () => new[] { T("Extended (100 files)", "拡張（100ファイル）"), T("Original (8 files)", "オリジナル（8ファイル）") },
                () => (int)_s.SaveScreen, i => _s.SaveScreen = (SaveScreen)i),
        };
    }

    /// <summary>The fonts as the list shows them: the original's, the pixel fonts, the rest.</summary>
    static readonly UiFont[] FontOrder = { UiFont.MsGothic, UiFont.PixelDot, UiFont.KhDot, UiFont.NotoSerif, UiFont.NotoSans, UiFont.BizUd, UiFont.YuGothic };

    static string FontName(UiFont f) => f switch
    {
        UiFont.MsGothic => T("MS Gothic (the original's)", "ＭＳ ゴシック（オリジナル）"),
        UiFont.PixelDot => T("Pixel: DotGothic16", "ドット：ドットゴシック16"),
        UiFont.KhDot => T("Pixel: KH Dot", "ドット：KHドット小伝馬町"),
        UiFont.NotoSerif => "Noto Serif JP",
        UiFont.NotoSans => "Noto Sans JP",
        UiFont.BizUd => T("BIZ UDPGothic", "BIZ UDPゴシック"),
        _ => T("Yu Gothic UI", "游ゴシック UI"),
    };

    /// <summary>A blank row between groups (skipped by the cursor).</summary>
    static Row Gap() => new("", () => "");

    List<Row> AudioRows()
    {
        var fonts = new List<string> { Settings.BuiltIn };
        fonts.AddRange(Settings.FindSoundFonts(_gameRoot));
        string FontName(string f) => f == Settings.BuiltIn ? T("GeneralUser GS (built-in)", "GeneralUser GS（内蔵）") : Path.GetFileName(f);
        return new()
        {
            new(T("BGM volume", "BGMの音量"), () => "", d => _s.MusicVolume = Math.Clamp(_s.MusicVolume + 10 * d, 0, 100), Wheel: true,
                Level: () => _s.MusicVolume, SetLevel: v => _s.MusicVolume = v),
            new(T("SE volume", "SEの音量"), () => "", d => _s.SoundVolume = Math.Clamp(_s.SoundVolume + 10 * d, 0, 100), Wheel: true,
                Level: () => _s.SoundVolume, SetLevel: v => _s.SoundVolume = v),
            Toggle(T("Combat BGM", "戦闘BGM"), () => _s.CombatBgm, v => _s.CombatBgm = v),
            Choice(T("Confirm sound (thunk)", "決定音"), () => new[] { T("On", "ON"), T("Gameplay only", "メッセージ送り以外"), T("Off", "OFF") },
                () => (int)_s.ConfirmSound, i => _s.ConfirmSound = (ConfirmSound)i),
            Gap(),
            // off Windows there is no Windows MIDI: the choice says so, and the SoundFont plays (Audio.SetSoundFont)
            Choice(T("MIDI playback", "MIDI再生"), () => new[] { OperatingSystem.IsWindows() ? T("Windows MIDI (original)", "Windows MIDI（オリジナル）")
                    : T("Windows MIDI (Windows only)", "Windows MIDI（Windowsのみ）"), T("SoundFont", "サウンドフォント") },
                () => (int)_s.Music, i => _s.Music = (MusicEngine)i),
            Choice(T("SoundFont", "サウンドフォント"), () => fonts.Select(FontName).ToArray(),
                () => Math.Max(0, fonts.IndexOf(_s.ResolveSoundFont(_gameRoot))),
                i => _s.SoundFont = fonts[i] == Settings.BuiltIn ? null : Path.GetFileName(fonts[i])),
            Toggle(T("Loop at loop markers (SoundFont)", "ループ位置で繰り返す（サウンドフォント）"), () => _s.LoopMarkers, v => _s.LoopMarkers = v),
        };
    }

    // battles and the enemy's turn, fast-forward, the map and menus, the mouse
    List<Row> GameplayRows() => new()
    {
        Choice(T("Battle animations", "戦闘演出"), () => new[] { T("On", "ON"), T("Player units only", "自軍ユニットのみ"), T("Simple", "簡易演出") },
            () => (int)_s.BattleSceneMode, i => _s.BattleSceneMode = (BattleSceneMode)i),
        Choice(T("Unit movement speed", "ユニット移動速度"), () => WalkSpeeds.Select(n => n == 1 ? T("×1 (original)", "×1（オリジナル）") : $"×{n}").ToArray(),
            () => Math.Max(0, Array.IndexOf(WalkSpeeds, _s.UnitSpeed)), i => _s.UnitSpeed = WalkSpeeds[i]),
        Toggle(T("Skip enemy turn", "敵ターンスキップ"), () => _s.SkipEnemyTurn, v => _s.SkipEnemyTurn = v),
        Gap(),
        Choice(T("Fast-forward key", "早送りキー"), () => new[] { T("Hold", "押している間"), T("Toggle", "切り替え") },
            () => (int)_s.FastForwardMode, i => _s.FastForwardMode = (FastForwardMode)i),
        Choice(T("Fast-forward speed", "早送り速度"), () => Speeds.Select(n => $"×{n}").ToArray(),
            () => Math.Max(0, Array.IndexOf(Speeds, _s.FastForwardSpeed)), i => _s.FastForwardSpeed = Speeds[i]),
        Toggle(T("Fast-forward skips battles", "早送り中は戦闘をスキップ"), () => _s.FastForwardSkipsScenes, v => _s.FastForwardSkipsScenes = v),
        Toggle(T("Fast-forward advances text", "早送りでメッセージを送る"), () => _s.FastForwardAdvancesText, v => _s.FastForwardAdvancesText = v),
        Toggle(T("Hold click / confirm to fast-forward", "クリック・決定キー長押しで早送り"), () => _s.FastForwardMouse, v => _s.FastForwardMouse = v),
        Gap(),
        Choice(T("Action menu starts on", "行動メニューの初期位置"), () => new[] { T("First entry", "先頭"), T("End action (original)", "行動終了（オリジナル）") },
            () => _s.ActionMenuFromTop ? 0 : 1, i => _s.ActionMenuFromTop = i == 0),
        Toggle(T("End the turn when every unit has acted", "全員行動後に自動でターン終了"), () => _s.AutoEndTurn, v => _s.AutoEndTurn = v),
        Choice(T("Weakness orbs (unit status)", "弱点属性の表示（ユニット情報）"), () => new[] { T("Off", "OFF"), T("On", "ON"), T("Colorblind (letters)", "色覚サポート（文字付き）") },
            () => (int)_s.WeaknessOrbs, i => _s.WeaknessOrbs = (WeaknessOrbs)i),
        Gap(),
        Toggle(T("Mouse input", "マウス操作"), () => _s.MouseInput, v => _s.MouseInput = v),
        Toggle(T("Scroll with the pointer at the edge", "画面端にカーソルでスクロール"), () => _s.EdgeScrollHover, v => _s.EdgeScrollHover = v),
    };

    static readonly int[] Speeds = { 2, 3, 4, 6, 8 };
    /// <summary>Unit movement speeds: divisors of a step's 16 frames, so every step is whole frames.</summary>
    static readonly int[] WalkSpeeds = { 1, 2, 4 };

    static string ActionName(GameAction a) => a switch
    {
        GameAction.Confirm => T("Confirm", "決定"), GameAction.Cancel => T("Cancel / back", "キャンセル／戻る"),
        GameAction.Info => T("Unit status", "ユニット情報"),
        GameAction.Up => T("Cursor / menu up", "カーソル・メニュー上"), GameAction.Down => T("Cursor / menu down", "カーソル・メニュー下"),
        GameAction.Left => T("Cursor left", "カーソル左"), GameAction.Right => T("Cursor right", "カーソル右"),
        GameAction.CameraUp => T("Camera up (menu up)", "画面スクロール上（メニュー上）"),
        GameAction.CameraDown => T("Camera down (menu down)", "画面スクロール下（メニュー下）"),
        GameAction.CameraLeft => T("Camera left", "画面スクロール左"), GameAction.CameraRight => T("Camera right", "画面スクロール右"),
        GameAction.FastForward => T("Fast-forward", "早送り"), GameAction.Backlog => T("Text backlog", "メッセージ履歴"),
        GameAction.Options => T("Options", "環境設定"), GameAction.Fullscreen => T("Fullscreen", "全画面切り替え"),
        GameAction.SkipCutscene => T("Skip cutscene", "イベントスキップ"),
        GameAction.EnemyRanges => T("Enemy ranges (all at once)", "敵の行動範囲表示"),
        _ => a.ToString(),
    };

    List<Row> ControlRows()
    {
        var rows = Enum.GetValues<GameAction>().Select(a => new Row(ActionName(a),
            () => _waiting == a ? T("press a key… (right click: cancel)", "キーを押してください…（右クリックで取消）")
                : string.Join(", ", _s.Keys.GetValueOrDefault(a, new()).Select(Controls.Name)),
            Activate: () => _waiting = a)).ToList();
        rows.Add(new Row("", () => ""));
        rows.Add(new Row(T("Reset to defaults", "初期設定に戻す"), () => "", Activate: () => { _s.Keys = Settings.DefaultKeys(); Changed(); }));
        return rows;
    }

    /// <summary>
    /// The gamepad's page: the controller found (its buttons drawn as an Xbox or a PlayStation pad's), then each action's
    /// buttons as pictures; choosing one waits for a button, which is added, or taken off if it was there.
    /// </summary>
    List<Row> PadRows()
    {
        var rows = new List<Row>
        {
            new(T("Controller", "コントローラー"), () => Controls.PadName() ?? T("none connected", "接続されていません")),
            new("", () => ""),
        };
        rows.AddRange(Enum.GetValues<GameAction>().Select(a => new Row(ActionName(a),
            () => _waitingPad == a ? T("press a button… (right click: cancel)", "ボタンを押してください…（右クリックで取消）") : "",
            Activate: () => _waitingPad = a,
            Pad: () => _waitingPad == a ? Array.Empty<Buttons>() : _s.Pad.GetValueOrDefault(a) ?? new())));
        rows.Add(new Row("", () => ""));
        rows.Add(new Row(T("Reset to defaults", "初期設定に戻す"), () => "", Activate: () => { _s.Pad = Settings.DefaultPad(); Changed(); }));
        return rows;
    }

    void Refresh()
    {
        _list.Set(_rows.Select(r => r.Label).ToArray(), _rows.Select(r => r.Change is not null || r.Activate is not null).ToArray());
        _list.Columns = new[] { _rows.Select(r => r.Value()).ToArray() };
        int w = _page == Page.Main ? 200 : 448;
        _list.Rect = new Rectangle((640 - w) / 2, _page == Page.Main ? 150 : 32, w, 16 * _rows.Count + 34);
        if (_list.Index >= _rows.Count || !_list.Enabled[_list.Index]) _list.Index = Math.Max(0, Array.IndexOf(_list.Enabled, true));
    }

    /// <summary>A setting changed: saved, applied, and the page made again (its words follow the language).</summary>
    void Changed()
    {
        _s.Save();
        _apply();
        Open(_page, _list.Index);
    }

    /// <summary>Opens row <paramref name="row"/>'s drop-down below it, its right edge at the values' (kept on the screen).</summary>
    void OpenDrop(int row)
    {
        var names = _rows[row].Choices!();
        int w = Math.Max(96, names.Max(_ui.Measure12) + 36), h = 16 * names.Length + 16;
        var bar = _list.Row(row);
        int x = Math.Clamp(_list.Rect.X + _list.ColumnX[0] + 18 - w, 8, 632 - w);
        int y = bar.Bottom + 2 + h <= 472 ? bar.Bottom + 2 : Math.Max(8, bar.Y - 2 - h);
        _drop = new Camp.MenuList { Rect = new Rectangle(x, y, w, h), ItemX = 18, ItemWidth = w - 36 };
        _drop.Set(names);
        _drop.Index = Math.Clamp(_rows[row].Chosen!(), 0, names.Length - 1);
        _dropRow = row;
        Sfx.Click();
    }

    /// <summary>The bar being dragged (−1 none): it follows the pointer while the left button is held.</summary>
    int _dragRow = -1;
    const int BarX = 236, BarW = 150;

    /// <summary>Row <paramref name="k"/>'s bar, as the pointer catches it (a little beyond its ends).</summary>
    Rectangle BarHit(int k) { var r = _list.Row(k); return new Rectangle(_list.Rect.X + BarX - 6, r.Y, BarW + 12, r.Height); }

    int LevelAt(int x) => Math.Clamp((int)MathF.Round((x - _list.Rect.X - BarX) * 100f / BarW), 0, 100);

    /// <param name="held">the left button is down (a bar's drag goes on)</param>
    public void Update(BattleInput i, Point mouse, Controls controls, bool held = false)
    {
        if (_confirm is { } q)
        {
            if (i.Cancel || i.RightClick) { _confirm = null; Sfx.Cancel(); return; }
            int answer = q.Navigate(i, mouse);
            if (answer < 0) return;
            _confirm = null;
            if (answer == 0) { Done = true; _onYes?.Invoke(); }
            return;
        }
        // a volume bar under drag: the level follows the pointer and is heard at once; saved when let go
        if (_dragRow >= 0)
        {
            if (held && _rows.ElementAtOrDefault(_dragRow) is { SetLevel: { } slide }) { slide(LevelAt(mouse.X)); _apply(); return; }
            _dragRow = -1;
            Sfx.Click();
            Changed();
            return;
        }
        if (_waitingPad is { } pa)
        {
            if (i.RightClick || controls.NewKey() == Keys.Escape) { _waitingPad = null; Refresh(); return; }
            if (controls.NewButton() is { } button)
            {
                var list = _s.Pad.GetValueOrDefault(pa) ?? new();
                if (!list.Remove(button)) list.Add(button);
                _s.Pad[pa] = list;
                _waitingPad = null;
                Changed();
            }
            else Refresh();   // the controller's name, should one be plugged in meanwhile
            return;
        }
        if (_waiting is { } a)
        {
            if (i.RightClick) { _waiting = null; Refresh(); return; }
            if (controls.NewKey() is { } key)
            {
                var list = _s.Keys.GetValueOrDefault(a) ?? new();
                if (!list.Remove(key)) list.Add(key);
                _s.Keys[a] = list;
                _waiting = null;
                Changed();
            }
            return;
        }
        if (_drop is { } drop)
        {
            if (i.Cancel || i.RightClick || (i.Click && !drop.Rect.Contains(mouse))) { _drop = null; Sfx.Cancel(); return; }
            int chosen = drop.Navigate(i with { Left = false, Right = false }, mouse);
            if (chosen < 0) return;
            _drop = null;
            _rows[_dropRow].Pick!(chosen);
            Changed();
            return;
        }
        if (i.Cancel || i.RightClick) { if (_page != Page.Main) Open(Parent(_page).Page, Parent(_page).Row); else Done = true; Sfx.Cancel(); return; }
        var row = _rows.ElementAtOrDefault(_list.Index);
        if ((i.Left || i.Right) && row?.Change is not null) { row.Change(i.Right ? 1 : -1); Sfx.Click(); Changed(); return; }
        // the wheel over a volume: up louder, down quieter, a step a notch as left / right
        if (i.Wheel != 0 && _list.Hover(mouse) is var over and >= 0 && _rows[over] is { Wheel: true, Change: { } step })
        {
            _list.Index = over;
            step(Math.Sign(i.Wheel));
            Sfx.Click();
            Changed();
            return;
        }
        // a click on a volume bar sets it there and starts a drag
        if (i.Click && _drop is null && Enumerable.Range(0, _rows.Count).FirstOrDefault(k => _rows[k].Level is not null && BarHit(k).Contains(mouse), -1) is var b and >= 0)
        {
            (_list.Index, _dragRow) = (b, b);
            _rows[b].SetLevel!(LevelAt(mouse.X));
            _apply();
            return;
        }
        int pick = _list.Navigate(i, mouse);
        if (pick < 0) return;
        var r = _rows[pick];
        if (r.Choices is not null) OpenDrop(pick);
        else if (r.Activate is not null) r.Activate();
        else if (r.Change is not null) { r.Change(1); Changed(); }
    }

    public void Draw(SpriteBatch sb, int frame)
    {
        _ui.Fill(sb, new Rectangle(0, 0, 640, 480), Color.Black * 0.6f);
        _list.Draw(sb, _ui, _drop is null, frame);
        // a small ▼ after the values that open a drop-down
        for (int k = 0; k < _rows.Count; k++)
        {
            if (_rows[k].Choices is null) continue;
            var bar = _list.Row(k);
            int x = _list.Rect.X + _list.ColumnX[0] + 6, y = bar.Y + 6;
            var c = Ui.Gilded ? Ui.Gold : Ui.Modern ? Ui.Accent : Ui.PanelText;
            for (int t = 0; t < 3; t++) _ui.Fill(sb, new Rectangle(x + t, y + t, 7 - 2 * t, 1), c);
        }
        // the volume bars: a track, the part up to the level filled, a knob at the level, the percentage at the values' edge
        for (int k = 0; k < _rows.Count; k++)
        {
            if (_rows[k].Level is not { } level) continue;
            var row = _list.Row(k);
            int v = Math.Clamp(level(), 0, 100), x = _list.Rect.X + BarX, cy = row.Y + row.Height / 2;
            var accent = Ui.Gilded ? Ui.Gold : Ui.Modern ? Ui.Accent : Ui.PanelText;
            _ui.Fill(sb, new Rectangle(x, cy - 2, BarW, 4), Color.Black * 0.55f);
            _ui.Fill(sb, new Rectangle(x, cy - 2, BarW * v / 100, 4), accent);
            int kx = x + BarW * v / 100;
            _ui.Fill(sb, new Rectangle(kx - 3, cy - 6, 7, 12), Color.Black * 0.6f);
            _ui.Fill(sb, new Rectangle(kx - 2, cy - 5, 5, 10), k == _dragRow ? Color.White : accent);
            if (_ui.SmallText.Get($"{v}%") is { } pct)
                pct.Draw(sb, new Vector2(_list.Rect.X + _list.ColumnX[0] - pct.Width, Ui.Line16Y(pct, row.Y)), Ui.PanelText);
        }
        // gamepad buttons as pictures, twice their pixels, right-aligned where the values end
        bool ps = PadGlyphs.PlayStation(_page == Page.Gamepad ? Controls.PadName() : null);
        for (int k = 0; k < _rows.Count; k++)
        {
            if (_rows[k].Pad?.Invoke() is not { Count: > 0 } buttons) continue;
            var row = _list.Row(k);
            int w = PadGlyphs.Measure(buttons, ps, 2, 4);
            _ui.Pad.Draw(sb, buttons, ps, _list.Rect.X + _list.ColumnX[0] - w, row.Y + (row.Height - 2 * PadGlyphs.Height) / 2, 2, 4, Color.White);
        }
        _ui.SmallText.Get(PageTitle)?.Draw(sb, new Vector2(_list.Rect.X + 8, _list.Rect.Y - 16), Ui.Gilded ? Ui.GoldLight : Ui.PanelText);
        if (_drop is { } drop)
        {
            // solid under the (translucent) restyled windows, so the rows behind stay hidden; rounded like Modern's window
            if (Ui.Modern) _ui.ModernBox(sb, drop.Rect, new Color(8, 10, 18), 10, 1f, shadow: false);
            else _ui.Fill(sb, drop.Rect, new Color(8, 10, 18));
            drop.Draw(sb, _ui, true, frame);
        }
        if (_confirm is { } q)
        {
            if (Ui.Modern) _ui.ModernBox(sb, q.Rect, new Color(8, 10, 18), 10, 1f, shadow: false);
            else _ui.Fill(sb, q.Rect, new Color(8, 10, 18));
            q.Draw(sb, _ui, true, frame);
        }
    }
}
