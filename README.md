# BreezySRPG95

A replacement player for Sim RPG Maker 95 (シミュレーションRPGツクール95) games.
It never modifies the game's files.

Tested: えるまに, ＯtoＺ, 昴の騎士, 闇鍋企画 (前編, 完結編).

## Setup

1. Put `BreezySRPG95.exe` in the game's folder, next to `SRPGEXEC.EXE`.
2. Run it.

Windows 10/11, 64-bit. Nothing to install.
From elsewhere: `BreezySRPG95.exe --game "C:\path\to\game folder"`

- Menus start in Japanese. For English: F1 > 画面 > 言語.
- Settings: `%APPDATA%\BreezySRPG95\settings.json` (shared by all games).
- The first start unpacks three DLLs to `%TEMP%\.net\BreezySRPG95`.

Linux and macOS: see `README-LINUX.txt` / `README-MAC.txt` in those packages.

## Saves

- Files 1-8 are the original's `SAVE1-8.DAT` and work in both programs.
  Files 9-100 are BreezySRPG95 only.
- Right-click during a cutscene to save in it. The original program loads
  such a save at the turn menu instead.
- Play times and pictures are kept in a `BreezySRPG95` folder by the saves.

## Controls

Change them under F1 > Controls.

| Key | Action |
|---|---|
| Enter / Z, left click | confirm; on a unit, its move and attack range |
| Esc / X, right click | cancel; on an empty tile, the turn menu |
| Arrow keys | cursor |
| W A S D | scroll the map |
| C / Left Shift | unit status |
| Tab / Left Ctrl (hold) | fast-forward |
| Space | skip the cutscene |
| Wheel up / L | text backlog (Up / Down scroll it) |
| R | every enemy's range |
| Q / E | previous / next unit that hasn't acted |
| F1 | options |
| F4 / Alt+Enter | fullscreen |
| F5 | pixel-perfect / fit scaling |

Gamepad: A confirm, B cancel, X status, Y options, View fast-forward,
Menu skip cutscene, LB backlog, RB enemy ranges, L2 / R2 previous / next
unit, D-pad / left stick cursor, right stick map.

## Problems

- A crash shows a message and writes `error.log` next to the exe (or in the
  settings folder). Please send it.
- A missing game file is named on screen, then skipped.
- When reporting, give the game, the map, and how it differs from
  `SRPGEXEC.EXE`.

## Credits and licences

- .NET 8 (Microsoft), MonoGame 3.8, SDL2.
- OpenAL Soft 1.25.2 (Chris Robinson), LGPL.
- FreeType 2.13, through FreeTypeSharp (Linux and macOS text). Portions of
  this software are copyright (C) 2023 The FreeType Project
  (www.freetype.org). All rights reserved.
- MeltySynth (MIDI synthesizer).
- NLayer 3.0.0, modified (MP3 music). MIT License, Copyright (c) 2018
  Mark Heath, Andrew Ward & Contributors.
- GeneralUser GS v2.0.3 SoundFont, S. Christian Collins
  (www.schristiancollins.com).
- Fonts: Noto Sans JP, Noto Serif JP (Adobe / Google), DotGothic16
  (Fontworks), KH Dot Kodenmachou (Keitarou Hiraki): SIL Open Font License
  1.1, see `Fonts-OFL.txt`. PixelMplus12 (Itou Hiroki, M+ FONTS PROJECT):
  see `Fonts-M+.txt`.
- Icon: FACE_003 from 昴の騎士.

Building from source: [client/BUILDING.md](client/BUILDING.md).
