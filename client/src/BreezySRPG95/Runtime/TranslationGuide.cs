namespace BreezySRPG95.Runtime;

/// <summary>
/// What Options → Dump Script for Translation writes beside the texts (Srpg95.Data.TextDump): the head of each text
/// file, translation/README.txt for people, translation/LLM PROMPT.txt to hand a language model with each file; and the
/// head of translation/en/PROBLEMS.txt (BreezyGame.LoadTranslation).
/// </summary>
public static class TranslationGuide
{
    public const string FileHeader = """
        # BreezySRPG95 translation file: {FILE}
        #
        # Translate only the lines under each ">>>" (up to the next "<<<"). Leave the "<<<" lines and the Japanese
        # between "<<<" and ">>>" exactly as they are. See ../README.txt for the rules, or ../LLM PROMPT.txt to have a
        # language model translate this file. Lines above the first "<<<", like these, are ignored.


        """;

    public const string ProblemsHeader = """
        # Written by BreezySRPG95 when it checked this translation against the game. One problem a line: the file, the
        # entry's key, what's wrong. Texts with problems are still used where the key finds them. Fix the files and start
        # the game again; this file goes away when nothing is left to report. See ../README.txt.


        """;

    public const string Readme = """
        BreezySRPG95: TRANSLATING THIS GAME
        ===================================

        This folder was written by BreezySRPG95 (Options > Display > Dump Script for Translation). It holds every text
        of the game, so that the game can be played in another language without touching the game's own files.


        THE FOLDERS
        -----------

          translation/jp/          The game's texts as the game has them. Writing the script again replaces this
                                   folder's files, so don't translate in here.
          translation/en/          YOUR translation. BreezySRPG95 shows it when Options > Display > Language is
                                   English (the option then says "English (Game Translated)"). With the language set
                                   to Japanese the game is shown as it always was.
          translation/en/BMP/      Optional: pictures with Japanese written in them (title screens, event pictures),
                                   redrawn. A picture here replaces the game's BMP/ picture of the same file name.
          translation/en/PROBLEMS.txt
                                   Written by BreezySRPG95 when your files don't fit the game (see PROBLEMS below).


        HOW TO START
        ------------

          1. Copy the jp folder and name the copy "en".
          2. Translate the files in en (by hand, or with a language model: see "LLM PROMPT.txt").
          3. Start the game, open Options (F1) > Display, and set Language to English.

        You don't have to translate everything at once. A text that is left as it is, or left empty, shows in
        Japanese, so a half-done translation already plays. A file you haven't started can be deleted from en.


        THE FILES
        ---------

          DATABASE.txt      Names (characters, classes, items, spells, terrain), item and spell descriptions, and
                            the game's menu words (WORD).
          SMAP_001.txt ...  Each map's event messages and choices, in the order of its events: the map's opening
                            first, then the rest by name ("EV002202" is map object 22's event number 2).

        Every text is an entry like this:

          <<< OPENING 11
          ルーティ：
          ん？・・・・・・・・いきなり
          これは何？
          >>>
          Rutee:
          Huh? ...What's this, all of a sudden?

          The "<<<" line is the entry's key: where the text is in the game. Don't change it.
          The lines between "<<<" and ">>>" are the game's own words. Don't change them: BreezySRPG95 checks them
            against the game to catch a translation that no longer fits.
          The lines after ">>>", up to the next "<<<", are what the game shows. Replace them with the translation.

        Save the files as UTF-8 text (any editor does by default; Notepad: "Save as" > Encoding UTF-8).


        RULES FOR EACH KIND OF TEXT
        ---------------------------

        Messages (the map files' entries with a number after the event name)
          - A first line that is just a name and a colon ("ルーティ：") is the speaker. Keep it a line of its own:
            "Rutee:". The restyled interfaces show it in a name box. Write the names the same way everywhere.
          - Lines wrap by themselves, between words, at the message window's width (about 40 letters). Break lines
            where you like or not at all.
          - A message window holds 5 lines (the speaker's included). A message a little longer is shown in a smaller
            font, 6 lines; a longer one goes on over more windows, one click each, the speaker's name on each.
            Try to keep a message to what fits one window, as the game had it.
          - Lines that say a map's conditions ("勝利条件：" ...) aren't a speaker. Keep the word "condition" in them
            ("Victory condition:", "Defeat conditions:") so they aren't taken for a name.

        Choices (entries ending in CHOICE)
          - One option a line, as many lines as the game's (the options are in the same order). Keep them short:
            about 30 letters.

        Names (DATABASE.txt: UNIT ... NAME, CLASS, ITEM, MAGIC, TERRAIN NAME)
          - Keep them short: they go in menus and windows made for 4-8 Japanese characters. Up to about 12 letters for
            units and classes, 16 for items and spells, 8 for terrain.

        Descriptions (ITEM ... DESC, MAGIC ... DESC)
          - One line, up to about 60 letters.

        Death messages (UNIT ... DEATH)
          - What a unit says when it falls: up to 5 lines of about 36 letters. They don't wrap by themselves.

        Menu words and pieces of sentences (WORD)
          Most are menu commands ("攻撃" Attack, "アイテム" Item). Some are pieces the game puts together into a
          line, in this order (a space is put between the pieces when the game is translated):

            [stat] WORD 64 [number] WORD 65 WORD 66/67/68/69    HP 25 pts recovered / Str 3 pts up
                     64 が (write <empty>)    65 ポイント (pts)   66 回復した (recovered)   67 強化された (boosted)
                     68 上昇した (up)          69 下降した (down)
            [number] WORD 60                                    25 EXP gained
            [number] WORD 61                                    500 gold gained
            [item] WORD 62                                      Potion obtained
            [unit] WORD 70 [level] WORD 71                      Rutee reached level 5
                     70 はレベル (reached level)   71 になった (write <empty>, or "!")
            [class] WORD 72                                     Knight: class changed
            [spell] WORD 73                                     Fire learned
            [item] (next line) WORD 58                          Potion / Use it?
            [gold]WORD 31                                       1200 G (no space is added here: write " G")
          WORD 81-88 ("ファイル１" ...) name the save files: "File 1" ... "File 8".
          To make a piece empty, translate it as a line that says only:  <empty>
          (an empty translation would leave the Japanese).


        LETTERS AND PUNCTUATION
        -----------------------

        Use plain letters and punctuation (the game's fonts are Japanese ones). Curly quotes, the "..." character and
        long dashes are turned into plain ' " ... - by themselves; accented letters (é, ü) may not show at all.
        Replace Japanese punctuation too: 「」 with "", ・・・ with ..., ！？ with !?.


        PICTURES
        --------

        Put the redrawn picture in translation/en/BMP/ with the game's file name (look in the game's BMP folder).
        Keep it the same size and kind: a Windows BMP, usually 256 colours. In pictures drawn over others, the first
        colour of the palette is the see-through one, as in the game's.


        PROBLEMS
        --------

        At each start BreezySRPG95 checks translation/en against the game and lists what doesn't fit in
        translation/en/PROBLEMS.txt: a key the game hasn't got (a mistyped "<<<" line), Japanese above ">>>" that isn't
        the game's (changed by hand, or translated by a language model too), a choice with too many or too few options,
        an entry that is in a file twice. Nothing in it stops the game: the texts are still used where they can be.


        SHARING A TRANSLATION
        ---------------------

        Share the en folder (zipped: the text is small). Players put it in the game's folder as translation/en and set
        Language to English. Don't share the game's own files.
        """;

