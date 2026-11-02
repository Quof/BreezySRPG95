namespace BreezySRPG95.Ui;

/// <summary>
/// BreezySRPG95's own words (options, prompts, the save screen) in English or Japanese, by the Language setting. The
/// game's text — messages, its menus' words from WORD.DAT, map names — is the game's, unless it has a translation
/// (translation/en, Srpg95.Data.Translation) and the language is English.
/// </summary>
public static class Lang
{
    public static bool Japanese { get; set; }

    /// <summary>The game's own text is shown translated (English, and the game has a translation).</summary>
    public static bool GameTranslated { get; set; }

    /// <summary>The English or the Japanese of a phrase.</summary>
    public static string T(string en, string ja) => Japanese ? ja : en;

    /// <summary>Yes / no for BreezySRPG95's own questions: the game's はい / いいえ (WORD.DAT 45, 46) in Japanese.</summary>
    public static string[] YesNo(Srpg95.Data.Database db) =>
        Japanese && db.Words.Length > 46 ? new[] { db.Words[45], db.Words[46] } : new[] { "Yes", "No" };

    /// <summary>
    /// A line the original puts together from WORD.DAT's pieces (「ＨＰ」「が」「５」「ポイント」「回復した」): run together, as
    /// Japanese has them, or in a translated game with a space between pieces that don't bring their own (none before
    /// punctuation), so a translation's pieces can be single words. A blank piece adds nothing.
    /// </summary>
    public static string Phrase(params string[] parts)
    {
        if (!GameTranslated) return string.Concat(parts);
        var sb = new System.Text.StringBuilder();
        foreach (var p in parts)
        {
            if (p.Length == 0) continue;
            if (sb.Length > 0 && !char.IsWhiteSpace(sb[^1]) && sb[^1] != '(' && !char.IsWhiteSpace(p[0]) && ",.!?:;)".IndexOf(p[0]) < 0) sb.Append(' ');
            sb.Append(p);
        }
        return sb.ToString();
    }

    /// <summary>On / off as the option values show them.</summary>
    public static string OnOff(bool on) => on ? T("On", "ON") : T("Off", "OFF");

    /// <summary>Hours, minutes and seconds of play, "--:--:--" when they aren't known.</summary>
    public static string PlayTime(double? seconds) => seconds is not { } s ? "--:--:--"
        : $"{(int)(s / 3600):00}:{(int)(s / 60) % 60:00}:{(int)s % 60:00}";
}
