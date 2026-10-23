using BreezySRPG95.Runtime;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace BreezySRPG95.Ui;

/// <summary>
/// Text backlog (modern): every message shown since the title, newest at the bottom, over the dimmed screen. Mouse wheel
/// up (or the backlog key) opens it and scrolls back a message at a time; wheel down scrolls forward and closes it past
/// the newest; Cancel or a click closes it. A spoken message shows the face it was spoken with (flipped as it was), half
/// size in a thin white frame, its text beside it (the speaker's 「名前：」 line left out: the face says who); narration has
/// no frame, its text in the same column. A message is as tall as its frame or its text, so a long one pushes the next
/// down.
/// </summary>
public sealed class Backlog(Ui ui)
{
    /// <summary>A message as it was shown: its lines, and its face (0 none) and whether that was flipped.</summary>
    readonly record struct Entry(string[] Lines, int Face, bool Flip);

    readonly List<Entry> _entries = new();
    int _scroll;   // messages scrolled up from the newest
    public bool Open { get; private set; }

    const int Top = 16, Bottom = 466;          // the list's room on the 640 × 480 screen
    const int FrameX = 22, FaceSize = 64;      // the face at half its 128 px: whole pixels at a render scale of 2 and up
    const int TextX = FrameX + FaceSize + 2 + 16, Gap = 16, TextTop = 2;

    public void Add(MessageView m)
    {
        _entries.Add(new Entry(m.Lines.Where(l => l.Length > 0).ToArray(), m.Face, m.FaceFlip));
        if (_entries.Count > 300) _entries.RemoveAt(0);
    }

    public void Clear() { _entries.Clear(); Open = false; }

    /// <summary>The lines shown: a spoken message's without its 「名前：」 line (its face stands for the name).</summary>
    IReadOnlyList<string> Text(Entry e) => e.Face > 0 && ui.Face(e.Face) is not null ? Ui.SplitSpeaker(e.Lines).Text : e.Lines;

    bool Framed(Entry e) => e.Face > 0 && ui.Face(e.Face) is not null;

    /// <summary>A message's height: its frame's or its text's, the taller.</summary>
    int Height(Entry e)
    {
        int text = Math.Max(1, Text(e).Count) * Ui.LineHeight;
        return Framed(e) ? Math.Max(FaceSize + 2, TextTop + text) : text;
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
        int y = Bottom, i = _entries.Count - 1 - _scroll;
        for (; i >= 0; i--)
        {
            var e = _entries[i];
            int h = Height(e);
            if (y - h < Top) break;
            y -= h;
            if (Framed(e) && ui.Face(e.Face) is { } face)
            {
                ui.Frame(sb, new Rectangle(FrameX, y, FaceSize + 2, FaceSize + 2), Color.White);
                sb.Draw(face, new Rectangle(FrameX + 1, y + 1, FaceSize, FaceSize), null, Color.White, 0, Vector2.Zero,
                    e.Flip ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0);
            }
            var text = Text(e);
            for (int k = 0; k < text.Count; k++)
                ui.ShadowText(sb, ui.Text, text[k], new Vector2(TextX, y + (Framed(e) ? TextTop : 0) + k * Ui.LineHeight), ink);
            y -= Gap;
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
