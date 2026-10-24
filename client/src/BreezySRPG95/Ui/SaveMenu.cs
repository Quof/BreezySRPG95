using BreezySRPG95.Runtime;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Srpg95.Data;
using static BreezySRPG95.Ui.Lang;

namespace BreezySRPG95.Ui;

/// <summary>
/// The extended save / load screen (modern; the "Save / load screen" option, in place of the original's ファイル１–８ in
/// a small panel). 100 files — SAVE1–100.DAT, of which the original game knows 1–8 — listed on the left five at a time:
/// the file's name, the turn (or キャンプ), the play time, the map's number and when it was saved; on the right the
/// chosen file's picture of the screen and its details. Saving over a file and loading one ask first. The map and
/// キャンプ / the turn are the save's own data (MAPNAME.DAT's names are left out: they're the author's working names, not
/// meant for players); the play time and the picture are BreezySRPG95's notes beside the saves (<see cref="SaveExtras"/>):
/// "--:--:--" and no picture for a save it didn't write. A save made in the middle of an event (<see cref="SceneSave"/>)
/// says so after its turn. The files' details come from <see cref="SaveSlots"/>, kept between visits; the pictures are
/// read and decoded off the game thread, the chosen file's and those in sight first, and drawn when they're ready.
/// </summary>
public sealed class SaveMenu : IDisposable
{
    public const int Slots = SaveSlots.Count;
    public enum Result { None, Cancelled, Picked }

    /// <summary>Scripted runs wait for each picture, so that a run's screens come out the same every time.</summary>
    public static bool WaitForPictures { get; set; }

    const int Visible = 5;
    static Rectangle EntryRect(int row) => new(16, 32 + row * 84, 252, 80);
    static readonly Rectangle HeaderRect = new(280, 32, 344, 30), PictureRect = new(292, 74, 320, 240), InfoRect = new(280, 330, 344, 118);
    static readonly Rectangle UpArrow = new(130, 18, 24, 12), DownArrow = new(130, 452, 24, 12);

    readonly bool _saving;
    readonly Ui _ui;
    readonly GraphicsDevice _device;
    readonly Database _db;
    readonly SaveSlots.Info?[] _slots;
    readonly string _title, _slotPrefix, _pictures;
    readonly bool _wideDigits;
    int _index, _top;
    Camp.MenuList? _confirm;

    public SaveMenu(bool saving, Ui ui, GraphicsDevice device, GameFolder folder, Database db, SaveSlots slots)
    {
        (_saving, _ui, _device, _db) = (saving, ui, device, db);
        _title = W(saving ? 53 : 54);   // セーブします / ロードします
        // the game's own name for a file (WORD.DAT 81, 「ファイル１」), the number written the same way for every file
        string first = W(81);
        _wideDigits = first.Length > 0 && first[^1] is >= '０' and <= '９';
        _slotPrefix = first.TrimEnd("0123456789０１２３４５６７８９".ToCharArray());
        if (_slotPrefix.Length == 0) _slotPrefix = T("File ", "ファイル");
        _slots = slots.Current();
        _pictures = SaveExtras.Folder(folder);
        DateTime newest = DateTime.MinValue;
        for (int i = 0; i < Slots; i++)
            if (_slots[i] is { } info && info.Saved > newest) (newest, _index) = (info.Saved, i);   // it opens on the file written last
        Follow();
        UpdatePicture();
    }

    string W(int i) => i >= 0 && i < _db.Words.Length ? _db.Words[i] : "";


    /// <summary>File <paramref name="n"/>'s name, as the game names its first ones (「ファイル１」…).</summary>
    public string SlotName(int n) => _slotPrefix + (_wideDigits ? new string(n.ToString().Select(c => (char)('０' + (c - '0'))).ToArray()) : n.ToString());

    /// <summary>The scroll keeps the chosen file in sight.</summary>
    void Follow()
    {
        if (_index < _top) _top = _index;
        if (_index >= _top + Visible) _top = _index - Visible + 1;
        _top = Math.Clamp(_top, 0, Slots - Visible);
    }

    int Hover(Point mouse)
    {
        for (int row = 0; row < Visible; row++)
            if (EntryRect(row).Contains(mouse)) return _top + row;
        return -1;
    }