    public const string Prompt = """
        LLM PROMPT FOR TRANSLATING A BREEZYSRPG95 TRANSLATION FILE
        ==========================================================

        How to use this: give a language model the prompt below (everything after the line of dashes), then one file
        from translation/jp (or a part of one: a long file is best given in parts of 100-200 entries, each part
        starting at a "<<<" line). Put what it gives back into translation/en under the same file name, the parts in
        order. Fill in the names list first, so every file spells the characters the same way. Afterwards start the
        game: translation/en/PROBLEMS.txt lists anything the model broke.

        --------------------------------------------------------------------------------------------------------------

        You are translating the script of a Japanese strategy RPG (made with Sim RPG Maker 95, from the late 1990s or
        2000s) into natural, readable English. The text is a game's dialogue and menus: keep each character's voice and
        the tone (jokes, slang, drama) rather than translating word for word.

        The file is made of entries. Each entry is:

        <<< KEY
        (Japanese lines: the original)
        >>>
        (the lines to translate: at first a copy of the original)

        Give back the complete file, in exactly the same format, with these rules:

        1. Copy every line that starts with "<<<" exactly as it is. Keep the entries in the same order. Don't add,
           drop, merge or split entries.
        2. Copy the Japanese lines between "<<<" and ">>>" exactly as they are, untranslated. They are used to check
           the translation against the game.
        3. Copy the ">>>" line, then write the English translation in place of the Japanese lines after it. Leave one
           empty line before the next "<<<".
        4. Copy the comment lines at the top of the file (starting with "#") as they are.
        5. Messages: if the first line is only a name followed by "：" (for example "ルーティ："), it is the speaker:
           write it as a line of its own with the name in English and a colon ("Rutee:"). Then the message. The game
           wraps long lines by itself, so line breaks are up to you, but keep a message about as long as the original:
           at most about 4 lines of 40 letters after the speaker's line.
        6. Lines that state a map's conditions (勝利条件, 敗北条件 ...) are not a speaker: translate them keeping the
           word "condition" ("Victory condition: Defeat all enemies").
        7. Entries whose key ends in CHOICE are the options of a choice: exactly as many lines as the original, one
           option per line, in the same order, each under 30 letters.
        8. Use only plain ASCII letters and punctuation: straight quotes ' and ", three dots ... (not the single
           ellipsis character), "-" for dashes, no accented letters. Turn Japanese brackets 「」『』 into "" and
           ・・・ or … into ...
        9. Don't add notes, explanations, alternatives or translator comments anywhere in the file.
        10. Keep in-jokes, names of places and attacks consistent through the file. If a word is a made-up name,
            romanize it instead of translating it.

        For the file DATABASE.txt (names, descriptions, menu words):
        - UNIT ... NAME, CLASS ... NAME: at most 12 letters. ITEM/MAGIC ... NAME: at most 16. TERRAIN ... NAME: at
          most 8. ITEM/MAGIC ... DESC: one line, at most 60 letters. UNIT ... DEATH: a character's last words, at most
          5 lines of 36 letters.
        - WORD entries are menu words (keep them short, like a game menu: "Attack", "Item", "End Turn") and pieces the
          game joins into sentences with a space between them, in this order:
            [stat] WORD 64 [number] WORD 65 WORD 66 (or 67, 68, 69)  ->  "HP 25 pts recovered", "Str 3 pts up"
            [number] WORD 60 -> "25 EXP gained"; [number] WORD 61 -> "500 gold gained"; [item] WORD 62 -> "Potion
            obtained"; [unit] WORD 70 [level] WORD 71 -> "Rutee reached level 5"; [class] WORD 72 -> "Knight: class
            changed"; [spell] WORD 73 -> "Fire learned".
          Translate those pieces so the joined sentence reads well. A piece English doesn't need (WORD 64 が,
          WORD 71 になった) is translated as a line saying only <empty>. WORD 31 is the money's unit after a number,
          with no space added: write it with a leading space (" G"). WORD 81 to 88 name the save files ("File 1" ...).

        Names (Japanese = English), to use everywhere:
        (fill in before using this prompt, for example:  ルーティ = Rutee)

        """;
}
