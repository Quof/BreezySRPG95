using Srpg95.Data;

namespace BreezySRPG95;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Options? opts = null;
        // A published BreezySRPG95.exe carries SDL2 and OpenAL inside it, unpacked by the runtime to its cache folder on the
        // first start; MonoGame only looks next to the exe. Loading them first, by the runtime's rules (which know that
        // folder), makes them the ones MonoGame gets. In a development build they're next to the exe anyway.
        foreach (var lib in new[] { "SDL2.dll", "soft_oal.dll" })
            System.Runtime.InteropServices.NativeLibrary.TryLoad(lib, typeof(Program).Assembly, null, out _);
        try
        {
            opts = Options.Parse(args);
            var game = new GameFolder(opts.GameDir, opts.RtpDir);
            using var g = new BreezyGame(game, opts);
            g.Run();
            return 0;
        }
        catch (NoGameException)
        {
            ErrorBox.Show("No SRPG Maker 95 game found.\n\n"
                + "Put BreezySRPG95 in the game's folder, next to SRPGEXEC.EXE, and start it from there.\n\n"
                + "シミュレーションRPGツクール95のゲームが見つかりません。\n"
                + "BreezySRPG95 をゲームのフォルダ（SRPGEXEC.EXE と同じ場所）に置いて起動してください。");
            return 1;
        }
        catch (Exception e)
        {
            // a scripted run: the error next to its screenshot, no box; else error.log beside the program (or in the
            // settings folder where that can't be written), and a box saying where — WinExe has no console
            if (opts?.Screenshot is { } shot) { File.WriteAllText(Path.ChangeExtension(shot, ".error.txt"), e.ToString()); return 1; }
            string? log = WriteLog(e.ToString());
            ErrorBox.Show("BreezySRPG95 ran into a problem and has to close.\n\n"
                + (log is not null ? $"Details were saved to error.log:\n{log}\n\nPlease send it with your report." : e.Message) + "\n\n"
                + "問題が発生したため終了します。" + (log is not null ? "詳細は error.log に保存されました。" : ""));
            return 1;
        }
    }

    /// <summary>error.log beside the program, else in the settings folder; where it went, or null.</summary>
    static string? WriteLog(string text)
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, Config.Settings.UserFolder })
            try
            {
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "error.log");
                File.WriteAllText(path, text);
                return path;
            }
            catch (Exception) { /* not writable: the next place */ }
        return null;
    }
}

/// <summary>No game folder: none given, and none beside the program or in a folder above it.</summary>
public sealed class NoGameException() : Exception("No SRPG Maker 95 game found (no folder with SRPGEXEC.EXE). Pass --game <folder>.");

/// <summary>
/// A message box for when the game can't start or has to stop: SDL's own (SDL_ShowSimpleMessageBox, which needs no
/// window and works on every system the program runs on), from the SDL2 library the program carries.
/// </summary>
static unsafe class ErrorBox
{
    const uint SdlMessageBoxError = 0x10;

    public static void Show(string message)
    {
        try
        {
            IntPtr lib = IntPtr.Zero;
            foreach (var name in new[] { "SDL2.dll", "libSDL2-2.0.so.0", "libSDL2.dylib" })
                if (System.Runtime.InteropServices.NativeLibrary.TryLoad(name, typeof(Program).Assembly, null, out lib)) break;
            if (lib == IntPtr.Zero || !System.Runtime.InteropServices.NativeLibrary.TryGetExport(lib, "SDL_ShowSimpleMessageBox", out var fn)) return;
            var show = (delegate* unmanaged[Cdecl]<uint, byte*, byte*, IntPtr, int>)fn;
            fixed (byte* title = System.Text.Encoding.UTF8.GetBytes("BreezySRPG95\0"))
            fixed (byte* text = System.Text.Encoding.UTF8.GetBytes(message + "\0"))
                show(SdlMessageBoxError, title, text, IntPtr.Zero);
        }
        catch (Exception) { /* no box: error.log stands */ }
    }
}