    /// <summary>Saving over a file or loading one: the question, a rule, はい / いいえ (Yes / No in English).</summary>
    void Ask(string question)
    {
        int w = Math.Max(150, _ui.Measure12(question) + 36);
        _confirm = new Camp.MenuList { Rect = new Rectangle(320 - w / 2, 196, w, 80), Header = new[] { question, "-" }, ItemX = w / 2 - 18, ItemWidth = 36 };
        _confirm.Set(YesNo(_db));
        Sfx.Click();
    }

    /// <summary>One frame of input: the file picked (after its question), the screen left, or neither yet.</summary>
    public (Result Result, int Slot) Update(BattleInput i, Point mouse)
    {
        UpdatePicture();
        if (_confirm is { } c)
        {
            if (i.Cancel || i.RightClick) { _confirm = null; Sfx.Cancel(); return (Result.None, 0); }
            int answer = c.Navigate(i, mouse);
            if (answer < 0) return (Result.None, 0);
            _confirm = null;
            if (answer != 0) return (Result.None, 0);
            Sfx.SaveLoad();   // now, with はい's own click (none twice), not when the load comes after the fade
            return (Result.Picked, _index + 1);
        }
        if (i.Cancel || i.RightClick) { Sfx.Cancel(); return (Result.Cancelled, 0); }
        // the wheel scrolls the list a file a notch, not the choice: the file that comes under the pointer is the one
        // chosen, as if the pointer had moved onto it (none under it: the choice stays, perhaps out of sight)
        if (i.Wheel != 0)
        {
            _top = Math.Clamp(_top - i.Wheel, 0, Slots - Visible);
            if (Hover(mouse) is var under and >= 0) _index = under;
            return (Result.None, 0);
        }
        int move = (i.Down ? 1 : 0) - (i.Up ? 1 : 0) + (i.Right ? Visible : 0) - (i.Left ? Visible : 0);
        if (i.Click && UpArrow.Contains(mouse)) move -= Visible;
        if (i.Click && DownArrow.Contains(mouse)) move += Visible;
        if (move != 0)
        {
            _index = ((_index + move) % Slots + Slots) % Slots;
            Follow();
            return (Result.None, 0);
        }
        int hover = Hover(mouse);
        if (i.MouseMoved && hover >= 0) _index = hover;
        if (!((i.Confirm && !i.Click) || (i.Click && hover >= 0))) return (Result.None, 0);
        if (hover >= 0) _index = hover;
        string name = SlotName(_index + 1);
        if (_saving)
        {
            if (_slots[_index] is null) { Sfx.Click(); Sfx.SaveLoad(); return (Result.Picked, _index + 1); }
            Ask(T($"Save over {name}?", $"{name}に上書きしますか？"));
        }
        else if (_slots[_index] is null) Sfx.Buzzer();
        else Ask(T($"Load {name}?", $"{name}をロードしますか？"));
        return (Result.None, 0);
    }

    // ---------------- pictures ----------------
    // A file's PNG is read and decoded on another thread (5–25 ms, more from a cold disk), one at a time: the chosen
    // file's first, then those in sight, then a page either way. The chosen one is then drawn at the screen's scale;
    // until it's ready its frame stays empty.

    readonly Dictionary<int, Png.Image?> _read = new();   // pictures read, by slot from 0 (null: not one Png reads)
    readonly HashSet<int> _broken = new();   // pictures that couldn't be read at all
    (int Slot, Task<Png.Image?> Task)? _reading;
    Texture2D? _source;            // the chosen file's picture as read
    RenderTarget2D? _picture;      // and at the screen's scale
    SpriteBatch? _batch;
    int _pictureSlot = -1, _pictureScale;   // what _picture shows

    bool HasPicture(int slot) => slot is >= 0 and < Slots && _slots[slot] is { Picture: true } && !_broken.Contains(slot);

    Png.Image? ReadPicture(int slot)
    {
        try { return Png.Decode(SaveFile.ReadShared(Path.Combine(_pictures, $"SAVE{slot + 1}.png"))); }
        catch (Exception) { return null; }
    }

    /// <summary>The order the pictures are wanted in: the chosen file's, those in sight, then a page either way.</summary>
    IEnumerable<int> Wanted()
    {
        yield return _index;
        for (int slot = _top; slot < _top + Visible; slot++) yield return slot;
        for (int d = 1; d <= Visible; d++)
        {
            yield return _top + Visible - 1 + d;
            yield return _top - d;
        }
    }

