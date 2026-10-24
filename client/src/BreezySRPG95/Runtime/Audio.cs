using System.Collections.Concurrent;
using MeltySynth;
using Microsoft.Xna.Framework.Audio;
using NLayer;
using Srpg95.Data;

namespace BreezySRPG95.Runtime;

/// <summary>
/// Sound effects from the original WAVs (all PCM mono 8/16-bit) and looping BGM. MIDI BGM plays either through
/// a SoundFont synthesizer (MeltySynth, the modern option; consistent on every PC) or the Windows MCI sequencer
/// (the Microsoft GS Wavetable synth, what the original runtime used). WAV BGM (some games loop a sound effect as their
/// music) loops seamlessly as a sound of its own; one that isn't plain PCM goes through MCI. A WAV track a game ships as
/// an MP3 for its music setup to make (Ruby2's MusicSetUp) plays from the MP3 (<see cref="SetUpMp3"/>).
/// System sounds the runtime plays itself, from the game folder: FUN_00409428's CLICK.WAV (0, on confirm and when
/// advancing text), CANCEL.WAV (1), BUZZER.WAV (2, an unavailable choice) and DOWN.WAV (3, a unit falling), and
/// LEVELUP.WAV when the level-up panel opens (FUN_00410a88) or a class changes (FUN_0040be00).
/// </summary>
public sealed class Audio(GameFolder game) : IDisposable
{
    readonly Dictionary<string, SoundEffect?> _cache = new(StringComparer.OrdinalIgnoreCase);
    public string? CurrentBgm { get; private set; }
    public float SoundVolume { get; set; } = 0.8f;
    public bool Muted { get; set; }
    /// <summary>The CLICK "thunk" (confirming in menus, on the map, advancing text): the one sound the player can turn off.</summary>
    public bool MessageSound { get; set; } = true;
    /// <summary>The thunk when a message is clicked away (the "gameplay only" confirm sound leaves it out).</summary>
    public bool AdvanceSound { get; set; } = true;
    bool _bgmOpen;
    int _pollFrames;

    float _musicVolume = 0.8f;
    public float MusicVolume
    {
        get => _musicVolume;
        set
        {
            _musicVolume = value;
            if (_stream is not null) _stream.Volume = Math.Clamp(value, 0, 1);
            if (_wavLoop is not null) _wavLoop.Volume = Math.Clamp(value, 0, 1);
            if (_mp3Stream is not null) _mp3Stream.Volume = Math.Clamp(value, 0, 1);
            if (_bgmOpen) Mci($"setaudio breezy_bgm volume to {(int)(value * 1000)}");
        }
    }

    // ---------------- SoundFont synth ----------------

    const int Rate = 44100, Block = 2048;
    Synthesizer? _synth;
    MidiFileSequencer? _sequencer;
    DynamicSoundEffectInstance? _stream;
    string? _soundFont;
    readonly float[] _left = new float[Block], _right = new float[Block];
    readonly byte[] _pcm = new byte[Block * 4];

    /// <summary>
    /// Use this SoundFont for MIDI: a .sf2 path, <see cref="Config.Settings.BuiltIn"/> for the one embedded in the
    /// executable, or null for Windows MIDI. Restarts the current track with it. Off Windows, which has no MCI, null and
    /// an unreadable file mean the built-in one.
    /// </summary>
    public void SetSoundFont(string? path)
    {
        if (!HasMci) path ??= Config.Settings.BuiltIn;
        if (string.Equals(path, _soundFont, StringComparison.OrdinalIgnoreCase)) return;
        _soundFont = path;
        StopSynth();
        _synth = null;
        _sequencer = null;
        if (path is not null)
        {
            try
            {
                if (path == Config.Settings.BuiltIn)
                {
                    using var s = typeof(Audio).Assembly.GetManifestResourceStream("GeneralUser-GS.sf2")
                        ?? throw new FileNotFoundException("built-in SoundFont missing");
                    _synth = new Synthesizer(new SoundFont(s), Rate);
                }
                else _synth = new Synthesizer(path, Rate);
                _sequencer = new MidiFileSequencer(_synth);
            }
            catch (Exception) { _synth = null; _sequencer = null; _soundFont = null; }   // unreadable SoundFont: Windows MIDI
            if (_synth is null && !HasMci && path != Config.Settings.BuiltIn) { SetSoundFont(Config.Settings.BuiltIn); return; }
        }
        var current = CurrentBgm;
        CurrentBgm = null;
        StopBgm();
        if (current is not null) PlayBgm(current);
    }

