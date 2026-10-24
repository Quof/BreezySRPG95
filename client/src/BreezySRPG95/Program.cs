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
        catch (Exception e)
        {
            // WinExe has no console: leave the error next to the executable (a scripted run: next to its screenshot)
            File.WriteAllText(opts?.Screenshot is { } shot ? Path.ChangeExtension(shot, ".error.txt") : Path.Combine(AppContext.BaseDirectory, "error.log"), e.ToString());
            return 1;
        }
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
public sealed record Options(string GameDir, string? RtpDir, int? StartMap, string? Screenshot, int Advance, string? Event, string? Keys = null, int? Level = null, string? Give = null, string? Place = null, int? LoadSlot = null, int? CampBefore = null, string? SaveDir = null, string? Choose = null, int Scale = 1, bool Fast = false, int Frames = 30000)
{
    public static Options Parse(string[] args)
    {
        string? game = null, rtp = null, shot = null, ev = null, keys = null, give = null, place = null, saves = null, choose = null;
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
            }
        }
        game ??= FindGameFolder() ?? throw new DirectoryNotFoundException(
            "Game folder not found. Pass --game <folder containing SRPGEXEC.EXE>.");
        rtp ??= FindUp(AppContext.BaseDirectory, Path.Combine("srpg maker 95", "RTP"));
        return new Options(game, rtp, map, shot, advance, ev, keys, level, give, place, loadSlot, campBefore, saves, choose, scale, fast, frames);
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
