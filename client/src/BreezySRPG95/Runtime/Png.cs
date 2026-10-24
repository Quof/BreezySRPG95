using System.Buffers.Binary;
using System.IO.Compression;

namespace BreezySRPG95.Runtime;

/// <summary>
/// The saves' pictures as PNG files, made and read on any thread: MonoGame's Texture2D.SaveAsPng and FromStream need the
/// game's, and its encoder took about 130 ms for a 640 × 480 picture. Writes 8-bit RGB, each row with the filter whose
/// bytes sum smallest (the usual rule), compressed by System.IO.Compression's zlib. Reads 8-bit RGB and RGBA without
/// interlacing — what this and SaveAsPng write; anything else is left to Texture2D.FromStream.
/// </summary>
public static class Png
{
    /// <summary>A picture: its size and its pixels, 4 bytes each (R, G, B, A), row by row from the top.</summary>
    public sealed record Image(int Width, int Height, byte[] Rgba);

    static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static byte[] Encode(Image image)
    {
        int w = image.Width, h = image.Height, stride = w * 3;
        var raw = new byte[h * (stride + 1)];
        byte[] row = new byte[stride], prev = new byte[stride], best = new byte[stride], trial = new byte[stride];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0, s = y * w * 4; x < w; x++, s += 4)
                (row[x * 3], row[x * 3 + 1], row[x * 3 + 2]) = (image.Rgba[s], image.Rgba[s + 1], image.Rgba[s + 2]);
            int bestType = 0;
            long bestSum = long.MaxValue;
            for (int type = 0; type < 5; type++)
            {
                long sum = 0;
                for (int i = 0; i < stride; i++)
                {
                    int a = i >= 3 ? row[i - 3] : 0, b = prev[i], c = i >= 3 ? prev[i - 3] : 0;
                    byte f = (byte)(row[i] - Predict(type, a, b, c));
                    trial[i] = f;
                    sum += Math.Abs((int)(sbyte)f);   // as a signed byte (−128 too, which sbyte's Abs can't take)
                }
                if (sum < bestSum) (bestSum, bestType, best, trial) = (sum, type, trial, best);
            }
            int o = y * (stride + 1);
            raw[o] = (byte)bestType;
            best.CopyTo(raw, o + 1);
            (prev, row) = (row, prev);
        }
        var idat = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);

        var png = new MemoryStream();
        png.Write(Signature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        (ihdr[8], ihdr[9]) = (8, 2);   // 8 bits, RGB; compression, filter and interlace methods 0
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", idat.GetBuffer().AsSpan(0, (int)idat.Length));
        Chunk(png, "IEND", ReadOnlySpan<byte>.Empty);
        return png.ToArray();
    }

    /// <summary>The picture in a PNG file, or null for one that isn't 8-bit RGB or RGBA without interlacing (or is damaged).</summary>
    public static Image? Decode(byte[] png)
    {
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(Signature)) return null;
        int w = 0, h = 0, bpp = 0;
        var idat = new MemoryStream();
        for (int pos = 8; pos + 12 <= png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            if (length < 0 || length > png.Length - pos - 12) return null;
            var type = png.AsSpan(pos + 4, 4);
            var data = png.AsSpan(pos + 8, length);
            if (type.SequenceEqual("IHDR"u8))
            {
                if (length < 13) return null;
                (w, h) = (BinaryPrimitives.ReadInt32BigEndian(data), BinaryPrimitives.ReadInt32BigEndian(data[4..]));
                bpp = data[9] switch { 2 => 3, 6 => 4, _ => 0 };
                if (data[8] != 8 || bpp == 0 || data[10] != 0 || data[11] != 0 || data[12] != 0) return null;
            }
            else if (type.SequenceEqual("IDAT"u8)) idat.Write(data);
            else if (type.SequenceEqual("IEND"u8)) break;
            pos += 12 + length;
        }
        if (bpp == 0 || w <= 0 || h <= 0 || w > 16384 || h > 16384) return null;
        int stride = w * bpp;
        var raw = new byte[h * (stride + 1)];
        try
        {
            idat.Position = 0;
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            z.ReadExactly(raw);
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException) { return null; }

        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int o = y * (stride + 1), type = raw[o];
            if (type > 4) return null;
            var line = raw.AsSpan(o + 1, stride);
            var above = y > 0 ? raw.AsSpan(o - stride, stride) : default;
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? line[i - bpp] : 0, b = y > 0 ? above[i] : 0, c = i >= bpp && y > 0 ? above[i - bpp] : 0;
                line[i] = (byte)(line[i] + Predict(type, a, b, c));
            }
            for (int x = 0, d = y * w * 4; x < w; x++, d += 4)
            {
                (rgba[d], rgba[d + 1], rgba[d + 2]) = (line[x * bpp], line[x * bpp + 1], line[x * bpp + 2]);
                rgba[d + 3] = bpp == 4 ? line[x * bpp + 3] : (byte)255;
            }
        }
        return new Image(w, h, rgba);
    }

    /// <summary>A filter's prediction of a byte from the one to its left (a), above (b) and above-left (c).</summary>
    static int Predict(int type, int a, int b, int c) => type switch
    {
        1 => a,
        2 => b,
        3 => (a + b) >> 1,
        4 => Paeth(a, b, c),
        _ => 0,
    };

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(head, data.Length);
        for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        s.Write(head);
        s.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFF, head[4..]), data) ^ 0xFFFFFFFF;
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(tail, crc);
        s.Write(tail);
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// <summary>The CRC-32 a chunk ends with, carried on over <paramref name="data"/>.</summary>
    internal static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