    void StopSynth()
    {
        _sequencer?.Stop();
        _stream?.Stop();
        _stream?.Dispose();
        _stream = null;
    }

    void FeedSynth()
    {
        if (_stream is null || _sequencer is null) return;
        while (_stream.PendingBufferCount < 3)
        {
            _sequencer.Render(_left, _right);
            for (int i = 0; i < Block; i++)
            {
                short l = (short)Math.Clamp(_left[i] * 32767f, short.MinValue, short.MaxValue);
                short r = (short)Math.Clamp(_right[i] * 32767f, short.MinValue, short.MaxValue);
                _pcm[i * 4] = (byte)l; _pcm[i * 4 + 1] = (byte)(l >> 8);
                _pcm[i * 4 + 2] = (byte)r; _pcm[i * 4 + 3] = (byte)(r >> 8);
            }
            _stream.SubmitBuffer(_pcm);
        }
    }

    // ---------------- MCI (Windows) ----------------

    /// <summary>Whether Windows' MCI (winmm.dll) is there to play MIDI and WAV music: on Windows only.</summary>
    static readonly bool HasMci = OperatingSystem.IsWindows();

    [System.Runtime.InteropServices.DllImport("winmm.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int mciSendStringW(string command, System.Text.StringBuilder? ret, int retLen, IntPtr callback);

    static string Mci(string cmd)
    {
        var sb = new System.Text.StringBuilder(128);
        try { mciSendStringW(cmd, sb, sb.Capacity, IntPtr.Zero); } catch (DllNotFoundException) { }
        return sb.ToString();
    }

    // HarmonyGetMusicLooping: whether the current music has played to its end at least once (COMPLETE!! waits for it),
    // timed on the game's clock (each frame adds its length), so a scripted run at full speed waits as many frames as play
    double _bgmLengthMs, _bgmPlayedMs;
    public bool BgmLooped => CurrentBgm is not null && _bgmLengthMs > 0 && _bgmPlayedMs >= _bgmLengthMs;

    public void PlayBgm(string file)
    {
        string? next = file == "(OFF)" || file.Length == 0 ? null : file;
        if (string.Equals(next, CurrentBgm, StringComparison.OrdinalIgnoreCase) && (_bgmOpen || _stream is not null || _wavLoop is not null || _mp3Stream is not null)) return;
        StopBgm();
        CurrentBgm = next;
        BgmMp3 = null;
        _bgmLengthMs = 0;
        if (next is null) return;
        var path = BgmPath(game, next);
        if (path is null) return;
        bool mp3 = path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
        if (mp3) BgmMp3 = Path.GetFileName(path);
        // a MIDI file with long silence at its ends plays without most of it (MidiTrim)
        byte[]? midiBytes = null, fileBytes = null;
        try { fileBytes = Bytes(path, keep: true); } catch (Exception) { }
        if (fileBytes is not null && path.EndsWith(".mid", StringComparison.OrdinalIgnoreCase))
            try { midiBytes = MidiTrim.Trim(fileBytes); } catch (Exception) { }
        _bgmLengthMs = LengthMs(path, midiBytes ?? fileBytes);   // also while muted, so scripted runs keep the same timing
        _bgmPlayedMs = 0;
        if (Muted) return;
        bool midi = path.EndsWith(".mid", StringComparison.OrdinalIgnoreCase);
        if (midi && _sequencer is not null)
        {
            try
            {
                // with the loop marker option, a track that has an RPG Maker loop marker (controller 111) repeats from
                // there, as RPG Maker 2000/2003 plays it; otherwise (and as the original does) from its start
                var bytes = midiBytes ?? fileBytes ?? File.ReadAllBytes(path);
                var loopType = LoopAtMarkers && MidiTrim.LoopStart(bytes) is not null ? MidiFileLoopType.RpgMaker : MidiFileLoopType.None;
                _sequencer.Play(new MidiFile(new MemoryStream(bytes), loopType), loop: true);
                _stream = new DynamicSoundEffectInstance(Rate, AudioChannels.Stereo) { Volume = Math.Clamp(_musicVolume, 0, 1) };
                FeedSynth();
                _stream.Play();
                return;
            }
            catch (Exception) { StopSynth(); }   // fall back to MCI
        }
        if (mp3)
        {
            try { PlayMp3(fileBytes ?? File.ReadAllBytes(path)); } catch (Exception) { StopMp3(); }   // a damaged MP3 stays silent
            return;
        }
        if (!midi)
        {
            // MCI only notices the end when polled, which left a gap before each repeat: a looping sound has none
            try { _wavBgm = Load(fileBytes ?? File.ReadAllBytes(path)); } catch (Exception) { _wavBgm = null; }
            if (_wavBgm is not null)
            {
                _wavLoop = _wavBgm.CreateInstance();
                _wavLoop.IsLooped = true;
                _wavLoop.Volume = Math.Clamp(_musicVolume, 0, 1);
                _wavLoop.Play();
                return;
            }
        }
        if (!HasMci) return;   // off Windows: a track the synth or a looping sound can't play is silent
        if (midi && midiBytes is not null) path = TrimmedCopy(midiBytes) ?? path;
        Mci($"open \"{path}\" type {(midi ? "sequencer" : "waveaudio")} alias breezy_bgm");
        Mci($"setaudio breezy_bgm volume to {(int)(_musicVolume * 1000)}");
        Mci("play breezy_bgm from 0");
        _bgmOpen = true;
    }

    /// <summary>The trimmed MIDI file written for the Windows sequencer, which plays files only; null if it can't be.</summary>
    static string? TrimmedCopy(byte[] midi)
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "BreezySRPG95");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"bgm{Environment.ProcessId}.mid");
            File.WriteAllBytes(file, midi);
            return file;
        }
        catch (Exception) { return null; }
    }

    /// <summary>A BGM file's playing time (<paramref name="bytes"/>: the file, a MIDI one as trimmed): a MIDI file's last event, a WAV's data at its byte rate, an MP3's frames.</summary>
    static double LengthMs(string path, byte[]? bytes)
    {
        try
        {
            if (path.EndsWith(".mid", StringComparison.OrdinalIgnoreCase))
                return (bytes is null ? new MidiFile(path) : new MidiFile(new MemoryStream(bytes))).Length.TotalMilliseconds;
            var b = bytes ?? File.ReadAllBytes(path);
            if (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                return new MpegFile(new MemoryStream(b)).Duration.TotalMilliseconds;
            int pos = 12, byteRate = 0;
            while (pos + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
                int len = Bin.I32(b, pos + 4);
                if (id == "fmt ") byteRate = Bin.I32(b, pos + 16);
                else if (id == "data" && byteRate > 0) return len * 1000.0 / byteRate;
                pos += 8 + len + (len & 1);
            }
        }
        catch (Exception) { }   // an unreadable file just has no known length
        return 0;
    }

    SoundEffect? _wavBgm;
    SoundEffectInstance? _wavLoop;

    void StopBgm()
    {
        StopSynth();
        StopMp3();
        _wavLoop?.Stop();
        _wavLoop?.Dispose();
        _wavBgm?.Dispose();
        (_wavLoop, _wavBgm) = (null, null);
        if (!_bgmOpen) return;
        Mci("stop breezy_bgm");
        Mci("close breezy_bgm");
        _bgmOpen = false;
    }

    /// <summary>Call every frame: keeps the synth and the MP3 track fed and restarts MCI BGM when it reaches the end (looping).</summary>
    public void Update()
    {
        _bgmPlayedMs += GameClock.FrameMs;
        FeedSynth();
        FeedMp3();
        if (!_bgmOpen || ++_pollFrames < 30) return;
        _pollFrames = 0;
        if (Mci("status breezy_bgm mode") == "stopped") Mci("play breezy_bgm from 0");
    }

    // ---------------- MP3 (MusicSetUp) ----------------

    /// <summary>
    /// The MP3 a missing WAV track is made from, or null. Ruby2 ships its CD-quality music as MP3s in MP3_File, for 稲富祥哲's
    /// MusicSetUp (MP3_D.exe, set up by mp3_D2.ini) to decode once, before the game is played, into WAVs of the same names in
    /// BGM (Ruby2_1.mp3 becomes Ruby2_1.wav; 380 MB of them), which the events and maps ask for. Its first map plays one as
    /// a test and asks the player to quit and set the music up if nothing is heard. Here such a track plays from the MP3,
    /// decoded as it plays (NLayer, to the samples Windows' own MP3 decoder gives), and loops as the WAV would.
    /// </summary>
    internal static string? SetUpMp3(GameFolder game, string file) =>
        file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? game.FindOwn("MP3_File/" + Path.ChangeExtension(file, ".mp3")) : null;

    /// <summary>A track's file: the game's own, else the MP3 its music setup makes it from, else the RTP's; null if none.</summary>
    internal static string? BgmPath(GameFolder game, string file) =>
        game.FindOwn("BGM/" + file) ?? SetUpMp3(game, file) ?? game.Find("BGM/" + file);

    /// <summary>The MP3 file the current track plays from (<see cref="SetUpMp3"/>), or null.</summary>
    public string? BgmMp3 { get; private set; }

    MpegFile? _mp3;
    DynamicSoundEffectInstance? _mp3Stream;
    float[] _mp3Samples = [];
    byte[] _mp3Pcm = [];

    void PlayMp3(byte[] file)
    {
        _mp3 = new MpegFile(new MemoryStream(file));
        _mp3Samples = new float[Block * _mp3.Channels];
        _mp3Pcm = new byte[_mp3Samples.Length * 2];
        _mp3Stream = new DynamicSoundEffectInstance(_mp3.SampleRate, _mp3.Channels == 2 ? AudioChannels.Stereo : AudioChannels.Mono)
            { Volume = Math.Clamp(_musicVolume, 0, 1) };
        FeedMp3();
        _mp3Stream?.Play();
    }

    /// <summary>Keeps three blocks of the MP3 track queued.</summary>
    void FeedMp3()
    {
        if (_mp3Stream is null || _mp3 is null) return;
        try
        {
            while (_mp3Stream.PendingBufferCount < 3)
            {
                int bytes = NextBlock(_mp3, _mp3Samples, _mp3Pcm);
                if (bytes == 0) { StopMp3(); return; }
                _mp3Stream.SubmitBuffer(_mp3Pcm, 0, bytes);
            }
        }
        catch (Exception) { StopMp3(); }   // a damaged file: silent from there on
    }

    /// <summary>
    /// The MP3 track's next block (<paramref name="samples"/>' length) as 16-bit PCM in <paramref name="pcm"/>: its length
    /// in bytes, 0 for a file without sound. At its end the track goes on from its start with no gap, as a looping WAV.
    /// </summary>
    internal static int NextBlock(MpegFile mp3, float[] samples, byte[] pcm)
    {
        int n = 0;
        bool rewound = false;
        while (n < samples.Length)
        {
            int got = mp3.ReadSamples(samples, n, samples.Length - n);
            if (got > 0) { n += got; rewound = false; continue; }
            if (rewound) break;   // nothing from the start either
            mp3.Position = 0;
            rewound = true;
        }
        for (int i = 0; i < n; i++)
        {
            short s = (short)Math.Clamp(samples[i] * 32767f, short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)s;
            pcm[i * 2 + 1] = (byte)(s >> 8);
        }
        return n * 2;
    }

    void StopMp3()
    {
        _mp3Stream?.Stop();
        _mp3Stream?.Dispose();
        (_mp3Stream, _mp3) = (null, null);
    }

    // ---------------- sound effects ----------------

    /// <summary>
    /// The EFS, BGM and MP3_File folders' files read ahead (<see cref="Prepare"/>), by path: a sound's until it becomes
    /// one, the music's for good (a track comes back, and all of a game's is a few MB; Ruby2's MP3s, 40 MB).
    /// </summary>
    readonly ConcurrentDictionary<string, byte[]> _readAhead = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A file's bytes, from the read-ahead if it got there (kept there with <paramref name="keep"/>), else from the disk.</summary>
    byte[] Bytes(string path, bool keep)
    {
        if (keep ? _readAhead.TryGetValue(path, out var b) : _readAhead.TryRemove(path, out b)) return b;
        b = File.ReadAllBytes(path);
        if (keep) _readAhead.TryAdd(path, b);
        return b;
    }

    /// <summary>
    /// Gets the sounds ready before they're needed, as the original does: it starts its sound at start-up and reserves
    /// its four system sounds then (HarmonyInitWave, FUN_00409484), and caches a battle animation's sound before its
    /// scene (HarmonyCacheSound). Here the audio device and the system sounds now, and the rest of the EFS and BGM folders
    /// (the game's and the RTP's) and MP3_File (<see cref="SetUpMp3"/>) read into memory in the background — at most about
    /// 25 MB in the games here, 60 MB with Ruby2's MP3s — so no sound or track waits on the disk (a sleeping USB drive's can
    /// take seconds) when it first plays.
    /// </summary>
    public void Prepare()
    {
        // looked up here: GameFolder isn't thread-safe
        var folders = game.Folders("EFS").Concat(game.Folders("BGM")).Concat(game.Folders("MP3_File")).ToList();
        if (!Muted) foreach (var f in new[] { "CLICK.WAV", "CANCEL.WAV", "BUZZER.WAV", "DOWN.WAV", "LEVELUP.WAV" }) Sound(f);
        Task.Run(() =>
        {
            foreach (var dir in folders)
                foreach (var path in Directory.EnumerateFiles(dir))
                    try { _readAhead.TryAdd(path, File.ReadAllBytes(path)); } catch (Exception) { }   // read when it plays instead
        });
    }

    /// <summary>A sound effect from EFS, made once (null for a missing or unreadable file: it isn't heard).</summary>
    SoundEffect? Sound(string file)
    {
        if (_cache.TryGetValue(file, out var se)) return se;
        try
        {
            if (game.Find("EFS/" + file) is { } path)
                se = Load(Bytes(path, keep: false));
        }
        catch (Exception) { se = null; }   // a damaged or unusual WAV stays silent
        return _cache[file] = se;
    }

    public void PlaySe(string file)
    {
        if (Muted || file.Length == 0) return;
        Sound(file)?.Play(Math.Clamp(SoundVolume, 0, 1), 0, 0);
    }

    /// <summary>Option (modern): MIDI tracks with an RPG Maker loop marker repeat from it (the SoundFont synthesizer only).</summary>
    public bool LoopAtMarkers { get; set; }

    public void Click() { if (MessageSound) { PlaySe("CLICK.WAV"); _clickAt = Environment.TickCount64; } }

    long _clickAt = long.MinValue / 2;

    /// <summary>
    /// CLICK.WAV when a game is saved or loaded (modern: the original plays only the pick's click), whatever the confirm
    /// sound option says — once: not again when the pick's own click played within the last second (a load comes after
    /// the screen has faded out, a third of a second or more after はい).
    /// </summary>
    public void SaveLoadSound()
    {
        if (Environment.TickCount64 - _clickAt < 1000) return;
        PlaySe("CLICK.WAV");
        _clickAt = Environment.TickCount64;
    }
    public void CancelSound() => PlaySe("CANCEL.WAV");
    public void Buzzer() => PlaySe("BUZZER.WAV");
    /// <summary>The text-advance thunk (CLICK.WAV), unless turned off in the options.</summary>
    public void MessageAdvance() { if (AdvanceSound) Click(); }

    /// <summary>Minimal RIFF/WAVE PCM loader; 8-bit unsigned samples are widened to 16-bit.</summary>
    static SoundEffect? Load(byte[] b)
    {
        if (b.Length < 12 || b[0] != 'R' || b[8] != 'W') return null;
        int pos = 12, channels = 1, rate = 22050, bits = 16;
        byte[]? pcm = null;
        while (pos + 8 <= b.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
            int len = Math.Min(Bin.I32(b, pos + 4), b.Length - pos - 8);
            if (id == "fmt ")
            {
                if (Bin.U16(b, pos + 8) != 1) return null;      // PCM only
                channels = Bin.U16(b, pos + 10);
                rate = Bin.I32(b, pos + 12);
                bits = Bin.U16(b, pos + 22);
            }
            else if (id == "data") pcm = b.AsSpan(pos + 8, len).ToArray();
            pos += 8 + len + (len & 1);
        }
        if (pcm is null) return null;
        if (bits == 8)
        {
            var w = new byte[pcm.Length * 2];
            for (int i = 0; i < pcm.Length; i++)
            {
                short s = (short)((pcm[i] - 128) << 8);
                w[i * 2] = (byte)s;
                w[i * 2 + 1] = (byte)(s >> 8);
            }
            pcm = w;
        }
        int frame = 2 * channels;
        return new SoundEffect(pcm, 0, pcm.Length - pcm.Length % frame, rate,
            channels == 2 ? AudioChannels.Stereo : AudioChannels.Mono, 0, 0);
    }

    public void Dispose()
    {
        StopBgm();
        foreach (var s in _cache.Values) s?.Dispose();
    }
}
