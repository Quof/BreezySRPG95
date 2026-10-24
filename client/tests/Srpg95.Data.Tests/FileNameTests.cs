namespace Srpg95.Data.Tests;

/// <summary>
/// File names on file systems that tell case or Unicode form apart (Linux, macOS): FileNames and GameFolder find a file
/// whatever the case and form of its name, and on Windows behave exactly as before. Windows' file systems ignore case, so
/// the search by hand is shown here through names that differ in Unicode form, which NTFS keeps apart.
/// </summary>
public class FileNameTests
{
    const string Composed = "が";           // が
    const string Decomposed = "が";   // か + combining ゛ (how macOS's older disks store が)

    static string TempDir(string tag) => Directory.CreateTempSubdirectory("breezysrpg95-" + tag).FullName;

    [Fact]
    public void On_Windows_a_path_is_used_exactly_as_given()
    {
        var dir = TempDir("names-win");
        try
        {
            File.WriteAllText(Path.Combine(dir, "Save" + Decomposed + ".dat"), "");
            Assert.Equal(Path.Combine(dir, "SAVE" + Composed + ".DAT"), FileNames.In(dir, "SAVE" + Composed + ".DAT", matchByHand: false));
            Assert.Equal(Path.Combine(dir, "nowhere", "SAVE1.DAT"), FileNames.In(Path.Combine(dir, "nowhere"), "SAVE1.DAT", matchByHand: false));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void By_hand_a_name_is_found_whatever_its_case_and_Unicode_form()
    {
        var dir = TempDir("names-hand");
        try
        {
            string stored = Path.Combine(dir, "Save" + Decomposed + ".dat");
            File.WriteAllText(stored, "");
            Directory.CreateDirectory(Path.Combine(dir, "Map"));
            Assert.Equal(stored, FileNames.In(dir, "SAVE" + Composed + ".DAT", matchByHand: true));
            Assert.Equal(Path.Combine(dir, "MAP"), FileNames.In(dir, "MAP", matchByHand: true));   // there as given: kept as given
            // nothing like it: the name as given, a file still to be made; no folder at all: the same, without an error
            Assert.Equal(Path.Combine(dir, "SAVE2.DAT"), FileNames.In(dir, "SAVE2.DAT", matchByHand: true));
            Assert.Equal(Path.Combine(dir, "nowhere", "SAVE1.DAT"), FileNames.In(Path.Combine(dir, "nowhere"), "SAVE1.DAT", matchByHand: true));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_name_is_compared_in_normal_form_C()
    {
        Assert.Equal(Composed, FileNames.Key(Decomposed));
        Assert.Equal("SAVE1.DAT", FileNames.Key("SAVE1.DAT"));
        Assert.Equal("\uD800x", FileNames.Key("\uD800x"));   // an unpaired surrogate can't be normalized: left as it is
    }

    [Fact]
    public void Off_Windows_a_game_file_is_found_whatever_its_Unicode_form()
    {
        var root = TempDir("names-game");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "bgm"));
            string stored = Path.Combine(root, "bgm", "Song" + Decomposed + ".MID");
            File.WriteAllText(stored, "");
            Assert.Equal(stored, new GameFolder(root, normalizeNames: true).Find("BGM/song" + Composed + ".mid"));
            // as on Windows until now: case ignored, the form compared as it is
            Assert.Null(new GameFolder(root, normalizeNames: false).Find("BGM/song" + Composed + ".mid"));
            Assert.Equal(stored, new GameFolder(root, normalizeNames: false).Find("BGM/song" + Decomposed + ".mid"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void The_map_list_lists_what_GetFiles_always_listed_on_Windows()
    {
        var dir = TempDir("names-maps");
        try
        {
            foreach (var name in new[] { "SMAP_001.DAT", "smap_002.dat", "SMAP_010.DAT", "SMAP_003.DAT.bak", "MAPC_001.DAT", "SMAP_004.TXT", "xSMAP_005.DAT" })
                File.WriteAllText(Path.Combine(dir, name), "");
            string hidden = Path.Combine(dir, "SMAP_006.DAT");
            File.WriteAllText(hidden, "");
            File.SetAttributes(hidden, FileAttributes.Hidden);   // GetFiles(dir, pattern) skips nothing
            Directory.CreateDirectory(Path.Combine(dir, "SMAP_007.DAT"));   // a folder isn't a file

            var listed = Directory.GetFiles(dir, "SMAP_*.DAT", FileNames.AnyCase).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            Assert.Equal(new[] { "SMAP_001.DAT", "smap_002.dat", "SMAP_006.DAT", "SMAP_010.DAT" }, listed);
            if (OperatingSystem.IsWindows())
                Assert.Equal(Directory.GetFiles(dir, "SMAP_*.DAT").Order().ToArray(), Directory.GetFiles(dir, "SMAP_*.DAT", FileNames.AnyCase).Order().ToArray());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
