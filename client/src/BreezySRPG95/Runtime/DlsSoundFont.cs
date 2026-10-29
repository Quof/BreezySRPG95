using System.Buffers.Binary;
using System.Text;

namespace BreezySRPG95.Runtime;

/// <summary>
/// A DLS Level 1 collection (Windows' gm.dls, the instruments of the Microsoft GS Wavetable synth that the games were
/// made with) turned into a SoundFont 2 in memory, for MeltySynth, which only reads SoundFonts. Each DLS instrument
/// becomes a preset (drum kits in bank 128, GS variations in their bank), each region an instrument zone, each wave
/// a sample. The articulation maps onto SoundFont generators: the volume envelope (EG1), the modulation envelope (EG2)
/// and its pitch depth, the LFO (as both the modulation and the vibrato LFO, so the mod wheel's vibrato runs at its
/// rate) and its pitch and volume depths, pan, key-scaled decay; velocity-scaled attack has no generator and is left out.
/// </summary>
public static class DlsSoundFont
{
    sealed record Conn(int Source, int Control, int Destination, double Value);
    sealed record Sampling(int UnityNote, int FineTune, double Attenuation, int LoopStart, int LoopLength);
    sealed record Wave(short[] Data, int Rate, Sampling? Sampling);
    sealed record Region(int KeyLo, int KeyHi, int VelLo, int VelHi, int KeyGroup, int Cue, Sampling? Sampling, List<Conn>? Art);
    sealed record Instrument(string Name, int Bank, int Program, List<Region> Regions, List<Conn>? Art);

    /// <summary>Whether the file is a DLS collection (by its RIFF form, not its name).</summary>
    public static bool Is(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var head = new byte[12];
            return f.Read(head, 0, 12) == 12 && Encoding.ASCII.GetString(head, 0, 4) == "RIFF" && Encoding.ASCII.GetString(head, 8, 4) == "DLS ";
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>The DLS file as a SoundFont 2 file's bytes.</summary>
    public static MemoryStream ToSoundFont(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length < 12 || Ascii(b, 0) != "RIFF" || Ascii(b, 8) != "DLS ") throw new InvalidDataException("not a DLS file");
        var instruments = new List<Instrument>();
        var cues = Array.Empty<int>();
        var waves = new Dictionary<int, Wave?>();  // by offset in the wave pool
        foreach (var (id, at, size) in Chunks(b, 12, b.Length))
        {
            if (id == "ptbl")
            {
                int cb = I32(b, at), n = I32(b, at + 4);
                cues = Enumerable.Range(0, n).Select(i => I32(b, at + cb + 4 * i)).ToArray();
            }
            else if (id == "LIST" && Ascii(b, at) == "lins")
            {
                foreach (var (iid, iat, isz) in Chunks(b, at + 4, at + size))
                    if (iid == "LIST" && Ascii(b, iat) == "ins ") instruments.Add(ReadInstrument(b, iat + 4, iat + isz));
            }
            else if (id == "LIST" && Ascii(b, at) == "wvpl")
            {
                // the pool table's offsets count from the start of the pool's contents
                foreach (var (wid, wat, wsz) in Chunks(b, at + 4, at + size))
                    if (wid == "LIST" && Ascii(b, wat) == "wave") waves[wat - 8 - (at + 4)] = ReadWave(b, wat + 4, wat + wsz);
            }
        }
        return Write(instruments, cues.Select(c => waves.GetValueOrDefault(c)).ToList());
    }

