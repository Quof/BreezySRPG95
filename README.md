# BreezySRPG95

A replacement player for Sim RPG Maker 95 (シミュレーションRPGツクール95) games.
It never modifies the game's files.

Tested: えるまに, ＯtoＺ, 昴の騎士, 闇鍋企画 (前編, 完結編).

## Setup

1. Put `BreezySRPG95.exe` in the game's folder, next to `SRPGEXEC.EXE`.
2. Run it.

Windows 10/11, 64-bit. Nothing to install.
From elsewhere: `BreezySRPG95.exe --game "C:\path\to\game folder"`

- Menus start in English on an English system, else Japanese.
  To change: F1 > Display (画面) > Language (言語).
- Settings: `%APPDATA%\BreezySRPG95\settings.json` (shared by all games).
- The first start unpacks three DLLs to `%TEMP%\.net\BreezySRPG95`.

Linux and macOS: see `README-LINUX.txt` / `README-MAC.txt` in those packages.

## Features

1) Resizable window and borderless fullscreen with various scaling and filtering options.
2) Fully rebindable keyboard controls so one does not have to use the mouse for everything, plus gamepad support too.
3) Fast-forward (hold or toggle) button to speed up movement, battles, and/or text.
4) Option to skip the enemy turn (with notifications for EXP gain and such throughout the turn)
5) Cutscene skip
6) Faster unit movement (1x, 2x, and 4x options)
7) Save anywhere, including in the middle of cutscenes.
8) 100 save slots instead of 8 - and save compatibility between the original and this interpreter.
9) Battle forecast before combat showing damage, accuracy, etc.
10) Enemy range display to see the overall zone of danger from all enemies combined
11) Clicking units also shows their movement and attack range.
12) Optional weakness display to show what elements units are weak to, as well as indicators for when your attack is boosted by said elements.
13) Optional HP display under units on the map
14) Text backlog with sprites
15) New user interfaces, plus various font choices
16) Built-in MIDI soundfont to play the music at a higher quality (with optional looping)
17) Can turn off combat BGM (it's a problem because the combat bgm only plays for like 10 seconds, and resets the background music, interrupting it constantly across a map)
18) Various modes for battle animations (can turn them off and have simplified combat reports like in modern SRPGs)
19) Terrain info shown on cursor hover
20) More detail when inspecting units, e.g. can see their abilities outside of the camp menu + can see the stats on equipment
21) Numerous more fixes and minor features not necessarily worth mentioning in details, like BGM and SE volume sliders and being able to cycle through ally units that can still move.

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

The licences in full are in each release package's `Third-Party-Licenses.txt`.

- .NET 8 (Microsoft), MonoGame 3.8 (with NVorbis), SDL2.
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
