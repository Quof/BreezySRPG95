using BreezySRPG95.Runtime;
using NLayer;
using NLayer.Decoder;

namespace Srpg95.Data.Tests;

/// <summary>
/// MP3 music: the decoder (client/src/NLayer) and a game whose WAV tracks are made from MP3s by its music setup (Ruby2's
/// MusicSetUp; Audio.SetUpMp3). The decoding was checked against Windows' own MP3 decoder on Ruby2's 14 tracks, which
/// can't be here (see src/NLayer/README.md); here, the bit reservoir that made it drop frames, the lookup and the loop.
/// </summary>
public class Mp3Tests
{
    static string TempDir(string tag) => Directory.CreateTempSubdirectory("breezysrpg95-" + tag).FullName;

    static string? Folder(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, name))) return Path.Combine(d.FullName, name);
        return null;
    }

    /// <summary>A silent MP3: <paramref name="frames"/> MPEG 1 Layer III frames, 44.1 kHz mono at 128 kbps, all their side info 0.</summary>
    static byte[] SilentMp3(int frames)
    {
        var frame = new byte[417];   // 144 × 128000 / 44100
        (frame[0], frame[1], frame[2], frame[3]) = ((byte)0xFF, (byte)0xFB, (byte)0x90, (byte)0xC4);
        return Enumerable.Repeat(frame, frames).SelectMany(f => f).ToArray();
    }

    /// <summary>A Layer III frame for the bit reservoir: MPEG 1 stereo, no CRC, its 32 bytes of side info read, then <paramref name="slots"/> bytes of main data.</summary>
    sealed class MainDataFrame(int slots) : IMpegFrame
    {
        int _read;
        public int SampleRate => 44100;
        public int SampleRateIndex => 0;
        public int FrameLength => 4 + 32 + slots;
        public int BitRate => 128000;
        public MpegVersion Version => MpegVersion.Version1;
        public MpegLayer Layer => MpegLayer.LayerIII;
        public MpegChannelMode ChannelMode => MpegChannelMode.Stereo;
        public int ChannelModeExtension => 0;
        public int SampleCount => 1152;
        public int BitRateIndex => 9;
        public bool IsCopyrighted => false;
        public bool HasCrc => false;
        public bool IsCorrupted => false;
        public void Reset() => _read = 0;
        public int ReadBits(int bitCount) => _read++ < slots ? 0x5A : -1;
    }

    [Fact]
    public void A_frame_continuing_data_that_ended_at_the_reservoirs_last_byte_is_kept()
    {
        var reservoir = new BitReservoir();
        Assert.True(reservoir.AddBits(new MainDataFrame(512), 0));
        for (int i = 1; i < 16; i++)
            Assert.True(reservoir.AddBits(new MainDataFrame(512), 100));
        // 16 × 512 bytes: the 8192-byte buffer is full to its last byte, and the next frame begins 300 bytes back
        // (NLayer 3.0.0 dropped it, as if nothing were there)
        Assert.True(reservoir.AddBits(new MainDataFrame(512), 300));
        Assert.Equal((300 + 512) * 8, reservoir.BitsAvailable);
    }

    [Fact]
    public void At_the_start_and_after_a_reset_a_frame_needing_earlier_data_is_still_dropped()
    {
        var reservoir = new BitReservoir();
        Assert.False(reservoir.AddBits(new MainDataFrame(512), 100));
        Assert.True(reservoir.AddBits(new MainDataFrame(512), 100));
        reservoir.Reset();
        Assert.False(reservoir.AddBits(new MainDataFrame(512), 100));
        reservoir.Reset();
        Assert.True(reservoir.AddBits(new MainDataFrame(512), 0));
    }

    [Fact]
    public void A_WAV_track_missing_from_BGM_plays_from_the_MP3_it_is_made_from()
    {
        string root = TempDir("mp3-game"), rtp = TempDir("mp3-rtp");
        try
        {
            foreach (var dir in new[] { Path.Combine(root, "BGM"), Path.Combine(root, "MP3_File"), Path.Combine(rtp, "BGM") })
                Directory.CreateDirectory(dir);
            foreach (var file in new[] { "Ruby2_1.mp3", "Theme.mp3", "Field.mp3", "Intro.mp3" })
                File.WriteAllText(Path.Combine(root, "MP3_File", file), "");
            File.WriteAllText(Path.Combine(root, "BGM", "Theme.wav"), "");
            File.WriteAllText(Path.Combine(rtp, "BGM", "Field.wav"), "");
            File.WriteAllText(Path.Combine(rtp, "BGM", "Battle.wav"), "");
            var game = new GameFolder(root, rtp);

            Assert.Equal(Path.Combine(root, "MP3_File", "Ruby2_1.mp3"), Audio.BgmPath(game, "RUBY2_1.WAV"));
            Assert.Equal(Path.Combine(root, "BGM", "Theme.wav"), Audio.BgmPath(game, "Theme.wav"));         // set up already
            Assert.Equal(Path.Combine(root, "MP3_File", "Field.mp3"), Audio.BgmPath(game, "Field.wav"));    // set up, it'd come before the RTP's
            Assert.Equal(Path.Combine(rtp, "BGM", "Battle.wav"), Audio.BgmPath(game, "Battle.wav"));
            Assert.Null(Audio.BgmPath(game, "Intro.mid"));   // only WAV tracks are made from MP3s
            Assert.Null(Audio.BgmPath(game, "still.wav"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(rtp, recursive: true);
        }
    }

    [Fact]
    public void An_MP3_track_goes_on_from_its_start_with_no_gap()
    {
        var mp3 = new MpegFile(new MemoryStream(SilentMp3(10)));
        Assert.Equal(11520 / 44100.0, mp3.Duration.TotalSeconds, 6);
        var samples = new float[2048];
        var pcm = new byte[samples.Length * 2];
        for (int i = 0; i < 12; i++)   // 24576 samples of an 11520-sample track: round its end twice, every block full
            Assert.Equal(pcm.Length, Audio.NextBlock(mp3, samples, pcm));
        Assert.All(pcm, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Ruby2s_test_track_plays_and_loops_as_decoded()
    {
        if (Folder("Ruby2") is not { } ruby2) return;
        var bytes = File.ReadAllBytes(Path.Combine(ruby2, "MP3_File", "ひとおお_adm.mp3"));
        // the track decoded once, whole
        var whole = new MpegFile(new MemoryStream(bytes));
        var track = new List<float>();
        var buf = new float[8192];
        int n;
        while ((n = whole.ReadSamples(buf, 0, buf.Length)) > 0) track.AddRange(buf.AsSpan(0, n).ToArray());
        Assert.Equal(6045 * 1152 * 2, track.Count);   // 6045 frames of stereo, 157.9 s
        Assert.Equal(6045 * 1152 / 44100.0, whole.Duration.TotalSeconds, 6);

        // as it plays: 2048-sample blocks of 16-bit PCM, past its end and a second on
        var mp3 = new MpegFile(new MemoryStream(bytes));
        var samples = new float[2048 * 2];
        var pcm = new byte[samples.Length * 2];
        int at = 0, wrong = -1;
        while (at < track.Count + 44100 * 2 && wrong < 0)
        {
            Assert.Equal(pcm.Length, Audio.NextBlock(mp3, samples, pcm));
            for (int i = 0; i < samples.Length && wrong < 0; i++, at++)
                if (BitConverter.ToInt16(pcm, i * 2) != (short)Math.Clamp(track[at % track.Count] * 32767f, short.MinValue, short.MaxValue))
                    wrong = at;
        }
        Assert.Equal(-1, wrong);
    }
}