    static Instrument ReadInstrument(byte[] b, int at, int end)
    {
        string name = "";
        int bank = 0, program = 0;
        var regions = new List<Region>();
        List<Conn>? art = null;
        foreach (var (id, a, s) in Chunks(b, at, end))
        {
            if (id == "insh")
            {
                uint locale = (uint)I32(b, a + 4);
                bank = (locale & 0x80000000) != 0 ? 128 : (int)(locale >> 8) & 0x7F;
                program = I32(b, a + 8) & 0x7F;
            }
            else if (id == "LIST" && Ascii(b, a) == "lrgn")
            {
                foreach (var (rid, ra, rs) in Chunks(b, a + 4, a + s))
                    if (rid == "LIST" && Ascii(b, ra) is "rgn " or "rgn2") regions.Add(ReadRegion(b, ra + 4, ra + rs));
            }
            else if (id == "LIST" && Ascii(b, a) == "lart") art = ReadArticulation(b, a + 4, a + s);
            else if (id == "LIST" && Ascii(b, a) == "INFO") name = Info(b, a + 4, a + s) ?? name;
        }
        return new(name, bank, program, regions, art);
    }

    static Region ReadRegion(byte[] b, int at, int end)
    {
        int keyLo = 0, keyHi = 127, velLo = 0, velHi = 127, group = 0, cue = -1;
        Sampling? sampling = null;
        List<Conn>? art = null;
        foreach (var (id, a, s) in Chunks(b, at, end))
        {
            if (id == "rgnh") (keyLo, keyHi, velLo, velHi, group) = (U16(b, a), U16(b, a + 2), U16(b, a + 4), U16(b, a + 6), U16(b, a + 10));
            else if (id == "wsmp") sampling = ReadSampling(b, a);
            else if (id == "wlnk") cue = I32(b, a + 8);
            else if (id == "LIST" && Ascii(b, a) == "lart") art = ReadArticulation(b, a + 4, a + s);
        }
        // DLS Level 1 has no velocity ranges (a file may leave them zero)
        if (velHi == 0) (velLo, velHi) = (0, 127);
        return new(Math.Min(keyLo, 127), Math.Min(keyHi, 127), Math.Min(velLo, 127), Math.Min(velHi, 127), group, cue, sampling, art);
    }

    static Sampling ReadSampling(byte[] b, int a)
    {
        int cb = I32(b, a), loops = I32(b, a + 16);
        int start = 0, length = 0;
        if (loops > 0) (start, length) = (I32(b, a + cb + 8), I32(b, a + cb + 12));
        return new(U16(b, a + 4), (short)U16(b, a + 6), I32(b, a + 8) / 65536.0, start, length);
    }

    static List<Conn> ReadArticulation(byte[] b, int at, int end)
    {
        var conns = new List<Conn>();
        foreach (var (id, a, _) in Chunks(b, at, end))
        {
            if (id != "art1") continue;
            int cb = I32(b, a), n = I32(b, a + 4);
            for (int i = 0; i < n; i++)
            {
                int c = a + cb + 12 * i;
                conns.Add(new(U16(b, c), U16(b, c + 2), U16(b, c + 4), I32(b, c + 8) / 65536.0));
            }
        }
        return conns;
    }

    static Wave? ReadWave(byte[] b, int at, int end)
    {
        int format = 0, channels = 0, rate = 0, bits = 0;
        Sampling? sampling = null;
        short[]? data = null;
        foreach (var (id, a, s) in Chunks(b, at, end))
        {
            if (id == "fmt ") (format, channels, rate, bits) = (U16(b, a), U16(b, a + 2), I32(b, a + 4), U16(b, a + 14));
            else if (id == "wsmp") sampling = ReadSampling(b, a);
            else if (id == "data") data = bits == 8
                ? Enumerable.Range(0, s).Select(i => (short)((b[a + i] - 128) << 8)).ToArray()
                : Enumerable.Range(0, s / 2).Select(i => (short)U16(b, a + 2 * i)).ToArray();
        }
        // DLS Level 1 waves are mono PCM; anything else is left silent
        return format == 1 && channels == 1 && bits is 8 or 16 && data is not null && rate > 0 ? new(data, rate, sampling) : null;
    }

    // ---------------- articulation → generators ----------------

