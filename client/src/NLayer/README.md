# NLayer

The MP3 decoder: NLayer 3.0.0 by Mark Heath, Andrew Ward and contributors, a fully managed MPEG 1 and 2 audio decoder
(<https://github.com/naudio/NLayer>, MIT License: [LICENSE](LICENSE)). These are the files of its `NLayer` folder at
tag v3.0.0 (commit 046c7ce422970f8f0f0bc205d6c6341bcb7debf1), with its own project file in place of the original's
(which builds the NuGet package: two frameworks, strong naming, SourceLink).

It's here as source, not as the NuGet package, so that it can be fixed. The fixes, marked `BreezySRPG95:` in the code:

- `Decoder/BitReservoir.cs`: a frame whose data begins in earlier frames was dropped when those frames' data had ended
  exactly at the end of the reservoir's 8192-byte buffer: the buffer's end index wraps to -1 there, which NLayer also
  used to mean "empty". Now "empty" is a flag of its own. (In Ruby2, 26 ms skipped 21 s into どぶ_wars3.mp3, and twice
  in フル_last-warriors.mp3.)
- `Decoder/LayerIIIDecoder.cs`: intensity stereo, rewritten. NLayer looked for the right channel's last value below
  sample 543 only, took every intensity position from 7 up as "no intensity" in MPEG-2 too, and took MPEG-2's intensity
  scale from the left channel. In MPEG-2 (ISO/IEC 13818-3), a band's "no intensity" position is the largest value its
  scalefactor's length allows (2^slen - 1), and the scale comes from the right channel's scalefac_compress.

Checked against Windows' own MP3 decoder (its ACM codec; Media Foundation's gives the same samples): Ruby2's 14 MP3s
(MPEG 1 stereo and joint stereo at 44.1 kHz, and one MPEG-2 joint-stereo file with intensity stereo at 22.05 kHz)
decode to the same number of samples, within one frame at the end, and to within 76-86 dB of Windows' 16-bit output, which is
that output's own rounding. As released, NLayer dropped 1-2 frames in two of them and decoded the MPEG-2 one to
29-33 dB (1.16.0 crashed on it). `Mp3Tests` covers the reservoir.
