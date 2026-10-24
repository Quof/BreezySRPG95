using BreezySRPG95.Runtime;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace BreezySRPG95.Ui;

/// <summary>
/// Text backlog (modern): every message shown since the title, newest at the bottom, over the dimmed screen. Mouse wheel
/// up (or the backlog key) opens it and scrolls back a message at a time; wheel down scrolls forward and closes it past
/// the newest; Up / Down (keys or pad) do the same; Cancel or a click closes it. A spoken message shows the face it was
/// spoken with (flipped as it was), half size in a thin white frame, its text beside it, the top of its first line's
/// letters level with the frame's top, no gap; narration has no frame, its text in the same column. The speaker's
/// 「名前：」 line is its first, the name alone in gold (left out with <see cref="HideNames"/>). A message is as tall as
/// its frame or its text, so a long one pushes the next down. The list starts where five framed messages stand centred on the screen, and runs down
/// from there: a longer message pushes the bottom down, towards the screen's edge.
/// </summary>
public sealed class Backlog(Ui ui)
{
    /// <summary>A message as it was shown: its lines, and its face (0 none) and whether that was flipped.</summary>
    readonly record struct Entry(string[] Lines, int Face, bool Flip);

    readonly List<Entry> _entries = new();
    int _scroll;   // messages scrolled up from the newest
    public bool Open { get; private set; }
    /// <summary>The "never show nametags in the backlog" option.</summary>
    public static bool HideNames { get; set; }

    const int FrameX = 22, FaceSize = 64;      // the face at half its 128 px: whole pixels at a render scale of 2 and up
    const int TextX = FrameX + FaceSize + 2 + 16, Gap = 16;
    // the list's room on the 640 × 480 screen: from where five framed messages are centred, down to near the bottom
    const int Top = (480 - (5 * (FaceSize + 2) + 4 * Gap)) / 2, Bottom = 466;

    public void Add(MessageView m)
    {
        _entries.Add(new Entry(m.Lines.Where(l => l.Length > 0).ToArray(), m.Face, m.FaceFlip));
        if (_entries.Count > 300) _entries.RemoveAt(0);
    }

    public void Clear() { _entries.Clear(); Open = false; }

    /// <summary>The speaker's name (null: none, or <see cref="HideNames"/>) and the lines under it.</summary>
    static (string? Name, IReadOnlyList<string> Text) Shown(Entry e)
    {
        var (name, text) = Ui.SplitSpeaker(e.Lines);
        return name is null || HideNames ? (null, text) : (name, text);
    }

    static int LineCount(Entry e) { var (name, text) = Shown(e); return text.Count + (name is null ? 0 : 1); }

    bool Framed(Entry e) => e.Face > 0 && ui.Face(e.Face) is not null;

    /// <summary>A message's height: its frame's or its text's, the taller.</summary>
    int Height(Entry e)
    {
        int text = Math.Max(1, LineCount(e)) * Ui.LineHeight;
        return Framed(e) ? Math.Max(FaceSize + 2, TextTop(e) + text) : text;
    }

    readonly Dictionary<string, float> _inks = new();
    string? _inkKey;

    /// <summary>
    /// Where a framed message's first line is drawn below the frame's top: so that the top of its letters, wherever the
    /// font puts them in its line, is level with the frame's top row. Read off the rendered line, once a line and font.
    /// </summary>
    int TextTop(Entry e)
    {
        var (name, text) = Shown(e);
        if ((name ?? text.FirstOrDefault()) is not { } line || ui.Text.Get(line) is not { } t) return 0;
        string key = $"{Ui.Skin}/{Ui.Font}/{ui.Text.FaceName}/{ui.Text.Height}/{t.Scale}/{t.Lift}";
        if (key != _inkKey) { _inks.Clear(); _inkKey = key; }
        if (!_inks.TryGetValue(line, out float ink))
        {
            var px = new Color[t.Texture.Width * t.Texture.Height];
            t.Texture.GetData(px);
            int first = Array.FindIndex(px, c => c.A > 64);
            _inks[line] = ink = first < 0 ? 0 : first / t.Texture.Width / (float)t.Scale - t.Lift;
        }
        return (int)MathF.Round(-ink);
    }

    /// <summary>How many of the newest messages can be scrolled past while older ones still fill the screen.</summary>
    int MaxScroll()
    {
        int used = 0, fit = 0;
        foreach (var e in _entries)
        {
            used += Height(e) + (fit > 0 ? Gap : 0);
            if (used > Bottom - Top) break;
            fit++;
        }
        return Math.Max(0, _entries.Count - fit);
    }

    /// <summary>Wheel notches: positive = up (back in time), a message each.</summary>
    public void Scroll(int notches)
    {
        if (_entries.Count == 0) return;
        if (!Open)
        {
            if (notches <= 0) return;
            Open = true;
            _scroll = 0;
            notches--;
        }
        int before = _scroll;
        _scroll = Math.Clamp(_scroll + notches, 0, MaxScroll());
        if (notches < 0 && before == 0) Open = false;
    }

    public void Toggle() { if (Open) Open = false; else Scroll(1); }

    public void Close() => Open = false;

    public void Draw(SpriteBatch sb)
    {
        if (!Open) return;
        ui.Fill(sb, new Rectangle(0, 0, 640, 480), Color.Black * 0.62f);
        var ink = new Color(242, 242, 242);
        // the newest messages that fit, oldest first from the top
        int first = _entries.Count - _scroll, used = 0;
        while (first > 0 && used + Height(_entries[first - 1]) + (used > 0 ? Gap : 0) <= Bottom - Top)
            used += Height(_entries[--first]) + (used > 0 ? Gap : 0);
        int y = Top, i = first - 1;
        for (int j = first; j < _entries.Count - _scroll; j++)
        {
            var e = _entries[j];
            int h = Height(e);
            if (Framed(e) && ui.Face(e.Face) is { } face)
            {
                ui.Frame(sb, new Rectangle(FrameX, y, FaceSize + 2, FaceSize + 2), Color.White);
                sb.Draw(face, new Rectangle(FrameX + 1, y + 1, FaceSize, FaceSize), null, Color.White, 0, Vector2.Zero,
                    e.Flip ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0);
            }
            var (name, text) = Shown(e);
            int ty = y + (Framed(e) ? TextTop(e) : 0);
            if (name is not null) { ui.ShadowText(sb, ui.Text, name, new Vector2(TextX, ty), Ui.GoldLight); ty += Ui.LineHeight; }
            for (int k = 0; k < text.Count; k++)
                ui.ShadowText(sb, ui.Text, text[k], new Vector2(TextX, ty + k * Ui.LineHeight), ink);
            y += h + Gap;
        }
        // small white marks where there is more: older messages above, newer below
        if (i >= 0) Triangle(sb, 320, 5, up: true);
        if (_scroll > 0) Triangle(sb, 320, 470, up: false);
    }

    void Triangle(SpriteBatch sb, int cx, int y, bool up)
    {
        for (int row = 0; row < 5; row++)
        {
            int half = up ? row : 4 - row;
            ui.Fill(sb, new Rectangle(cx - half, y + row, 2 * half + 1, 1), Color.White * 0.85f);
        }
    }
}