    // SoundFont generator numbers
    const int StartLoop = 2, EndLoop = 3, ModLfoToPitch = 5, VibLfoToPitch = 6, ModEnvToPitch = 7, ModLfoToVolume = 13,
        Pan = 17, DelayModLfo = 21, FreqModLfo = 22, DelayVibLfo = 23, FreqVibLfo = 24, AttackModEnv = 26, DecayModEnv = 28,
        SustainModEnv = 29, ReleaseModEnv = 30, KeyToModDecay = 32, AttackVolEnv = 34, DecayVolEnv = 36, SustainVolEnv = 37,
        ReleaseVolEnv = 38, KeyToVolDecay = 40, InstrumentId = 41, KeyRange = 43, VelRange = 44, StartLoopCoarse = 45,
        InitialAttenuation = 48, EndLoopCoarse = 50, FineTune = 52, SampleId = 53, SampleModes = 54, ScaleTuning = 56,
        ExclusiveClass = 57, RootKey = 58;

    static Dictionary<int, double> Generators(List<Conn>? art)
    {
        var g = new Dictionary<int, double>();
        double volDecayByKey = 0, modDecayByKey = 0;
        foreach (var c in art ?? [])
        {
            double v = c.Value;
            switch (c.Source, c.Control, c.Destination)
            {
                case (0, 0, 0x104): g[FreqModLfo] = g[FreqVibLfo] = v; break;               // LFO frequency, absolute cents
                case (0, 0, 0x105): g[DelayModLfo] = g[DelayVibLfo] = v; break;             // LFO delay, timecents
                case (1, 0, 3): g[ModLfoToPitch] = v; break;                                // vibrato, cents
                case (1, 0, 1): g[ModLfoToVolume] = -v; break;                              // tremolo, centibels of gain
                case (5, 0, 3): g[ModEnvToPitch] = v; break;                                // EG2 → pitch, cents
                case (3, 0, 3): g[ScaleTuning] = v / 128; break;                            // cents over the 128 keys
                case (0, 0, 1): g[InitialAttenuation] = -v; break;
                case (0, 0, 4): g[Pan] = v; break;                                          // tenths of a percent both
                case (0, 0, 0x206): g[AttackVolEnv] = v; break;
                case (0, 0, 0x207): g[DecayVolEnv] = v; break;
                case (0, 0, 0x209): g[ReleaseVolEnv] = v; break;
                case (0, 0, 0x20A): g[SustainVolEnv] = (1000 - v) * 0.96; break;          // DLS: tenths of a percent of 96 dB
                case (0, 0, 0x30A): g[AttackModEnv] = v; break;
                case (0, 0, 0x30B): g[DecayModEnv] = v; break;
                case (0, 0, 0x30D): g[ReleaseModEnv] = v; break;
                case (0, 0, 0x30E): g[SustainModEnv] = 1000 - v; break;
                case (3, 0, 0x207): volDecayByKey = v; break;
                case (3, 0, 0x30B): modDecayByKey = v; break;
            }
        }
        // DLS scales decay by key / 128 over the whole range; SoundFont by (60 - key) per key around middle C
        void ByKey(int time, int perKey, double scale)
        {
            if (scale == 0) return;
            g[time] = g.GetValueOrDefault(time, -12000) + scale * 60 / 128;
            g[perKey] = -scale / 128;
        }
        ByKey(DecayVolEnv, KeyToVolDecay, volDecayByKey);
        ByKey(DecayModEnv, KeyToModDecay, modDecayByKey);
        return g;
    }

    static short Clamp(int gen, double v) => gen switch
    {
        AttackVolEnv or DecayVolEnv or ReleaseVolEnv or AttackModEnv or DecayModEnv or ReleaseModEnv => (short)Math.Clamp(Math.Round(v), -12000, 8000),
        DelayModLfo or DelayVibLfo => (short)Math.Clamp(Math.Round(v), -12000, 5000),
        FreqModLfo or FreqVibLfo => (short)Math.Clamp(Math.Round(v), -16000, 4500),
        SustainVolEnv => (short)Math.Clamp(Math.Round(v), 0, 1440),
        SustainModEnv => (short)Math.Clamp(Math.Round(v), 0, 1000),
        InitialAttenuation => (short)Math.Clamp(Math.Round(v), 0, 1440),
        Pan => (short)Math.Clamp(Math.Round(v), -500, 500),
        _ => (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue),
    };