    /// <summary>Takes the picture just read, lets go of those now out of reach, and starts on the next one wanted.</summary>
    void ReadPictures()
    {
        if (_reading is { } r && r.Task.IsCompleted)
        {
            _read[r.Slot] = r.Task.Result;
            _reading = null;
        }
        foreach (int slot in _read.Keys.Where(s => s != _index && (s < _top - Visible || s >= _top + 2 * Visible)).ToList())
            _read.Remove(slot);
        if (_reading is not null) return;
        foreach (int slot in Wanted())
            if (HasPicture(slot) && !_read.ContainsKey(slot))
            {
                _reading = (slot, Task.Run(() => ReadPicture(slot)));
                return;
            }
    }

    /// <summary>The chosen file's picture, drawn at the render scale once it's read (outside drawing, where render targets can change).</summary>
    void UpdatePicture()
    {
        int k = Math.Max(1, Ui.RenderScale);
        ReadPictures();
        if (_pictureSlot == _index && _pictureScale == k || !HasPicture(_index)) return;
        if (!_read.TryGetValue(_index, out var image))
        {
            if (!WaitForPictures) return;   // next frame
            _read[_index] = image = ReadPicture(_index);
        }
        try
        {
            if (_pictureSlot != _index)
            {
                _pictureSlot = -1;
                if (image is not null)
                {
                    if (_source is null || _source.Width != image.Width || _source.Height != image.Height)
                    {
                        _source?.Dispose();
                        _source = new Texture2D(_device, image.Width, image.Height);
                    }
                    _source.SetData(image.Rgba);
                }
                else
                {
                    // a PNG Png doesn't read (none of its own): MonoGame's reader, here
                    using var file = File.OpenRead(Path.Combine(_pictures, $"SAVE{_index + 1}.png"));
                    _source?.Dispose();
                    _source = Texture2D.FromStream(_device, file);
                }
            }
            if (_picture is null || _picture.Width != PictureRect.Width * k)
            {
                _picture?.Dispose();
                _picture = new RenderTarget2D(_device, PictureRect.Width * k, PictureRect.Height * k);
            }
            var before = _device.GetRenderTargets();
            _device.SetRenderTarget(_picture);
            _device.Clear(Color.Black);
            _batch ??= new SpriteBatch(_device);
            _batch.Begin(samplerState: SamplerState.LinearClamp);
            _batch.Draw(_source!, new Rectangle(0, 0, _picture.Width, _picture.Height), Color.White);
            _batch.End();
            _device.SetRenderTargets(before);
            (_pictureSlot, _pictureScale) = (_index, k);
        }
        catch (Exception) { _broken.Add(_index); }   // an unreadable picture: none
    }

    // ---------------- drawing ----------------

    static Color Dim => Ui.Restyled ? new Color(150, 158, 178) : new Color(208, 224, 200);
    static Color Bright => Ui.Gilded ? Ui.GoldLight : Ui.Modern ? new Color(170, 204, 255) : new Color(248, 248, 168);

    void Small(SpriteBatch sb, string s, int x, int y, Color c) => _ui.Text12(sb, s, x, y, c);
    void SmallRight(SpriteBatch sb, string s, int right, int y, Color c) => _ui.Text12(sb, s, right - _ui.Measure12(s), y, c);

    void Big(SpriteBatch sb, string s, int x, int y, Color c)
    {
        if (s.Length == 0) return;
        if (Ui.Restyled) _ui.ShadowText(sb, _ui.NameText, s, new Vector2(x, y), c);
        else _ui.NameText.Get(s)?.Draw(sb, new Vector2(x, y), c);
    }

    string Where(SaveSlots.Info info) => info.Camp ? T("Camp", "キャンプ")
        : info.Scene ? T($"Turn {info.Turn} (event)", $"ターン {info.Turn}（イベント中）") : T($"Turn {info.Turn}", $"ターン {info.Turn}");

    public void Draw(SpriteBatch sb, int frame)
    {
        _ui.Fill(sb, new Rectangle(0, 0, 640, 480), Color.Black * 0.62f);
        Small(sb, _title, 20, 10, Bright);
        for (int row = 0; row < Visible; row++) DrawEntry(sb, _top + row, EntryRect(row), _top + row == _index && _confirm is null, frame);
        if (_top > 0) Arrow(sb, UpArrow, -1);
        if (_top + Visible < Slots) Arrow(sb, DownArrow, 1);
        SmallRight(sb, $"{_index + 1} / {Slots}", 268, 10, Dim);
        DrawDetails(sb);
        _confirm?.Draw(sb, _ui, true, frame);
    }

