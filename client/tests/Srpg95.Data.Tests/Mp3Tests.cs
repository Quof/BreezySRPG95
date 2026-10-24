using NLayer;
using NLayer.Decoder;

namespace Srpg95.Data.Tests;

/// <summary>
/// The MP3 decoder (client/src/NLayer). Its decoding was checked against Windows' own MP3 decoder on Ruby2's 14 tracks,
/// which can't be here (see src/NLayer/README.md); here, the bit reservoir that made it drop frames.
/// </summary>
public class Mp3Tests
{
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
}
