using System.Text;

namespace Srpg95.Data;

/// <summary>
/// File names on file systems that tell case apart (Linux; macOS can): the games and the original program name their
/// files in capitals (SAVE1.DAT, SRPGEXEC.EXE, MAP\SMAP_001.DAT), and the files on disk may differ in case — or, on
/// macOS, in Unicode form (が stored as か followed by a combining ゛). On Windows, whose file systems ignore case, a path
/// is used exactly as given and nothing is looked up by hand, as it always was.
/// </summary>
public static class FileNames
{
    /// <summary>Whether names have to be matched by hand: everywhere but Windows.</summary>
    public static readonly bool MatchByHand = !OperatingSystem.IsWindows();

    /// <summary>
    /// The options Directory.GetFiles(dir, pattern) uses (EnumerationOptions.Compatible: Win32 wildcards, nothing skipped,
    /// an inaccessible entry an error), with the pattern's case ignored on every system — as Windows does anyway.
    /// </summary>
    public static EnumerationOptions AnyCase => new()
    {
        MatchType = MatchType.Win32, AttributesToSkip = 0, IgnoreInaccessible = false, MatchCasing = MatchCasing.CaseInsensitive,
    };

    /// <summary>A name as lookups compare it: in Unicode normal form C (one that can't be normalized stays as it is).</summary>
    public static string Key(string name)
    {
        try { return name.IsNormalized(NormalizationForm.FormC) ? name : name.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return name; }   // an unpaired surrogate: compared as it is
    }

    /// <summary>
    /// <paramref name="name"/> (a file or folder) in <paramref name="dir"/>: as given where that exists (or on Windows),
    /// else the entry whose name matches it but for case and Unicode form, else as given (a file still to be made).
    /// </summary>
    public static string In(string dir, string name) => In(dir, name, MatchByHand);

    /// <summary><see cref="In(string, string)"/>, with whether to look by hand given (the tests look on Windows too).</summary>
    public static string In(string dir, string name, bool matchByHand)
    {
        string path = Path.Combine(dir, name);
        if (!matchByHand || File.Exists(path) || Directory.Exists(path)) return path;
        try
        {
            string key = Key(name);
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                if (string.Equals(Key(Path.GetFileName(entry)), key, StringComparison.OrdinalIgnoreCase)) return entry;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // no such folder, or not readable
        return path;
    }
}