    void DrawEntry(SpriteBatch sb, int slot, Rectangle r, bool chosen, int frame)
    {
        _ui.Panel(sb, r, chosen ? Ui.PanelColor.Blue : Ui.PanelColor.Green);
        // three rows in the larger type: the file, its turn (or キャンプ), its play time; the map's number and the date small
        var bar = new Rectangle(r.X + 6, r.Y + 5, r.Width - 12, 22);
        var top = chosen ? _ui.MenuRow(sb, bar, true, true, frame) : Ui.PanelText;
        // the name and the map's number both in the middle of the bar's height
        string name = SlotName(slot + 1);
        if (_ui.NameText.Get(name) is { } nt) Big(sb, name, r.X + 12, bar.Y + (int)MathF.Round((bar.Height - nt.LayoutHeight) / 2), top);
        if (_slots[slot] is not { } info)
        {
            Small(sb, T("―― Empty ――", "―― 空き ――"), r.X + 12, r.Y + 42, Dim);   // ― is in the game's own (Shift-JIS) font
            return;
        }
        SmallRight(sb, $"{T("Map", "マップ")} {info.Map}", r.Right - 12, bar.Y + (bar.Height - 16) / 2, chosen ? top : Dim);
        Big(sb, Where(info), r.X + 12, r.Y + 32, Bright);
        Big(sb, PlayTime(info.Play), r.X + 12, r.Y + 55, Ui.PanelText);
        SmallRight(sb, info.Saved.ToString("yyyy/MM/dd HH:mm"), r.Right - 12, r.Y + 59, Dim);
    }

    void DrawDetails(SpriteBatch sb)
    {
        var info = _slots[_index];
        _ui.Panel(sb, HeaderRect, Ui.PanelColor.Blue);
        string head = SlotName(_index + 1);
        if (_ui.NameText.Get(head) is { } ht)
            Big(sb, head, HeaderRect.X + (HeaderRect.Width - ht.Width) / 2, HeaderRect.Y + (int)((HeaderRect.Height - ht.LayoutHeight) / 2), Bright);

        var frame = new Rectangle(PictureRect.X - 6, PictureRect.Y - 6, PictureRect.Width + 12, PictureRect.Height + 12);
        _ui.Panel(sb, frame);
        if (_picture is not null && _pictureSlot == _index) sb.Draw(_picture, PictureRect, Color.White);
        else
        {
            _ui.Fill(sb, PictureRect, Color.Black * 0.45f);
            string none = info is null ? T("Empty", "空き") : T("No picture", "画像なし");
            if (!HasPicture(_index))   // one still being read: the frame alone
                Small(sb, none, PictureRect.X + (PictureRect.Width - _ui.Measure12(none)) / 2, PictureRect.Y + PictureRect.Height / 2 - 8, Dim);
        }

        _ui.Panel(sb, InfoRect);
        int y = InfoRect.Y + 10;
        void Line(string label, string value)
        {
            Small(sb, label, InfoRect.X + 14, y, Dim);
            Small(sb, value, InfoRect.X + 118, y, Ui.PanelText);
            y += 20;
        }
        Line(T("File", "ファイル"), SlotName(_index + 1));
        if (info is null) { Line(T("Status", "状況"), T("Empty", "空き")); return; }
        Line(T("Map", "マップ"), $"{info.Map}");
        Line(T("Status", "状況"), Where(info));
        Line(T("Play time", "プレイ時間"), PlayTime(info.Play));
        Line(T("Saved", "保存日時"), info.Saved.ToString("yyyy/MM/dd HH:mm"));
    }

    /// <summary>A small arrowhead showing that the list goes on that way (−1 up, +1 down).</summary>
    void Arrow(SpriteBatch sb, Rectangle r, int dir)
    {
        if (Ui.Gilded) { _ui.Arrow(sb, r.Center.X, r.Center.Y, 0, dir); return; }
        var c = Ui.Modern ? Ui.Accent : Ui.PanelText;
        for (int t = 0; t < 5; t++)
        {
            int len = 9 - 2 * t, y = dir < 0 ? r.Bottom - 3 - t : r.Y + 2 + t;   // the wide row away from the point
            _ui.Fill(sb, new Rectangle(r.Center.X - len / 2, y, len, 1), c);
        }
    }

    public void Dispose()
    {
        _picture?.Dispose();
        _source?.Dispose();
        _batch?.Dispose();
    }
}