/// <summary>
/// Command line: --game DIR  --rtp DIR  --map N (1-based, like SMAP_nnn)
///               --screenshot FILE.png  (render one frame at 640x480, save it, exit)
///               --scale N              (with --screenshot: render and save the frame at N × 640x480, as a window N times
///                                       the size draws it)
///               --fast 1               (with --screenshot: run frames as fast as the machine can instead of at 60 a
///                                       second; the game advances by frames, so the outcome is the same)
///               --frames N             (with --screenshot: the shot at frame N at the latest; default 30000)
///               --advance N            (with --screenshot: auto-confirm N messages/choices first)
///               --event NAME           (run this script on load instead of OPENING, e.g. EV000202)
///               --advance -1           (confirm every message/choice)
///               --level N              (developer: allies start the map at level N or higher)
///               --give "ID:N,.."      (developer: add N of ITEM.DAT item ID (0-based) to the inventory)
///               --place "UNIT:X.Y,.."  (developer: move a unit (UNIT.DAT id) to a tile and keep it shown; :X.Y.HP also sets HP)
///               --saves DIR            (where SAVEn.DAT files are read and written; default: the game folder)
///               --soundfont FILE       (a SoundFont (.sf2, or .dls like gm.dls) anywhere, chosen for this run and listed in the options)
///               --load N               (load SAVEn.DAT from the game folder at start)
///               --camp N               (developer: open the camp before map N, 1-based)
///               --choose "a,b,.."     (with --screenshot: the option each character choice takes in turn, 0-based or
///                                       "last"; once used up, the first option; "random" picks every one from then
///                                       on at random, seeded)
///               --keys "a,b,..."       (with --screenshot: scripted input once the map is idle;
///                                       up/down/left/right/enter/esc, at:X,Y moves the cursor to a tile,
///                                       wait skips a beat, shot+N takes the shot N frames later even mid-animation;
///                                       telop+N / telopK+N wait for the next caption (any, or TELOPK.BMP) and shoot N frames in;
///                                       state:Name+N shoots N frames after the battle map enters state Name (e.g. Acting);
///                                       choice+N / choiceK+N shoot N frames after the next (the K-th) character choice;
///                                       endturn ends the player's turn (ターン終了);
///                                       otherwise the shot is taken once the queue is done and the map is idle)
/// </summary>
public sealed record Options(string GameDir, string? RtpDir, int? StartMap, string? Screenshot, int Advance, string? Event, string? Keys = null, int? Level = null, string? Give = null, string? Place = null, int? LoadSlot = null, int? CampBefore = null, string? SaveDir = null, string? Choose = null, int Scale = 1, bool Fast = false, int Frames = 30000, string? SoundFont = null)
{
    public static Options Parse(string[] args)
    {
        string? game = null, rtp = null, shot = null, ev = null, keys = null, give = null, place = null, saves = null, choose = null, soundFont = null;
        int? level = null, loadSlot = null, campBefore = null;
        int? map = null;
        int advance = 0, scale = 1, frames = 30000;
        bool fast = false;
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--game": game = args[i + 1]; break;
                case "--rtp": rtp = args[i + 1]; break;
                case "--map": map = int.Parse(args[i + 1]) - 1; break;
                case "--screenshot": shot = args[i + 1]; break;
                case "--advance": advance = int.Parse(args[i + 1]); break;
                case "--event": ev = args[i + 1]; break;
                case "--keys": keys = args[i + 1]; break;
                case "--level": level = int.Parse(args[i + 1]); break;
                case "--give": give = args[i + 1]; break;
                case "--place": place = args[i + 1]; break;
                case "--load": loadSlot = int.Parse(args[i + 1]); break;
                case "--saves": saves = args[i + 1]; break;
                case "--camp": campBefore = int.Parse(args[i + 1]) - 1; break;
                case "--choose": choose = args[i + 1]; break;
                case "--scale": scale = int.Parse(args[i + 1]); break;
                case "--fast": fast = args[i + 1] != "0"; break;
                case "--frames": frames = int.Parse(args[i + 1]); break;
                case "--soundfont": soundFont = Path.GetFullPath(args[i + 1]); break;
            }
        }
        game ??= FindGameFolder() ?? throw new NoGameException();
        if (!Directory.Exists(game)) throw new NoGameException();
        rtp ??= FindUp(AppContext.BaseDirectory, Path.Combine("srpg maker 95", "RTP"));
        return new Options(game, rtp, map, shot, advance, ev, keys, level, give, place, loadSlot, campBefore, saves, choose, scale, fast, frames, soundFont);
    }

    /// <summary>
    /// Next to the exe, or in a parent directory (development layout: ./えるまに). SRPGEXEC.EXE in any case off Windows; a
    /// folder that can't be listed (Linux: a parent the user may pass through but not read) is passed over.
    /// </summary>
    static string? FindGameFolder()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(FileNames.In(d.FullName, "SRPGEXEC.EXE"))) return d.FullName;
            DirectoryInfo[] subs;
            try { subs = d.GetDirectories(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var sub in subs)
                if (File.Exists(FileNames.In(sub.FullName, "SRPGEXEC.EXE")) && !sub.Name.Contains("Copy") && !sub.Name.Contains("Play"))
                    return sub.FullName;
        }
        return null;
    }

    static string? FindUp(string start, string relative)
    {
        for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, relative))) return Path.Combine(d.FullName, relative);
        return null;
    }
}
