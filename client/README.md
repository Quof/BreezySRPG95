# BreezySRPG95

A modern replacement client for games made with Sim RPG Maker 95 (シミュレーションRPGツクール95), started for えるまに and checked against ＯtoＺ, 昴の騎士 and 闇鍋企画 as well. It reads the original game folder directly (`SRPGEXEC.EXE`'s data files, BMP graphics, MIDI/WAV audio, and MP3 music shipped for a music setup tool) and never modifies it. Format documentation lives in [`../re/FORMAT_NOTES.md`](../re/FORMAT_NOTES.md).

## Stack

| Piece | Choice | Why |
|---|---|---|
| Language/runtime | C# / .NET 8 | Ships as a self-contained Windows build (no runtime install for players). Fast, with good tooling and first-class Shift-JIS support via `System.Text.Encoding.CodePages` |
| Engine | MonoGame 3.8 (DesktopGL) | A code-first 2D framework. The game is fully data-driven from the original files, so a scene editor (Godot/Unity) would add weight without helping. Gives pixel-perfect scaling and borderless fullscreen |
| Music | MeltySynth + a General MIDI SoundFont | Pure-C# SoundFont synthesizer: consistent, good-sounding MIDI on every PC instead of the Windows GS wavetable, with user-replaceable SoundFonts |
| MP3 | [NLayer](https://github.com/naudio/NLayer) 3.0.0, as source with two fixes (`src/NLayer`) | Pure-C# MP3 decoder for the music a game ships as MP3s (Ruby2); fixed where it dropped frames (its bit reservoir's wrap) and mangled MPEG-2 intensity stereo, to decode as Windows' own decoder does |
| Text | Windows GDI on Windows; FreeType ([FreeTypeSharp](https://github.com/ryancheung/FreeTypeSharp)) elsewhere | GDI draws the original's own MS Gothic exactly, and the Windows fonts; FreeType draws the fonts inside the exe the same way on Linux and macOS, with IPAGothic in MS Gothic's place (`Ui/TextFont.cs`) |

Alternatives considered:
- **Godot 4:** no native MIDI, and its scene editor isn't needed.
- **Python/pygame:** distribution and performance.
- **Rust:** slower iteration for little gain here.

## Layout

```
client/
  src/Srpg95.Data/          format loaders (C# port of re/srpg95_data.py)
  src/BreezySRPG95/              the game (MonoGame)
  src/NLayer/               the MP3 decoder (NLayer's source, fixed: see its README)
  tests/Srpg95.Data.Tests/  cross-checks every loader against the Python reference JSON
```

## Build and run

Needs the .NET 8 SDK.

```bash
dotnet build client/BreezySRPG95.sln
```

```bash
dotnet run --project client/src/BreezySRPG95 -- --map 1
```

```bash
dotnet test client/tests/Srpg95.Data.Tests
```

Options:
- `--game DIR`: the game folder. By default it searches next to and above the executable for a folder containing `SRPGEXEC.EXE`.
- `--rtp DIR`: the RTP folder used as a fallback for graphics and audio. Default `srpg maker 95/RTP`.
- `--map N`: the map to open (1-based, as in `SMAP_nnn`).
- `--screenshot FILE.png`: render one frame at 640×480, save it and exit. It also writes `FILE.txt` with the battle state (units, HP, EXP) and a trace of the scripted input. In this mode the real keyboard and mouse are ignored and the random numbers of battles and of events (variable operations) are seeded, so runs are reproducible.
- `--scale N`: used with `--screenshot`, renders and saves the frame at N × 640×480, as a window N times the size draws it.
- `--fast 1`: used with `--screenshot`, runs frames as fast as the machine allows instead of 60 a second. The game advances by frames, so the outcome is the same, only sooner.
- Scripted runs take their look from the environment instead of the settings: `BREEZY_SKIN` (original / modern / gilded), `BREEZY_UIFONT` (NotoSans / NotoSerif / BizUd / YuGothic / MsGothic / PixelDot / KhDot), `BREEZY_TEXT=freetype` (Windows: the text through FreeType, as on Linux and macOS, instead of GDI), `BREEZY_LANGUAGE` (english / japanese), `BREEZY_SAVESCREEN` (extended / original), `BREEZY_COMBATBGM` (`off` turns the combat BGM off).
- `--frames N`: used with `--screenshot`, captures at frame N at the latest (default 30000), so a run that never settles still ends. A scripted run also appends map, camp, title and game-over lines to `FILE.log` as they happen, and a crash leaves `FILE.error.txt`.
- `--choose "a,b,..."`: used with `--advance`, the option each character choice takes in turn (0-based, or `last`); `random` picks every choice from then on at random (seeded, so a run repeats).
- `--advance N`: used with `--screenshot`, auto-confirms N event messages or choices before capturing. `-1` confirms all of them.
- `--keys "a,b,..."`: used with `--screenshot`, scripted input fed one token every 10 frames while the map is idle:
  - `up`, `down`, `left`, `right`, `enter`, `esc`;
  - `at:X.Y` puts the cursor on tile (X,Y);
  - `wait` skips a beat;
  - `shot+N` captures N frames later, even mid-animation.
  - `telop+N` / `telopK+N` captures N frames after the next caption (any, or `TELOPK.BMP`: 1 PLAYER TURN, 2 ENEMY TURN, 3 COMPLETE!!) appears.
  - `choice+N` / `choiceK+N` captures N frames after the next (the K-th) character choice comes up, leaving it unanswered.
  - `endturn` ends the player's turn, as ターン終了 does.
  - `turnmenu` opens the turn menu, as a click on an empty tile does (`down,down,down,enter` then picks ロードする).
  - `wheelup` / `wheeldown` turn the mouse wheel a notch.
  - `skip` opens the cutscene skip's question (Space) as soon as an event shows a message, `skipany` as soon as an event runs at all (mid-move, mid-pan, mid-fade); `skipyes` answers はい. `client\tools\skips.ps1` uses them.
  - `point:X.Y` puts the pointer at (X, Y) on the 640×480 screen and keeps it there, for what reads the pointer rather than a map tile (the options' rows, the camp's and the title's menus): `options,down,enter,point:320.48,wheeldown` turns the BGM volume down a step.
  - Stress tests: `fuzz:N[:seed]` sends N frames of random game input (keys, clicks, right clicks, wheel, pointer) whatever the game is doing; `keyfuzz:N[:seed]` holds and releases random keys and mouse buttons through the real keyboard and mouse path instead (options, backlog, skip and debug keys included); `keycalm:N[:seed]` then mashes only Esc and the mouse buttons, as a player would to get back. A run that never finishes its later tokens is stuck.
  - `ffhold` / `ffrelease` hold or let go of the fast-forward key at once; `msg` waits until an event shows a message or a choice.
  - `rclick` right-clicks as soon as an event shows a message or a choice, once the `--advance` confirms are done (the event's menu: save, load, options); `rclickfx` as soon as it shows neither (a screen effect, a move, a pan, a wait). With the menu up, the other keys work it: `enter` saves, `down,enter` loads, `down,down,enter` opens the options; in the save screen `enter` picks the file (a save made in an event starts on file 9).

  Example: select ジュナ on map 1 and move her 3 tiles up:

  ```bash
  dotnet run --project client/src/BreezySRPG95 -- --map 1 --advance -1 --keys "at:7.12,enter,at:7.9,enter" --screenshot shot.png
  ```
- `--event NAME`: run this script on load instead of the first map's `OPENING`, e.g. `--map 4 --event EV000101` (object 1's first event slot). With `--advance -1 --choose random --fast 1 --screenshot` this runs any event of any map by itself.
- `--load N`: load `SAVEn.DAT` at start (a camp save opens its camp).
- `--saves DIR`: where `SAVEn.DAT` files are read and written. The default is the game folder, as in the original; point it elsewhere to keep the game folder untouched.
- `--camp N` (developer): open the camp before map N.
- `--level N` (developer): allies start the map at level N or higher, e.g. `--level 12` gives ルーティ her spells on map 1.
- `--place "UNIT:X.Y[.HP],..."` (developer): move a unit (UNIT.DAT id) to a tile, keep it shown, and optionally set its HP. For example, `--map 2 --place 245:15.13,247:16.13.9` puts グレイ next to a hurt ジュリア.
- `--give "ID:N,..."` (developer): add N of item ID (0-based ITEM.DAT index) to the inventory, e.g. `--give 40:2` for two ポーション.

Set `SRPG95_REQUIRE_DATA=1` to make the tests fail instead of silently passing when the game data or `re/json` is missing.

## Playing

Put `BreezySRPG95.exe` (one file, from a published build) in the game folder, next to `SRPGEXEC.EXE`, and run it; or run it from anywhere with `--game "<game folder>"`. No .NET install is needed: the runtime, SDL2, OpenAL and FreeType are inside the exe (the three native libraries are unpacked to `%TEMP%\.net\BreezySRPG95` on the first start; FreeType only draws text with `BREEZY_TEXT=freetype`). Nothing is written next to the exe: settings live in `%APPDATA%\BreezySRPG95\settings.json`, shared by every game, and saves go where the original keeps them (the game folder). A `settings.json` placed beside the exe is used instead, for a setup that keeps its own.

Music plays through **GeneralUser GS** (v2.0.3, by S. Christian Collins, [schristiancollins.com](http://www.schristiancollins.com)), a GS SoundFont embedded in the exe, so everyone hears the same soundtrack; its licence ([`src/BreezySRPG95/soundfont/GeneralUser-GS-LICENSE.txt`](src/BreezySRPG95/soundfont/GeneralUser-GS-LICENSE.txt)) allows bundling it. Under Options you can switch to another `.sf2` put in a `soundfonts` folder next to the exe or in the game folder, or to Windows MIDI as in the original. A MIDI track plays without the silence before its first note and after its last one, so no repeat waits: ＯtoＺ's `legend.mid` ended in 26 s of silence. The first build downloads the SoundFont from the author's repository (hash-checked); it isn't kept in git.

The sound follows Windows' default output: switch from speakers to headphones (or plug them in) and it moves there, the music and sounds going on (`Runtime/AudioDevice.cs`). MonoGame's OpenAL Soft 1.18.1 stays on the device it opened, so the Windows build uses OpenAL Soft 1.25.2 in its place, which reports the change (`ALC_SOFT_system_events`) and moves a playing device in place (`ALC_SOFT_reopen_device`); the first build downloads the official release (hash-checked) and keeps its 64-bit `soft_oal.dll` and licence in `src/BreezySRPG95/openal/`, not in git.

Ruby2 ships its CD-quality music as MP3s in `MP3_File`, for MusicSetUp (`MP3_D.exe`, by 稲富祥哲) to decode once into WAVs in `BGM` (380 MB), the names its events and maps ask for; its first map plays one as a test and asks the player to set the music up if nothing is heard. A WAV track that isn't in `BGM` plays from `MP3_File`'s MP3 of the same name instead, decoded as it plays and looped as the WAV would be (`Runtime/Audio.cs`: `SetUpMp3`); once set up, the WAVs play. The F3 overlay and a scripted run's state show `(MP3_File/…)` after the track's name.

```bash
dotnet publish client/src/BreezySRPG95 -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o client/publish
```

### Linux

```bash
dotnet publish client/src/BreezySRPG95 -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o client/publish-linux
```

The program is one file there too (`BreezySRPG95`), but its three native libraries — `libSDL2-2.0.so.0`, `libopenal.so.1` and `libfreetype.so` — stay files beside it: MonoGame and FreeTypeSharp look beside the program first, and unpacked elsewhere they'd be found only after the system's own copies (`/usr/lib`). It needs no ICU (the culture is invariant: nothing sorts, compares or formats by culture), and runs on glibc 2.33 or newer, which FreeType's library needs: Ubuntu 22.04, Debian 12, Fedora 34, Linux Mint 21, SteamOS 3 or newer, x86-64 only (MonoGame carries no ARM Linux libraries). `python client/tools/make_tarball.py <folder> <out.tar.gz> --exec BreezySRPG95` packs a folder into a `.tar.gz` that keeps the program executable, which zip and rar files made on Windows don't. On Linux the text goes through FreeType, with IPAGothic for MS Gothic (see the fonts under Options), MIDI only through the SoundFont (there's no Windows MIDI), settings live in `~/.config/BreezySRPG95/settings.json`, and the game's files are found whatever their case or Unicode form (`Srpg95.Data/FileNames.cs`). The games' own archives have Shift-JIS file names: they must be unpacked with a tool that converts them (`unar -e Shift_JIS`, `7z x -mcp=932`, Debian's `unzip -O cp932`).

The icon is 昴の騎士's face graphic FACE_003, made into `src/BreezySRPG95/Icon.ico` by `client/tools/make_icon.py`.

The project, its folder (`src/BreezySRPG95`) and its namespace are all called BreezySRPG95 (it was EasySRPG95 before, and Erumani while it played only えるまに).

## Controls

Default keys (SRPG Studio style); all can be rebound under Options → Controls → Keyboard.

A gamepad works too (the first one connected: Xbox, PlayStation and most others, through SDL), rebound under Options → Controls → Gamepad, which names the controller and shows its buttons as pictures (an Xbox or a PlayStation pad's, by its name; `Ui/PadGlyphs.cs`, from DT3's `sKeyPressPad`). By default, as SRPG Studio numbers its pad: A confirm, B cancel, X unit status, Y options, View fast-forward, Menu skip the cutscene, LB the backlog, RB enemy ranges, the D-pad and the left stick the cursor, the right stick the camera.

| Keys | Action |
|---|---|
| Enter / Z, left click | confirm. On a unit: select it, or show its status if it can't act; a unit's movement range (blue) comes with its attack range around it (red: its weapon's and its harmful spells' reach, not on its own side's units) — your units' too, while you move them. On an empty tile: the turn menu |
| Esc / X / Backspace, right click | cancel or back (at the command menu: undo the move). During a battle scene: skip it. A right click on an empty tile opens the turn menu, which also has 環境設定 (Options) |
| Arrows | move the map cursor (hold to repeat); move in menus |
| W A S D | scroll the map camera; W/S also move in menus |
| C / Left Shift, right click on a unit | unit status; C / Left Shift again shows the weapon's, armour's, accessory's stats in turn (pointing at an item's name does too). On the map, ← / →, the wheel or Enter / Z (not a click) turn to the unit's abilities (the arrows at the window's sides): each with its TP cost, the chosen one's description above (??? where the game gives none); C / Left Shift, ↑ ↓ or the pointer choose one. While a spell is aimed, C / Left Shift steps the forecast on to the next unit the spell reaches |
| Tab / Left Ctrl | fast-forward (hold, or toggle; see Options). When gameplay gives way to an event's story, a fast-forward stops before its first line: let go and press again (toggle again) to skip the story too |
| Mouse wheel up / L | text backlog, a message per notch, each with the face it was spoken with in a white frame (narration unframed in the same column; wheel down past the newest or a click closes it). While a spell is aimed, the wheel is the forecast's instead: it opens on the nearest unit the spell is meant for (framed when there are several, with "2/3" between the names), and the wheel steps through all the units it reaches |
| Space | during an event: skip the rest of it (asks first) |
| R | enemy ranges: every enemy's reach for its coming phase (moves and attacks, spells included) as one area — blue (the movement range's darker shade) where enemies can move to, red around it where they can only hit; press again to hide |
| F1 | options (anywhere) |
| Right click during an event | a menu at the pointer: save, load, options (環境設定), in the middle of the cutscene |
| F4 or Alt+Enter | fullscreen |
| F5 | switch pixel-perfect / fit scaling |
| F3 | debug overlay (map, BGM, event log) |
| PageUp / PageDown | previous / next map (developer; only while the F3 overlay is up) |

## Options (modern features)

F1 opens them anywhere; the title screen, the camp menu and a map's turn menu list them as 環境設定 (Options; above ゲーム終了 on the title and in the camp, last on the turn menu, which has no ゲーム終了: the window's close button quits); during an event (a cutscene), a right click's menu has them too (see [Saving in the middle of an event](#saving-in-the-middle-of-an-event)), and the event carries on where it was once they're closed. A setting with several values steps through them with ← / → and opens a drop-down list of them on a click or Enter. Opened in a game (not from the title), the main page also has タイトルへ戻る (Return to title) and ゲーム終了 (Close the game) above 戻る, each asking はい / いいえ first (いいえ chosen).

| Section | Settings |
|---|---|
| Display | interface style (original / gilded / modern); font of the modern and gilded styles (listed as MS Gothic — the face the original draws its messages in —, the pixel fonts DotGothic16 with PixelMplus12 and the KH Dot font, then Noto Serif JP, the default, Noto Sans JP, BIZ UDPGothic and Yu Gothic UI); language of BreezySRPG95's own menus and messages (日本語 by default, or English — the game's text is the game's); window size (4:3 sizes that fit the screen, whole multiples marked, then fullscreen, borderless); scaling (pixel-perfect whole multiples / fit 4:3 / stretch); filter (sharp / smooth); save / load screen (extended: 100 files / original: 8) |
| Audio | BGM and SE volume (bars: click or drag along them, ← / → or the wheel in steps of 10), loop at loop markers (off at first; see below), MIDI playback (SoundFont via MeltySynth, or Windows MIDI like the original), SoundFont file, combat BGM (off: a fight plays the turn's own music, which goes on uninterrupted; music an event put on still ends with the next fight, as the original's battle music ends it), confirm sound (the CLICK "thunk": on, gameplay only — everywhere but clicking a message away; the default — or off; saving and loading play it even when it's off) |
| Gameplay | unit movement speed on the battle map (×1 the original's, ×2 at first, ×4; event moves keep the original pace), weakness orbs on the unit status portrait (off, on, or colour-blind with the attribute's letter: A red, B yellow, C green, D blue, E white, F black, A at the top), end the turn by itself once every unit of yours has acted (off at first), battle animations (戦闘演出): on, player units only (allies, guests and neutral units: an enemy's attack or spell shows just its result) or simple (results only), skip enemy turn, fast-forward key (hold / toggle), mouse input (off: keyboard only — no clicks, wheel, pointer or hover; F1 still opens the options), hold left click or the confirm key to fast-forward (after 0.4 s in battle, 1 s in a cutscene, so a slow click on a message doesn't skip the next; a press still confirms), scroll with the pointer at the edge (on the screen frame; only when the original lets a held click scroll there), action menu starts on 行動終了 (the original's, and the default) or its first entry, speed (×2–×8, ×2 at first), fast-forward skips battle scenes (off at first), fast-forward advances text |
| Controls | keyboard or gamepad: rebind every action (a key or button added, or taken off if it was there); reset to defaults |

The frame is drawn at the window's resolution: the pixel art is scaled up whole, and the modern and gilded styles' text and gold are drawn at the window's own pixels, so they stay crisp at any size. The filter only matters when the window isn't a whole multiple of 640×480.

Fonts: Noto Sans JP and Noto Serif JP travel inside the exe (SIL Open Font License 1.1, [`src/BreezySRPG95/fonts/OFL.txt`](src/BreezySRPG95/fonts/OFL.txt)); the build copies them from Windows' fonts folder (Windows 11 has them) and they aren't kept in git. So do two pixel fonts, which aren't Windows': put their files in `src/BreezySRPG95/fonts/` before building — `DotGothic16Pixel.ttf`, made from [`DotGothic16-Regular.ttf`](https://github.com/fontworks-fonts/DotGothic16/tree/master/fonts/ttf) (OFL) by `python tools/make_pixel_font.py DotGothic16-Regular.ttf` (it draws each dot as a separate square, and GDI without anti-aliasing runs dense kanji together; the tool rebuilds it with every dot a whole pixel's square), `PixelMplus12-Regular.ttf` from [PixelMplus](https://github.com/itouhiro/PixelMplus/releases) (M+ FONT LICENSE, [`fonts/MPLUS-LICENSE.txt`](src/BreezySRPG95/fonts/MPLUS-LICENSE.txt)), and `KH-Dot-Kodenmachou-12.ttf` and `KH-Dot-Kodenmachou-16.ttf` from the [KH Dot font](http://jikasei.me/font/kh-dotfont/) (OFL). A pixel font is drawn at the size its dots were made for — 16 px for messages and names, 12 px for everything smaller — without anti-aliasing, at the 640 × 480 screen's own size, and enlarged with the rest of the frame, so each dot is a square of the window's pixels. BIZ UDPGothic, Yu Gothic UI and MS Gothic are Windows' own (MS Gothic's licence doesn't let it be bundled); a font that isn't there falls back to Yu Gothic UI. Off Windows the text goes through FreeType (`Ui/FreeTypeText.cs`) from the fonts inside the exe alone: the Windows fonts aren't there (Noto Sans JP takes their place), and the original skin's MS Gothic is played by IPAGothic, `ipag.ttf` from [ipag00303.zip](https://moji.or.jp/ipafont/ipa00303/) (IPA Font License v1.0, [`fonts/IPA_Font_License_Agreement_v1.0.txt`](src/BreezySRPG95/fonts/IPA_Font_License_Agreement_v1.0.txt)) put in `src/BreezySRPG95/fonts/` as it is — embedded where GDI isn't given it, and left out of a Windows build. FreeType follows GDI's numbers (line heights, widths, simulated bold, Yu Gothic UI's metrics for the layout, measured with GDI), so with a pixel font every screen comes out pixel-identical to GDI's. Each font is drawn a little smaller than Yu Gothic UI and placed where Yu Gothic UI's glyphs were, so every layout keeps its lines. The modern and gilded message boxes fit the font: as wide as a full line in it (20 full-width characters, the editor's limit; a longer line in a game widens it again), narrowed on the side away from the face, centred when there is none.

### The extended save / load screen

100 files: `SAVE1.DAT`–`SAVE100.DAT` beside the game, in the original's format (the original game sees files 1–8). The list on the left shows each file's name (the game's own 「ファイル１」 naming), キャンプ or the turn, the play time, the map's number and when it was saved; the right shows a picture of the screen when it was saved and the details. Map names are left out: the game's `MAPNAME.DAT` holds the author's working names, not ones meant for players. Saving over a file or loading one asks first. ↑ ↓ move, ← → page through five files at a time, the mouse wheel scrolls the list a file at a time (the file that comes under the pointer is the one chosen), a click picks.

SRPG Maker 95 keeps no play time and no picture, so BreezySRPG95 notes them itself in an `BreezySRPG95` folder beside the saves (`SAVEn.png`, `saves.json`), tied to each save file's contents: a save the original game wrote shows `--:--:--` and no picture, and so does a game continued from one. Play time counts while a map or camp is up and the window is in front.

Opening the screen reads nothing it already knows: the files' details are kept between visits (`Runtime/SaveSlots.cs`) — looked at in the background when the game starts and after each save, and on opening listed again in one go, a file read again only if its time or size changed or the game saved to it — where reading and hashing every save took 30 ms warm and half a second or more from a cold disk. The pictures are read and decoded on another thread, one at a time, the chosen file's first (`Runtime/Png.cs`, which also writes them: about 30 ms where MonoGame's encoder took 130). Saving writes `SAVEn.DAT` at once; the picture's PNG and the note are written after it in the background (`SaveExtras.WriteLater`), in order, each file replaced whole, and whatever reads the notes next waits for them, as does quitting.

### Saving in the middle of an event

The original saves only from the turn menu and the camp, between events, and a right click does nothing while an event runs. In BreezySRPG95 a right click during an event (a cutscene, a choice, a battle's opening words) opens セーブ / ロード / 環境設定 (Save / Load / Options) at the pointer; the event waits meanwhile.

- **Save** opens the extended save screen (whatever the "Save / load screen" option says) and writes the map as it stands into `SAVEn.DAT` as an ordinary battle save; the rest — where the event stands (its script, the block and command it's at, the message or choice up, a wait's time left), the pictures, the screen's brightness, the music, the camera, the fight a "before" event holds up (as rolled), what follows the event (the first turn after an opening, the rest of a fight's "after" events, the enemy phase) — goes into the save's note in `saves.json`. A fade, a pan or a unit's move still under way jumps to its end first, and the event goes on from there. The picture keeps the event's message and choice. The list marks such a save "Turn n (event)" / 「ターン n（イベント中）」. It can go in any file, but only BreezySRPG95 carries on with the event: the original game, loading one from files 1–8, starts that battle at the turn menu, and the rest of the event is lost.
- **Load** leaves the event for any file (a save made in an event carries on with that event, from the turn menu, the camp or the title too).
- **Options** opens the options.

Should the save file change (the original game saving over it), its note no longer counts and the file loads as the plain battle save it is.

Faithfulness rule: replicate the original's behaviour, including its bugs (see FORMAT_NOTES §11.4), by default. Any fixes are opt-in.