    // ---------------- SoundFont 2 writer ----------------

    static MemoryStream Write(List<Instrument> instruments, List<Wave?> cues)
    {
        // samples: each followed by 46 zero points, as the format asks
        var smpl = new MemoryStream();
        var shdr = new MemoryStream();
        var sampleIndex = new Dictionary<Wave, int>(ReferenceEqualityComparer.Instance);
        var sampleStart = new Dictionary<Wave, (int Start, int LoopStart, int LoopEnd)>(ReferenceEqualityComparer.Instance);
        int points = 0;
        foreach (var w in cues)
        {
            if (w is null || sampleIndex.ContainsKey(w)) continue;
            int start = points, end = start + w.Data.Length;
            var (ls, le) = w.Sampling is { LoopLength: > 0 } s ? (start + s.LoopStart, start + s.LoopStart + s.LoopLength) : (start, end);
            foreach (var p in w.Data) { smpl.WriteByte((byte)p); smpl.WriteByte((byte)(p >> 8)); }
            smpl.Write(new byte[46 * 2]);
            points = end + 46;
            sampleIndex[w] = sampleIndex.Count;
            sampleStart[w] = (start, ls, le);
            Name(shdr, $"wave{sampleIndex[w]}");
            foreach (var u in new[] { start, end, ls, le, w.Rate }) U32(shdr, u);
            shdr.WriteByte((byte)Math.Min(w.Sampling?.UnityNote ?? 60, 127));
            shdr.WriteByte(0);
            U16(shdr, 0);
            U16(shdr, 1);  // mono
        }
        Name(shdr, "EOS");
        shdr.Write(new byte[26]);

        var inst = new MemoryStream();
        var ibag = new MemoryStream();
        var igen = new MemoryStream();
        var phdr = new MemoryStream();
        var pbag = new MemoryStream();
        var pgen = new MemoryStream();
        int ibags = 0, igens = 0, made = 0;
        foreach (var ins in instruments.OrderBy(i => i.Bank).ThenBy(i => i.Program))
        {
            Name(inst, ins.Name);
            U16(inst, ibags);
            foreach (var r in ins.Regions)
            {
                if (r.Cue < 0 || r.Cue >= cues.Count || cues[r.Cue] is not { } w) continue;
                U16(ibag, igens);
                U16(ibag, 0);
                ibags++;
                void Gen(int op, short amount) { U16(igen, op); U16(igen, (ushort)amount); igens++; }
                Gen(KeyRange, (short)(r.KeyLo | r.KeyHi << 8));
                Gen(VelRange, (short)(r.VelLo | r.VelHi << 8));
                // the region's articulation replaces the instrument's; the region's sampling (tuning, gain, loop) the wave's
                var gens = Generators(r.Art ?? ins.Art);
                var s = r.Sampling ?? w.Sampling;
                if (s is not null) gens[InitialAttenuation] = gens.GetValueOrDefault(InitialAttenuation) - s.Attenuation;
                // MeltySynth takes 40% of the attenuation (as Polyphone and the EMU cards did): the DLS figure in full
                if (gens.ContainsKey(InitialAttenuation)) gens[InitialAttenuation] *= 2.5;
                foreach (var (op, v) in gens) Gen(op, Clamp(op, v));
                if (s is not null)
                {
                    Gen(RootKey, (short)Math.Min(s.UnityNote, 127));
                    if (s.FineTune != 0) Gen(FineTune, (short)s.FineTune);
                    if (s.LoopLength > 0)
                    {
                        var (start, ls, le) = sampleStart[w];
                        int startOff = start + s.LoopStart - ls, endOff = start + s.LoopStart + s.LoopLength - le;
                        if (startOff % 32768 != 0) Gen(StartLoop, (short)(startOff % 32768));
                        if (startOff / 32768 != 0) Gen(StartLoopCoarse, (short)(startOff / 32768));
                        if (endOff % 32768 != 0) Gen(EndLoop, (short)(endOff % 32768));
                        if (endOff / 32768 != 0) Gen(EndLoopCoarse, (short)(endOff / 32768));
                        Gen(SampleModes, 1);
                    }
                }
                if (r.KeyGroup != 0) Gen(ExclusiveClass, (short)r.KeyGroup);
                Gen(SampleId, (short)sampleIndex[w]);
            }
            Name(phdr, ins.Name);
            U16(phdr, (ushort)ins.Program);
            U16(phdr, (ushort)ins.Bank);
            U16(phdr, (ushort)made);
            phdr.Write(new byte[12]);
            U16(pbag, (ushort)made);
            U16(pbag, 0);
            U16(pgen, InstrumentId);
            U16(pgen, (ushort)made);
            made++;
        }
        Name(inst, "EOI");
        U16(inst, ibags);
        U16(ibag, igens); U16(ibag, 0);
        igen.Write(new byte[4]);
        Name(phdr, "EOP");
        phdr.Write(new byte[4]);
        U16(phdr, (ushort)made);
        phdr.Write(new byte[12]);
        U16(pbag, (ushort)made); U16(pbag, 0);
        pgen.Write(new byte[4]);

        var info = List("INFO", Chunk("ifil", [2, 0, 1, 0]), Chunk("isng", "EMU8000\0"u8.ToArray()), Chunk("INAM", "gm.dls\0\0"u8.ToArray()));
        var sdta = List("sdta", Chunk("smpl", smpl.ToArray()));
        var pdta = List("pdta", Chunk("phdr", phdr.ToArray()), Chunk("pbag", pbag.ToArray()), Chunk("pmod", new byte[10]),
            Chunk("pgen", pgen.ToArray()), Chunk("inst", inst.ToArray()), Chunk("ibag", ibag.ToArray()), Chunk("imod", new byte[10]),
            Chunk("igen", igen.ToArray()), Chunk("shdr", shdr.ToArray()));
        var body = new MemoryStream();
        body.Write("sfbk"u8);
        body.Write(info); body.Write(sdta); body.Write(pdta);
        var file = new MemoryStream(Chunk("RIFF", body.ToArray()));
        return file;
    }

