using System.Buffers.Binary;
using System.IO.Compression;
using BreezySRPG95.Runtime;

namespace Srpg95.Data.Tests;

/// <summary>
/// The save / load screen's list and pictures off the game thread: SaveSlots (the files' details, kept between visits and
/// read again only when a file changed or the game saved to it), Png (the pictures made and read on any thread) and
/// SaveExtras.WriteLater (a save's note written in the background).
/// </summary>
public class SaveListTests
{
    static string TempDir(string tag) => Directory.CreateTempSubdirectory("breezysrpg95-" + tag).FullName;

    /// <summary>A SAVEn.DAT: at a camp or on a map (0-based), at a phase (ally turn t = 2t − 1), the rest <paramref name="fill"/>.</summary>
    static byte[] Save(bool camp, int map, int phase, byte fill = 0)
    {
        var b = new byte[SaveFile.Size];
        Array.Fill(b, fill, 0x1000, 0x100);
        BinaryPrimitives.WriteInt32LittleEndian(b, camp ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x004), map);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x20C), phase);
        return b;
    }

    // ---------------- Png ----------------

    // made by Pillow (an encoder of its own, its rows filtered Sub and Paeth), with their pixels as RGBA
    const string PillowRgb = "iVBORw0KGgoAAAANSUhEUgAAAAkAAAAHCAIAAABV+fA3AAAAkUlEQVR4nBWMQQrCMBAAZ5OgIlqtYmpFm6LWi6iwVxX//6h0JTCXYWBkjrRqS6VXjioztYv6hY6NutDzPsEZP2FsIOE25AEXsXDnm7AT7AuyLY0Eayw8+DTYGbdDaojkGn/EIhJevBe4BD0SYUp+4mtyhw8Dv5Z8wFWMN6jKwBLuQA4Dn66IXLFtedoK2yMt/AFNlR5Wu45nRgAAAABJRU5ErkJggg==";
    const string PillowRgbPixels = "CgAB/yMyAP8wZAD/UJYA/2zIAf90+gD/lywD/6NeAf+6kAP/KgA8/0cyPP9pZD//b5Y9/4bIPf+l+j//tyw8/9xePv/ykD3/VQB5/3QyeP+RZHj/qZZ4/8HIef/U+nv/5ix7/wVee/8VkHr/ggC1/5kytP+7ZLb/0Ja3/+HIt//3+rT/CCy3/yFetv83kLf/sQDw/70y8v/cZPL//Jbz/xLI8/8Z+vD/Nizz/0de8P9lkPP/1gAv/+8yLP8JZC7/F5Ys/zjILP9G+i7/Wywt/3peL/+UkCz/+wBr/xkyav8oZGv/TJZq/1/Iav91+mn/hCxo/5xeaf+1kGn/";
    const string PillowRgba = "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAGCAYAAAD+Bd/7AAAAc0lEQVR4nDWOywrCUAxEz8DdGKSiRfsSlVYEV0IWXd2P66dLJC6GTMLJJAK2l2OjY5a1d6xxbHKsrFRNoCPoDrqADtkvoPKhqg8DeoJG0AnUpcpMVZP0Nbf36dsAFuovOqAz6AZ6ZNrwB96gXQ7n/CUUZ7/dLwhMtQp9kAAAAABJRU5ErkJggg==";
    const string PillowRgbaPixels = "AAAAgCYyAIpAZACUSpYAnmTIAKh8+gCyiiwAvKVeAMY2ADyBUTI8i2JkPJWBljyfl8g8qab6PLO3LDy92l48x2UAeIJ9MniMgWR4lqWWeKC/yHiq0fp4tOgseL7/XnjIhwC0g5UytI24ZLSX1Ja0oePItKvv+rS1Cyy0vx5etMmqAPCEyTLwjtdk8JjslvCiCsjwrCr68LYvLPDASF7wys0ALIX2MiyP/2QsmSOWLKMsyCytS/ost2osLMFuXizL";

    [Fact]
    public void Png_reads_what_another_encoder_wrote()
    {
        var rgb = Png.Decode(Convert.FromBase64String(PillowRgb))!;
        Assert.Equal((9, 7), (rgb.Width, rgb.Height));
        Assert.Equal(Convert.FromBase64String(PillowRgbPixels), rgb.Rgba);
        var rgba = Png.Decode(Convert.FromBase64String(PillowRgba))!;
        Assert.Equal((8, 6), (rgba.Width, rgba.Height));
        Assert.Equal(Convert.FromBase64String(PillowRgbaPixels), rgba.Rgba);
    }

    [Fact]
    public void Png_reads_rows_under_each_of_the_five_filters()
    {
        const int w = 5, h = 10, bpp = 4, stride = w * bpp;
        var pixels = new byte[w * h * 4];
        new Random(3).NextBytes(pixels);
        // rows filtered None, Sub, Up, Average, Paeth, twice over, as the PNG specification defines them
        var raw = new byte[h * (stride + 1)];
        for (int y = 0; y < h; y++)
        {
            int type = y % 5;
            raw[y * (stride + 1)] = (byte)type;
            for (int i = 0; i < stride; i++)
            {
                int x = pixels[y * stride + i], a = i >= bpp ? pixels[y * stride + i - bpp] : 0, b = y > 0 ? pixels[(y - 1) * stride + i] : 0;
                int c = i >= bpp && y > 0 ? pixels[(y - 1) * stride + i - bpp] : 0;
                int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                int predicted = type switch { 1 => a, 2 => b, 3 => (a + b) / 2, 4 => pa <= pb && pa <= pc ? a : pb <= pc ? b : c, _ => 0 };
                raw[y * (stride + 1) + 1 + i] = (byte)(x - predicted);
            }
        }
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        (ihdr[8], ihdr[9]) = (8, 6);
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw);
        var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        foreach (var (type, data) in new[] { ("IHDR", ihdr), ("IDAT", z.ToArray()), ("IEND", Array.Empty<byte>()) })
        {
            var head = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(head, data.Length);
            System.Text.Encoding.ASCII.GetBytes(type, head.AsSpan(4));
            png.Write(head);
            png.Write(data);
            var crc = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Png.Crc(Png.Crc(0xFFFFFFFF, head.AsSpan(4)), data) ^ 0xFFFFFFFF);
            png.Write(crc);
        }
        Assert.Equal(pixels, Png.Decode(png.ToArray())!.Rgba);
    }

    [Fact]
    public void Png_writes_what_it_reads_back_opaque()
    {
        const int w = 37, h = 23;
        var rgba = new byte[w * h * 4];
        var rng = new Random(5);
        for (int i = 0; i < rgba.Length; i += 4)
        {
            int x = i / 4 % w, y = i / 4 / w;
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = ((byte)(x * 7), (byte)(y * 11 + rng.Next(4)), (byte)rng.Next(256), 255);
        }
        var png = Png.Encode(new Png.Image(w, h, rgba));
        Assert.Equal(2, png[25]);   // colour type 2: RGB, the alpha (always opaque here) left out
        var back = Png.Decode(png)!;
        Assert.Equal((w, h), (back.Width, back.Height));
        Assert.Equal(rgba, back.Rgba);
    }

    [Fact]
    public void Png_leaves_what_it_doesnt_read_to_MonoGame()
    {
        var png = Convert.FromBase64String(PillowRgb);
        var interlaced = (byte[])png.Clone();
        interlaced[28] = 1;   // IHDR's interlace method
        Assert.Null(Png.Decode(interlaced));
        var sixteenBit = (byte[])png.Clone();
        sixteenBit[24] = 16;  // bit depth
        Assert.Null(Png.Decode(sixteenBit));
        Assert.Null(Png.Decode(new byte[] { 1, 2, 3 }));
        Assert.Null(Png.Decode(png[..^40]));   // cut short
    }

    // ---------------- SaveSlots ----------------

    [Fact]
    public void The_list_shows_each_save_with_its_note_and_picture()
    {
        var root = TempDir("slots-list");
        try
        {
            var g = new GameFolder(root);
            File.WriteAllBytes(Path.Combine(root, "SAVE1.DAT"), Save(camp: true, map: 4, phase: 0));
            File.WriteAllBytes(Path.Combine(root, "save12.dat"), Save(camp: false, map: 9, phase: 5));   // turn 3
            File.WriteAllBytes(Path.Combine(root, "SAVE01.DAT"), Save(true, 1, 0));    // not a slot's name
            File.WriteAllBytes(Path.Combine(root, "SAVE101.DAT"), Save(true, 1, 0));   // past the last slot
            File.WriteAllBytes(Path.Combine(root, "SAVE7.DAT"), new byte[100]);        // not a save
            SaveExtras.Write(g, 12, Save(false, 9, 5), 3725.5, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

            var slots = new SaveSlots(g).Current();
            Assert.Equal(new[] { 0, 11 }, Enumerable.Range(0, SaveSlots.Count).Where(i => slots[i] is not null));
            Assert.Equal((5, true, 0, (double?)null, false), (slots[0]!.Map, slots[0]!.Camp, slots[0]!.Turn, slots[0]!.Play, slots[0]!.Picture));
            Assert.Equal((10, false, 3, (double?)3725.5, true), (slots[11]!.Map, slots[11]!.Camp, slots[11]!.Turn, slots[11]!.Play, slots[11]!.Picture));
            Assert.Equal(File.GetLastWriteTime(Path.Combine(root, "save12.dat")), slots[11]!.Saved);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_file_is_read_again_when_it_changes_or_the_game_saves_to_it()
    {
        var root = TempDir("slots-change");
        try
        {
            var g = new GameFolder(root);
            string path = Path.Combine(root, "SAVE3.DAT");
            File.WriteAllBytes(path, Save(false, 0, 1));
            var list = new SaveSlots(g);
            list.RefreshLater();   // as at the game's start
            Assert.Equal(1, list.Current()[2]!.Map);

            // written by another program: a new time
            File.WriteAllBytes(path, Save(false, 1, 1));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            Assert.Equal(2, list.Current()[2]!.Map);

            // the same size and time (a FAT32 drive keeps the time to 2 s): as it was read, until the game says it saved there
            var time = File.GetLastWriteTimeUtc(path);
            File.WriteAllBytes(path, Save(false, 2, 1));
            File.SetLastWriteTimeUtc(path, time);
            Assert.Equal(2, list.Current()[2]!.Map);
            list.Changed(3);
            Assert.Equal(3, list.Current()[2]!.Map);

            File.Delete(path);
            Assert.Null(list.Current()[2]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_note_written_in_the_background_is_there_for_whatever_reads_next()
    {
        var root = TempDir("slots-later");
        try
        {
            var g = new GameFolder(root);
            var save = Save(true, 2, 0);
            File.WriteAllBytes(Path.Combine(root, "SAVE5.DAT"), save);
            var list = new SaveSlots(g);
            var picture = new Png.Image(4, 3, Enumerable.Range(0, 48).Select(i => i % 4 == 3 ? (byte)255 : (byte)(i * 5)).ToArray());
            using var done = new ManualResetEventSlim();
            list.Changed(5);
            SaveExtras.WriteLater(g, 5, save, 99, picture, then: done.Set);
            var slot = list.Current()[4]!;   // waits for the note
            Assert.Equal((99.0, true), (slot.Play!.Value, slot.Picture));
            Assert.Equal(picture.Rgba, Png.Decode(File.ReadAllBytes(SaveExtras.PicturePath(g, 5)))!.Rgba);
            Assert.True(done.Wait(5000));
            Assert.Empty(Directory.GetFiles(SaveExtras.Folder(g), "*.tmp"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void An_index_that_cant_be_read_is_never_written_over()
    {
        var root = TempDir("notes-locked");
        try
        {
            var g = new GameFolder(root);
            SaveExtras.Write(g, 1, Save(true, 0, 0), 10, null);
            string before = File.ReadAllText(SaveExtras.IndexPath(g));
            // locked (an antivirus, a sync program): the new note is given up, the others kept
            using (new FileStream(SaveExtras.IndexPath(g), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Throws<IOException>(() => SaveExtras.Write(g, 2, Save(true, 1, 0), 20, null));
            Assert.Equal(before, File.ReadAllText(SaveExtras.IndexPath(g)));
            // no longer an index: kept aside, and a new one begun
            File.WriteAllText(SaveExtras.IndexPath(g), "not an index");
            SaveExtras.Write(g, 3, Save(true, 2, 0), 30, null);
            Assert.Equal("not an index", File.ReadAllText(SaveExtras.IndexPath(g) + ".damaged"));
            Assert.Equal(30.0, Assert.Single(SaveExtras.Read(g)).Value.PlaySeconds);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