    // ---------------- RIFF ----------------

    static IEnumerable<(string Id, int At, int Size)> Chunks(byte[] b, int at, int end)
    {
        while (at + 8 <= end)
        {
            int size = I32(b, at + 4);
            if (size < 0 || at + 8 + size > end) yield break;
            yield return (Ascii(b, at), at + 8, size);
            at += 8 + size + (size & 1);
        }
    }

    static string? Info(byte[] b, int at, int end)
    {
        foreach (var (id, a, s) in Chunks(b, at, end))
            if (id == "INAM") return Encoding.Latin1.GetString(b, a, s).TrimEnd('\0').Trim();
        return null;
    }

    static byte[] Chunk(string id, byte[] data)
    {
        var m = new MemoryStream();
        m.Write(Encoding.ASCII.GetBytes(id));
        U32(m, data.Length);
        m.Write(data);
        if ((data.Length & 1) != 0) m.WriteByte(0);
        return m.ToArray();
    }

    static byte[] List(string type, params byte[][] chunks) => Chunk("LIST", [.. Encoding.ASCII.GetBytes(type), .. chunks.SelectMany(c => c)]);

    static void Name(Stream s, string name)
    {
        var n = new byte[20];
        Encoding.Latin1.GetBytes(name.Length > 19 ? name[..19] : name).CopyTo(n, 0);
        s.Write(n);
    }

    static string Ascii(byte[] b, int at) => Encoding.ASCII.GetString(b, at, 4);
    static int I32(byte[] b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));
    static int U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
    static void U16(Stream s, int v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); }
    static void U32(Stream s, int v) { U16(s, v); U16(s, v >> 16); }
}
