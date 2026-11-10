using BreezySRPG95.Runtime;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Srpg95.Data;
using Srpg95.Rules;

namespace BreezySRPG95.Camp;

/// <summary>
/// A menu in a green SYSTEM panel as FUN_00414344 / FUN_004144f4 / FUN_00413100 run them (GDI coordinates from the
/// panel's corner): header rows first — text at x 12, or "-" for an etched rule (dark at +15, light at +16) — then the
/// items, a 16-px row each from y 8, their text at <see cref="ItemX"/>, greyed when they can't be picked; the chosen
/// item's bar runs from 6 px left of the text to 6 px past <see cref="ItemWidth"/>, and the pointer chooses an item
/// inside that span. A paged list (<see cref="PageRows"/> rows, header included) gets a footer below them when it has
/// more than one page: a rule, 「ページ」 and 「%2d／%2d」, and SYSTEM.BMP's ◁ ▷ 54 and 30 px from the right edge.
/// </summary>
public sealed class MenuList
{
    public Rectangle Rect;
    public string[] Items = Array.Empty<string>();
    public bool[] Enabled = Array.Empty<bool>();
    public int Index;
    /// <summary>Header rows above the items: text, or "-" for an etched rule.</summary>
    public string[] Header = Array.Empty<string>();
    public int ItemX = 12;
    /// <summary>The items' width (their bar is 12 px wider); −1 for the panel's width less 24.</summary>
    public int ItemWidth = -1;
    /// <summary>Rows per page, the header included (0: one page).</summary>
    public int PageRows;
    public bool SkipDisabled;   // keyboard movement jumps over disabled rows (headers in the options)
    /// <summary>An item shown as available that still refuses the pick (the buzzer), as the shop's when gold runs short.</summary>
    public Func<int, bool>? CanPick;
    /// <summary>Right-aligned columns (the client's options screen): text per item, right edge at ColumnX from the panel's left.</summary>
    public string[][]? Columns;
    public int[] ColumnX = Array.Empty<int>();
    /// <summary>The first header row's column titles, right-aligned at <see cref="ColumnX"/> as the columns are.</summary>
    public string[]? HeaderColumns;
    /// <summary>WORD.DAT 63 「ページ」, set once the game's words are loaded.</summary>
    public static string PageLabel = "ページ";
    /// <summary>The page arrow last clicked (0 ◁, 1 ▷) and the frames it still shows pressed (3 ticks, FUN_0041f27c).</summary>
    int _pressed = -1, _pressedFrames;

    int Width => ItemWidth >= 0 ? ItemWidth : Rect.Width - 24;
    int PerPage => PageRows > 0 ? PageRows - Header.Length : Math.Max(1, Items.Length);
    public int Pages => Math.Max(1, (Items.Length + PerPage - 1) / PerPage);
    public int Page => Math.Clamp(Index, 0, Math.Max(0, Items.Length - 1)) / PerPage;
    int First => Page * PerPage;
    int Shown => Math.Min(PerPage, Items.Length - First);
    int RowY(int row) => Rect.Y + 8 + 16 * row;

    /// <summary>Item <paramref name="i"/>'s bar (on its page).</summary>
    public Rectangle Row(int i) => new(Rect.X + ItemX - 6, RowY(Header.Length + i % PerPage), Width + 12, 16);

    /// <summary>The item under the pointer, −1 for none.</summary>
    public int Hover(Point mouse) => Enumerable.Range(First, Shown).FirstOrDefault(k => Row(k).Contains(mouse), -1);

    Rectangle ArrowRect(int k) => new(Rect.Right - (k == 0 ? 54 : 30), RowY(PageRows) + 16, 16, 16);

    public void Set(string[] items, bool[]? enabled = null)
    {
        Items = items;
        Enabled = enabled ?? items.Select(_ => true).ToArray();
        (Columns, HeaderColumns) = (null, null);   // a list's columns go with its items: whoever sets them sets them after
        Index = Math.Clamp(Index, 0, Math.Max(0, items.Length - 1));
    }

    public int Navigate(BattleInput i, Point mouse)
    {
        if (Items.Length == 0) return -1;
        for (int step = 0; step < Items.Length && (i.Up || i.Down); step++)
        {
            Index = (Index + (i.Up ? Items.Length - 1 : 1)) % Items.Length;
            if (!SkipDisabled || Enabled[Index]) break;
        }
        if (PageRows > 0 && Pages > 1 && (i.Left || i.Right)) TurnPage(i.Right ? 1 : -1);
        int hover = Hover(mouse);
        if (i.MouseMoved && hover >= 0) Index = hover;
        if (_pressedFrames > 0) _pressedFrames--;
        int arrow = PageRows > 0 && Pages > 1 ? Enumerable.Range(0, 2).FirstOrDefault(k => ArrowRect(k).Contains(mouse), -1) : -1;
        if (i.Click && arrow >= 0) { Sfx.Click(); TurnPage(arrow == 1 ? 1 : -1); (_pressed, _pressedFrames) = (arrow, 6); return -1; }
        // a click counts on a row only (FUN_00413100: none is chosen while the pointer is off the rows); keys pick the bar's
        if ((i.Confirm && !i.Click) || (i.Click && hover >= 0))
        {
            if (Enabled[Index] && (CanPick?.Invoke(Index) ?? true)) { Sfx.Click(); return Index; }
            Sfx.Buzzer();
        }
        return -1;
    }

    /// <summary>The next or previous page, wrapping (FUN_0041e4b0), on its first row.</summary>
    void TurnPage(int d) => Index = Math.Min(Items.Length - 1, (Page + d + Pages) % Pages * PerPage);

    public void Draw(SpriteBatch sb, Ui.Ui ui, bool active, int frame)
    {
        ui.Panel(sb, Rect);
        var white = Ui.Ui.PanelText;
        void T(string s, int x, int y, Color c) => ui.Text12(sb, s, x, y, c);
        for (int h = 0; h < Header.Length; h++)
        {
            if (Header[h] == "-") EtchedRule(sb, ui, Rect, RowY(h));
            else T(Header[h], Rect.X + 12, RowY(h), white);
            if (h == 0 && HeaderColumns is { } titles)
                for (int col = 0; col < titles.Length && col < ColumnX.Length; col++)
                    if (ui.SmallText.Get(titles[col]) is { } ht) ht.Draw(sb, new Vector2(Rect.X + ColumnX[col] - ht.Width, Ui.Ui.Line16Y(ht, RowY(0))), white);
        }
        for (int k = First; k < First + Shown; k++)
        {
            var r = Row(k);
            var c = ui.MenuRow(sb, r, active && k == Index, Enabled[k], frame);
            T(Items[k], Rect.X + ItemX, r.Y, c);
            if (Columns is null) continue;
            for (int col = 0; col < Columns.Length && col < ColumnX.Length; col++)
                if (k < Columns[col].Length && Columns[col][k] is { Length: > 0 } s && ui.SmallText.Get(s) is { } ct)
                    ct.Draw(sb, new Vector2(Rect.X + ColumnX[col] - ct.Width, Ui.Ui.Line16Y(ct, r.Y)), c);
        }
        if (PageRows > 0 && Pages > 1) PageFooter(sb, ui, Rect, RowY(PageRows), Page, Pages, _pressedFrames > 0 ? _pressed : -1);
    }

    /// <summary>A header rule (FUN_00414344): dark at the row's y + 7, light at y + 8, 12 px in from the panel's sides.</summary>
    public static void EtchedRule(SpriteBatch sb, Ui.Ui ui, Rectangle panel, int rowY)
    {
        if (Ui.Ui.Gilded) { ui.GoldRule(sb, panel.X + 12, rowY + 8, panel.Width - 24); return; }
        var (light, dark) = Ui.Ui.EdgeColors(Ui.Ui.PanelColor.Green);
        ui.Fill(sb, new Rectangle(panel.X + 12, rowY + 7, panel.Width - 24, 1), dark);
        ui.Fill(sb, new Rectangle(panel.X + 12, rowY + 8, panel.Width - 24, 1), light);
    }

    /// <summary>
    /// A paged list's footer (FUN_00420ca8, FUN_0041f27c): a rule on the row after the last, then 「ページ」 at x 12 and
    /// 「%2d／%2d」 at x 60 a row lower, with ◁ ▷ (SYSTEM.BMP y 304; x 16 more while pressed) 54 and 30 px from the right.
    /// </summary>
    public static void PageFooter(SpriteBatch sb, Ui.Ui ui, Rectangle panel, int rowY, int page, int pages, int pressed)
    {
        EtchedRule(sb, ui, panel, rowY);
        void T(string s, int x) => ui.Text12(sb, s, panel.X + x, rowY + 16, Ui.Ui.PanelText);
        T(PageLabel, 12);
        T($"{page + 1,2}／{pages,2}", 60);
        if (ui.SystemBmp is { } sys)
            for (int k = 0; k < 2; k++)
                sb.Draw(sys, new Vector2(panel.Right - (k == 0 ? 54 : 30), rowY + 16), new Rectangle(k * 32 + (pressed == k ? 16 : 0), 304, 16, 16), Color.White);
    }
}

/// <summary>
/// The camp between maps (SMAP camp_before_battle) as the exe's camp states run it (FUN_0041f6d4 and the rest; GDI
/// positions). CAMP_nnn full screen with the camp BGM, and:
/// - the menu at (16,16), 96 × 128: 次のステージ (once a unit is set to 出撃) / ユニット編成 (once there is an army) /
///   アイテム購入 / アイテム売却 / セーブする / ロードする / ゲーム終了 — and the client's 環境設定 (Options) before
///   ゲーム終了, a row more (96 × 144). Its questions and file lists take its place.
/// - ユニット編成 (FUN_004202dc, FUN_00420564, FUN_004208cc): the army at (88,40), 180 × 400, 22 a page — names at x 100,
///   出撃 / ―― at x 232 — beside the commands at (288,40), 108 × 96 (武器 / 防具 / 装飾品を替える, 道具を使う, 技を見る), the
///   chosen unit's status window at (288,152) and 「出撃ユニット数」 at (432,40), 120 × 48. The army and the commands
///   answer the pointer together (FUN_0041e210): a click on a name chooses that unit, one on its 出撃 part chooses and
///   toggles it (on while fewer than the map's start positions are, never off for one of the map's characters, not at
///   all when the map fixes the army); the part of a row under the pointer gets the bar.
/// - a command's list at (80,104) with the description at (80,32) (FUN_004209e8, FUN_00420e60): equipment of the slot's
///   kind the class may use, 「（装備をはずす）」 first when something is held; for 道具を使う stat-up items the class may
///   use and class-change items for its level and class — 「%-22s×%2d」 in the inventory's order, 18 a page; for 技を見る
///   the known spells, 「%-22sＴＰ%4d」. A pick (FUN_0041e4b0) equips, takes off or uses the item at once, without a
///   message, and goes back to the commands.
/// - the shop (FUN_004210f4 / FUN_00421478) at (80,96), 264 × 336, 16 a page: 「  アイテム … 値段 (下取り額) … 所持数」, a
///   rule, then 「%-22s%10d%8d」; the gold, 「%14d,肉」, in a panel at (368,96). Buying needs the price and fewer than 99
///   held (FUN_0041e94c); an item sells for half its price, 1 for a price of 1, not at all without one (FUN_0041eba8).
/// Every menu opens with the pointer on its first row and comes back to the row it was left from (FUN_00413100).
/// </summary>
public sealed class CampScreen
{
    enum Mode { Menu, Units, Equip, UseItem, Skills, Buy, Sell, Save, Load, Confirm }

    readonly Database _db;
    readonly GameFolder _folder;
    readonly Textures _tex;
    readonly Ui.Ui _ui;
    readonly GameState _state;
    readonly MapData _next;
    readonly int _nextIndex;
    readonly Battlefield _field;   // for stats (terrain unused)
    Mode _mode = Mode.Menu;
    readonly MenuList _menu = new() { Rect = new Rectangle(16, 16, 96, 144), ItemWidth = 72 };
    readonly MenuList _commands = new() { Rect = new Rectangle(288, 40, 108, 96), ItemWidth = 84 };
    readonly MenuList _list = new();
    readonly MenuList _confirm = new() { ItemWidth = 36 };
    /// <summary>The army's rows: roster entries, by their place in the roster (one unit id can have several).</summary>
    readonly List<int> _unitIds = new();
    /// <summary>The chosen unit's row in the army (+0x5c64) and the army list's page.</summary>
    int _unitRow, _unitPage;
    /// <summary>The army row under the pointer (−1: none), and whether on its 出撃 part.</summary>
    int _hoverRow = -1;
    bool _hoverSortie;
    /// <summary>Keys (a modern addition) work the commands rather than the army; the army bar follows the keys.</summary>
    bool _commandKeys, _armyKeys;
    /// <summary>The command a list came from: 0–2 equipment, 3 道具を使う, 4 技を見る (+0x5c68).</summary>
    int _slot;
    List<int> _rowItems = new();   // ITEM ids per row, −1 for 「（装備をはずす）」
    List<Magic> _rowSpells = new();
    Action? _confirmYes;
    int _confirmRow;
    bool _pointMenu = true;
    int _armyPressed = -1, _armyPressedFrames;

    public Action? StartNext { get; set; }
    /// <summary>A save picked to load, and its file number (its BreezySRPG95 note is the file's own).</summary>
    public Action<SaveFile, int>? Loaded { get; set; }
    /// <summary>A file was just written (BreezySRPG95 notes its play time and picture beside it).</summary>
    public Action<int>? Saved { get; set; }
    /// <summary>The extended save / load screen (modern), when the options choose it over the original's file list (null).</summary>
    public Func<bool, Ui.SaveMenu?>? NewSaveMenu { get; set; }
    Ui.SaveMenu? _saveMenu;
    public Action? Quit { get; set; }
    /// <summary>The menu's 環境設定 (modern): the game opens the options; the menu stays up under them.</summary>
    public Action? OptionsRequested { get; set; }
    /// <summary>Moves the mouse pointer (FUN_00409d24).</summary>
    public Action<Point>? WarpPointer { get; set; }
    public string Bgm => _next.Bgm[0];
    public bool AcceptsCancel => _mode != Mode.Menu;

    public CampScreen(Database db, GameFolder folder, Textures tex, Ui.Ui ui, GameState state, MapData next, int nextIndex)
    {
        (_db, _folder, _tex, _ui, _state, _next, _nextIndex) = (db, folder, tex, ui, state, next, nextIndex);
        _field = new Battlefield(next, db, null);
        RefreshMenu();
    }

    string W(int i) => i >= 0 && i < _db.Words.Length ? _db.Words[i] : "";
    int SortieMax => _next.StartPositions.Length;
    /// <summary>The chosen roster entry (its place in the roster), −1 with an empty army.</summary>
    int Unit => _unitIds.Count > 0 ? _unitIds[Math.Clamp(_unitRow, 0, _unitIds.Count - 1)] : -1;

    /// <summary>"%-ns": padded with spaces to <paramref name="bytes"/> Shift-JIS bytes.</summary>
    static string Pad(string s, int bytes) => s + new string(' ', Math.Max(0, bytes - Bin.Sjis.GetByteCount(s)));
    /// <summary>"%ns": spaces first, to <paramref name="bytes"/> Shift-JIS bytes.</summary>
    static string PadLeft(string s, int bytes) => new string(' ', Math.Max(0, bytes - Bin.Sjis.GetByteCount(s))) + s;

    BattleUnit UnitOf(int slot)
    {
        var e = _state.Roster[slot];
        var u = _db.Units[e.Unit];
        return new BattleUnit
        {
            Unit = e.Unit, Side = Side.Ally, ClassId = e.ClassId, Level = e.Level, Exp = e.Exp, Base = e.Base, Growth = u.GrowthPct,
            Hp = e.Base[0], Mp = e.Base[1], Weapon = e.Weapon, Armor = e.Armor, Accessory = e.Accessory, Name = u.Name,
        };
    }

    // ---------------- update ----------------

    public void Update(BattleInput i, Point mouse)
    {
        MousePoint = mouse;
        if (_pointMenu)
        {
            _pointMenu = false;
            if (PointerRow() is { } r) WarpPointer?.Invoke(r.Center);
        }
        bool back = i.Cancel || i.RightClick;
        switch (_mode)
        {
            case Mode.Menu:
                switch (_menu.Navigate(i, mouse))
                {
                    case 0: Confirm(0, new[] { W(55), W(56) }, () => StartNext?.Invoke()); break;   // キャンプを終了して / 進撃を開始します
                    case 1: OpenUnits(); break;
                    case 2: OpenShop(sell: false); break;
                    case 3: OpenShop(sell: true); break;
                    case 4: OpenFiles(save: true); break;
                    case 5: OpenFiles(save: false); break;
                    case OptionsRow: OptionsRequested?.Invoke(); break;
                    case 7: Confirm(7, new[] { W(57) }, () => Quit?.Invoke()); break;                 // ゲームを終了します
                }
                break;
            case Mode.Confirm:
                if (back) { BackToMenu(_confirmRow); break; }
                if (_confirm.Navigate(i, mouse) is var c and >= 0) { if (c == 0) _confirmYes?.Invoke(); else BackToMenu(_confirmRow); }
                break;
            case Mode.Units:
                UpdateUnits(i, mouse, back);
                break;
            case Mode.Equip:
            case Mode.UseItem:
                if (back) { BackToCommands(); break; }
                if (_list.Navigate(i, mouse) is var e and >= 0)
                {
                    if (_mode == Mode.Equip) Equip(_rowItems[e]); else UseItem(_rowItems[e]);
                    BackToCommands();
                }
                break;
            case Mode.Skills:
                if (back) { BackToCommands(); break; }
                _list.Navigate(i with { Confirm = false, Click = false }, mouse);   // the descriptions only
                break;
            case Mode.Buy:
                if (back) { BackToMenu(2); break; }
                if (_list.Navigate(i, mouse) is var b and >= 0) Buy(_rowItems[b]);
                break;
            case Mode.Sell:
                if (back) { BackToMenu(3); break; }
                if (_list.Navigate(i, mouse) is var s and >= 0) Sell(_rowItems[s]);
                break;
            case Mode.Save or Mode.Load when _saveMenu is { } files:
                var (result, slot) = files.Update(i, mouse);
                if (result == Ui.SaveMenu.Result.None) break;
                files.Dispose();
                _saveMenu = null;
                if (result == Ui.SaveMenu.Result.Cancelled) BackToMenu(_mode == Mode.Save ? 4 : 5);
                else if (_mode == Mode.Save) SaveTo(slot);
                else LoadFrom(slot);
                break;
            case Mode.Save:
                if (back) { BackToMenu(4); break; }
                if (_list.Navigate(i, mouse) is var f and >= 0) SaveTo(f + 1);
                break;
            case Mode.Load:
                if (back) { BackToMenu(5); break; }
                if (_list.Navigate(i, mouse) is var l and >= 0) LoadFrom(l + 1);
                break;
        }
    }

    /// <summary>The row a menu that opened or came back puts the pointer on.</summary>
    Rectangle? PointerRow() => _mode switch
    {
        Mode.Menu => _menu.Row(_menu.Index),
        Mode.Confirm => _confirm.Row(_confirm.Index),
        Mode.Units => _commandKeys ? _commands.Row(_commands.Index) : null,
        Mode.Save or Mode.Load when _saveMenu is not null => null,
        _ when _list.Items.Length > 0 => _list.Row(_list.Index),
        _ => null,
    };

    /// <summary>FUN_0041f6d4: 次のステージ once a unit is set to 出撃, ユニット編成 once there is an army, the rest always.</summary>
    void RefreshMenu() =>
        _menu.Set(new[] { W(24), W(25), W(26), W(27), W(21), W(22), OptionsLabel, W(23) },   // WORD.DAT 24-27, 21-22, the client's, 23
            new[] { _state.Roster.Any(e => e.Sortie), _state.Roster.Count > 0, true, true, true, true, true, true });

    /// <summary>The menu's 環境設定 row, and its label in the language the options may just have changed.</summary>
    const int OptionsRow = 6;
    static string OptionsLabel => Ui.Lang.T("Options", "環境設定");

    void BackToMenu(int row)
    {
        RefreshMenu();
        _menu.Index = row;
        _mode = Mode.Menu;
        _pointMenu = true;
    }

    /// <summary>
    /// 次のステージ / ゲーム終了 (FUN_0041f948, FUN_0041fb54): the question's lines, a rule and はい / いいえ 36 px wide in the
    /// middle, at (16,16), (the larger of 3 and half the longest line's bytes, plus 2) × 12 px wide.
    /// </summary>
    void Confirm(int row, string[] lines, Action yes)
    {
        _confirmRow = row;
        _confirmYes = yes;
        int w = (Math.Max(3, lines.Max(l => (Bin.Sjis.GetByteCount(l) + 1) / 2)) + 2) * 12;
        _confirm.Rect = new Rectangle(16, 16, w, (lines.Length + 4) * 16);
        _confirm.Header = lines.Append("-").ToArray();
        _confirm.ItemX = w / 2 - 18;
        _confirm.Set(new[] { W(45), W(46) });   // はい / いいえ
        _confirm.Index = 0;
        _mode = Mode.Confirm;
        _pointMenu = true;
    }

    // ---------------- ユニット編成 ----------------

    const int ArmyRows = 22;
    static readonly Rectangle ArmyRect = new(88, 40, 180, 400);
    /// <summary>A row's name part (its bar and the pointer's span, FUN_0041f0c4): x 94, 126 wide.</summary>
    static Rectangle NameRow(int k) => new(94, 48 + 16 * k, 126, 16);
    /// <summary>A row's 出撃 part: x 226, 36 wide.</summary>
    static Rectangle SortieRow(int k) => new(226, 48 + 16 * k, 36, 16);
    int ArmyPages => Math.Max(1, (_unitIds.Count + ArmyRows - 1) / ArmyRows);

    /// <summary>FUN_0041e028: the army on the chosen unit's page, the pointer on its name.</summary>
    void OpenUnits()
    {
        _unitIds.Clear();
        _unitIds.AddRange(Enumerable.Range(0, _state.Roster.Count));
        _unitRow = Math.Clamp(_unitRow, 0, Math.Max(0, _unitIds.Count - 1));
        _unitPage = _unitRow / ArmyRows;
        _commandKeys = _armyKeys = false;
        _mode = Mode.Units;
        RefreshCommands();
        WarpPointer?.Invoke(new Point(172, 56 + 16 * (_unitRow % ArmyRows)));
    }

    void UpdateUnits(BattleInput i, Point mouse, bool back)
    {
        if (_armyPressedFrames > 0) _armyPressedFrames--;
        if (back)
        {
            if (_commandKeys && !i.RightClick) { _commandKeys = false; return; }   // keys: back to the army
            BackToMenu(1);
            return;
        }
        // the army row under the pointer: its name part, or its 出撃 part unless the map fixes the army
        _hoverRow = -1;
        int first = _unitPage * ArmyRows;
        for (int k = 0; k < ArmyRows && first + k < _unitIds.Count; k++)
        {
            if (NameRow(k).Contains(mouse)) (_hoverRow, _hoverSortie) = (first + k, false);
            else if (SortieRow(k).Contains(mouse) && !_next.DisablePartyChanges) (_hoverRow, _hoverSortie) = (first + k, true);
        }
        if (i.MouseMoved) _armyKeys = false;
        if (i.Click)
        {
            int arrow = ArmyPages > 1 ? Enumerable.Range(0, 2).FirstOrDefault(k => ArmyArrow(k).Contains(mouse), -1) : -1;
            if (arrow >= 0)
            {
                Sfx.Click();
                _unitPage = (_unitPage + (arrow == 1 ? 1 : ArmyPages - 1)) % ArmyPages;
                (_armyPressed, _armyPressedFrames) = (arrow, 6);
                return;
            }
            if (_hoverRow >= 0)
            {
                Sfx.Click();
                if (_hoverRow != _unitRow) { _unitRow = _hoverRow; RefreshCommands(); }
                if (_hoverSortie) ToggleSortie();
                return;
            }
        }
        if (!_commandKeys && _unitIds.Count > 0)
        {
            if (i.Up || i.Down)
            {
                _unitRow = (_unitRow + (i.Up ? _unitIds.Count - 1 : 1)) % _unitIds.Count;
                _unitPage = _unitRow / ArmyRows;
                _armyKeys = true;
                RefreshCommands();
            }
            if (i.Left || i.Right) { ToggleSortie(); _armyKeys = true; }
            if (i.Confirm && !i.Click) { Sfx.Click(); _commandKeys = true; return; }
        }
        // the commands answer the pointer all along, the keys once Enter moved them there
        var ci = _commandKeys ? i : i with { Up = false, Down = false, Left = false, Right = false, Confirm = i.Click };
        switch (_commands.Navigate(ci, mouse))
        {
            case 0: case 1: case 2: case 3: OpenItems(_commands.Index); break;
            case 4: OpenSkills(); break;
        }
    }

    Rectangle ArmyArrow(int k) => new(ArmyRect.Right - (k == 0 ? 54 : 30), 48 + 16 * ArmyRows + 16, 16, 16);

    /// <summary>
    /// 出撃 ↔ ―― (FUN_0041e210): on only while fewer than the map's start positions are; a unit from the map's character
    /// list can't be taken off; nothing changes when the map fixes the army.
    /// </summary>
    void ToggleSortie()
    {
        if (_next.DisablePartyChanges || Unit < 0) return;
        int id = Unit;
        var e = _state.Roster[id];
        if (!e.Sortie && _state.Roster.Count(x => x.Sortie) < SortieMax) _state.Roster[id] = e with { Sortie = true };
        else if (e.Sortie && !_next.CharacterList.Contains(e.Unit)) _state.Roster[id] = e with { Sortie = false };
    }

    static readonly ItemType[] SlotTypes = { ItemType.Weapon, ItemType.Armor, ItemType.Accessory };

    static bool Usable(Item it, int classId) => classId >= 0 && classId < it.ClassUsable.Length && it.ClassUsable[classId] != 0;

    /// <summary>
    /// A command's rows (FUN_004209e8): 「（装備をはずす）」 (−1) first when the slot holds something, then the inventory's
    /// items in its order — for a slot, those of its kind the class may use; for 道具を使う, stat-up items the class may
    /// use and class-change items for its level whose classes include its own.
    /// </summary>
    List<int> ItemChoices(int slot, RosterEntry e)
    {
        var rows = new List<int>();
        if (slot < 3 && (slot switch { 0 => e.Weapon, 1 => e.Armor, _ => e.Accessory }) >= 0) rows.Add(-1);
        foreach (var (id, n) in _state.Items)
        {
            if (n <= 0 || id < 0 || id >= _db.Items.Length) continue;
            var it = _db.Items[id];
            bool ok = slot < 3
                ? it.Type == SlotTypes[slot] && Usable(it, e.ClassId)
                : it.Type == ItemType.StatUp && Usable(it, e.ClassId)
                  || it.Type == ItemType.ClassChange && e.Level >= it.S.ClassChangeLevel && UnitChanges.ClassChangeTarget(it, e.ClassId) >= 0;
            if (ok) rows.Add(id);
        }
        return rows;
    }

    /// <summary>FUN_00420564: a slot's command while it holds something or there's something to put in it; 道具を使う while
    /// something can be used; 技を見る once the unit knows a spell.</summary>
    void RefreshCommands()
    {
        _commands.Set(new[] { W(33), W(34), W(35), W(36), W(37) },   // 武器 / 防具 / 装飾品を替える, 道具を使う, 技を見る
            Unit < 0 ? new bool[5] : new[]
            {
                ItemChoices(0, _state.Roster[Unit]).Count > 0, ItemChoices(1, _state.Roster[Unit]).Count > 0,
                ItemChoices(2, _state.Roster[Unit]).Count > 0, ItemChoices(3, _state.Roster[Unit]).Count > 0,
                _field.KnownSpells(UnitOf(Unit)).Any(),
            });
    }

    /// <summary>Back from a command's list, the pointer on that command (FUN_0041e4b0, FUN_0041e7fc).</summary>
    void BackToCommands()
    {
        _mode = Mode.Units;
        RefreshCommands();
        _commands.Index = _slot;
        _commandKeys = true;
        _pointMenu = true;
    }

    void OpenItems(int slot)
    {
        _slot = slot;
        _rowItems = ItemChoices(slot, _state.Roster[Unit]);
        _list.Rect = new Rectangle(80, 104, 180, 336);
        _list.Header = Array.Empty<string>();
        (_list.ItemX, _list.ItemWidth, _list.PageRows, _list.CanPick) = (12, 156, 18, null);
        _list.Index = 0;
        _list.Set(_rowItems.Select(id => id < 0 ? W(32) : $"{Pad(_db.Items[id].Name, 22)}×{_state.ItemCount(id),2}").ToArray());
        _mode = slot < 3 ? Mode.Equip : Mode.UseItem;
        _pointMenu = true;
    }

    /// <summary>FUN_0040af4c / FUN_0040b038: the slot's old item back to the inventory, the new one (if any) out of it.</summary>
    void Equip(int item)
    {
        int unit = Unit;
        var e = _state.Roster[unit];
        int old = _slot switch { 0 => e.Weapon, 1 => e.Armor, _ => e.Accessory };
        if (old >= 0) _state.AddItem(old, 1);
        if (item >= 0) _state.AddItem(item, -1);
        _state.Roster[unit] = _slot switch
        {
            0 => e with { Weapon = item },
            1 => e with { Armor = item },
            _ => e with { Accessory = item },
        };
    }

    /// <summary>
    /// FUN_0040b074 at the camp: a stat-up raises the base stats (positive values only; max HP/MP up to 60, the rest
    /// 30, FUN_0040b8f0); a class-change item changes the class (FUN_0040be00 → FUN_0040bb0c). One is used up; nothing
    /// is said.
    /// </summary>
    void UseItem(int id)
    {
        int unit = Unit;
        var e = _state.Roster[unit];
        var it = _db.Items[id];
        if (it.Type == ItemType.StatUp) _state.Roster[unit] = e with { Base = UnitChanges.StatUp(e.Base, it.S.StatUp).Item1 };
        else Party.UseClassChangeItem(_state, _db, unit, it);
        _state.AddItem(id, -1);
    }

    void OpenSkills()
    {
        _slot = 4;
        _rowSpells = _field.KnownSpells(UnitOf(Unit)).ToList();
        int units = (Bin.Sjis.GetByteCount(W(3)) + 1) / 2;   // ＴＰ
        _list.Rect = new Rectangle(80, 104, (units + 15) * 12, 336);
        _list.Header = Array.Empty<string>();
        (_list.ItemX, _list.ItemWidth, _list.PageRows, _list.CanPick) = (12, (units + 13) * 12, 0, null);
        _list.Index = 0;
        _list.Set(_rowSpells.Select(m => $"{Pad(m.Name, 22)}{W(3)}{m.MpCost,4}").ToArray());
        _mode = Mode.Skills;
        _pointMenu = true;
    }

    // ---------------- shop ----------------

    /// <summary>FUN_0041eba8: half the price, 1 for a price of 1; an item without a price can't be sold.</summary>
    static int SellPrice(Item it) => it.Price == 1 ? 1 : it.Price / 2;

    void OpenShop(bool sell)
    {
        _mode = sell ? Mode.Sell : Mode.Buy;
        _list.Rect = new Rectangle(80, 96, 264, 336);
        (_list.ItemX, _list.ItemWidth, _list.PageRows) = (12, 240, 18);
        // 「  %-22s」 アイテム, the price's word centred in 8 and then 9 bytes' worth of spaces, 「%8s」 所持数 (FUN_00421220)
        string price = W(sell ? 29 : 28);   // 下取り額 / 値段
        int len = Bin.Sjis.GetByteCount(price);
        // the restyled skins' fonts aren't fixed-width, so spaces can't line the columns up: the price and the count go in
        // columns of their own, right-aligned under their titles where the original's fixed-width text ends them
        bool columns = Ui.Ui.Restyled;
        _list.Header = new[]
        {
            columns ? $"  {W(18)}" : $"  {Pad(W(18), 22)}{new string(' ', Math.Max(0, 8 - len) / 2)}{price}{new string(' ', Math.Max(0, 9 - len) / 2)}{PadLeft(W(30), 8)}",
            "-",
        };
        _list.ColumnX = columns ? new[] { 204, 252 } : Array.Empty<int>();
        _list.Index = 0;
        RefreshShop();
        _pointMenu = true;
    }

    void RefreshShop()
    {
        bool sell = _mode == Mode.Sell;
        _rowItems = sell
            ? _state.Items.Where(kv => kv.Value > 0 && kv.Key >= 0 && kv.Key < _db.Items.Length).Select(kv => kv.Key).ToList()
            : _next.ShopItems.Where(id => id >= 0 && id < _db.Items.Length).ToList();
        string Price(Item it) => sell && it.Price == 0 ? "－" : $"{(sell ? SellPrice(it) : it.Price)}";
        string Row(int id)
        {
            var it = _db.Items[id];
            return Ui.Ui.Restyled ? it.Name : $"{Pad(it.Name, 22)}{Price(it),10}{_state.ItemCount(id),8}";
        }
        _list.Set(_rowItems.Select(Row).ToArray(), sell ? _rowItems.Select(id => _db.Items[id].Price > 0).ToArray() : null);
        _list.Columns = Ui.Ui.Restyled
            ? new[] { _rowItems.Select(id => Price(_db.Items[id])).ToArray(), _rowItems.Select(id => $"{_state.ItemCount(id)}").ToArray() }
            : null;
        _list.HeaderColumns = Ui.Ui.Restyled ? new[] { W(sell ? 29 : 28), W(30) } : null;   // 値段 or 下取り額, 所持数
        _list.CanPick = sell ? null : k => _db.Items[_rowItems[k]].Price <= _state.Gold && _state.ItemCount(_rowItems[k]) < 99;
    }

    void Buy(int id)
    {
        _state.Gold -= _db.Items[id].Price;
        _state.AddItem(id, 1);
        RefreshShop();
    }

    void Sell(int id)
    {
        _state.AddItem(id, -1);
        _state.Gold += SellPrice(_db.Items[id]);
        RefreshShop();
    }

    // ---------------- save / load ----------------

    /// <summary>FUN_0041fd08 / FUN_0041ffe0: 「セーブします」 (「ロードします」), a rule and ファイル１–８ at (16,16), the files 60 px wide in the middle.</summary>
    void OpenFiles(bool save)
    {
        if (NewSaveMenu?.Invoke(save) is { } menu)
        {
            _saveMenu = menu;
            _mode = save ? Mode.Save : Mode.Load;
            return;
        }
        string title = W(save ? 53 : 54);
        int w = (Math.Max(5, (Bin.Sjis.GetByteCount(title) + 1) / 2) + 2) * 12;
        _list.Rect = new Rectangle(16, 16, w, 176);
        _list.Header = new[] { title, "-" };
        (_list.ItemX, _list.ItemWidth, _list.PageRows, _list.CanPick) = (w / 2 - 30, 60, 0, null);
        var exists = Enumerable.Range(1, 8).Select(n => File.Exists(SaveFile.PathFor(_folder, n))).ToArray();
        _list.Set(Enumerable.Range(81, 8).Select(W).ToArray(), save ? null : exists);   // ファイル１–８
        _list.Index = 0;
        _mode = save ? Mode.Save : Mode.Load;
        _pointMenu = true;
    }

    /// <summary>FUN_0041ef30: the file written, back to the menu's セーブする.</summary>
    void SaveTo(int slot)
    {
        string path = SaveFile.PathFor(_folder, slot);
        var template = SaveFile.Load(path)?.Raw ?? _state.LastSave;
        if (SaveFile.ForCamp(_state, _db, _nextIndex, Bgm, template).Write(path)) Saved?.Invoke(slot);
        else Sfx.Buzzer();   // couldn't be written (another program holds the file)
        BackToMenu(4);
    }

    void LoadFrom(int slot)
    {
        if (SaveFile.Load(SaveFile.PathFor(_folder, slot)) is { } f) Loaded?.Invoke(f, slot);
    }

    // ---------------- drawing ----------------

    void T(SpriteBatch sb, string s, int x, int y, Color c) => _ui.Text12(sb, s, x, y, c);

    public void Draw(SpriteBatch sb, int frame)
    {
        _menu.Items[OptionsRow] = OptionsLabel;
        if (_tex.Get($"CAMP_{_next.CampBackground:000}.BMP", transparent: false) is { } back) sb.Draw(back, Vector2.Zero, Color.White);
        else _ui.Fill(sb, new Rectangle(0, 0, 640, 480), Color.Black);
        switch (_mode)
        {
            case Mode.Menu:
                _menu.Draw(sb, _ui, true, frame);
                break;
            case Mode.Confirm:
                _confirm.Draw(sb, _ui, true, frame);
                break;
            case Mode.Save or Mode.Load when _saveMenu is not null:
                _saveMenu.Draw(sb, frame);
                break;
            case Mode.Save or Mode.Load:
                _list.Draw(sb, _ui, true, frame);
                break;
            case Mode.Units:
                DrawArmy(sb, frame);
                _commands.Draw(sb, _ui, _commandKeys || _commands.Hover(MousePoint) >= 0, frame);
                DrawSortieCount(sb);
                if (Unit >= 0) Ui.StatusWindow.Draw(sb, _ui, _tex, _db, _field, UnitOf(Unit), StatusRect, mouse: MousePoint);
                break;
            case Mode.Equip or Mode.UseItem or Mode.Skills:
                _list.Draw(sb, _ui, true, frame);
                if (Unit >= 0) Ui.StatusWindow.Draw(sb, _ui, _tex, _db, _field, UnitOf(Unit), StatusRect);
                DrawDescription(sb);
                break;
            case Mode.Buy or Mode.Sell:
                _list.Draw(sb, _ui, true, frame);
                DrawGold(sb);
                DrawDescription(sb);
                break;
        }
    }

    /// <summary>The camp as a save's picture shows it: its background and the menu, nothing chosen.</summary>
    public void DrawBackdrop(SpriteBatch sb, int frame)
    {
        if (_tex.Get($"CAMP_{_next.CampBackground:000}.BMP", transparent: false) is { } back) sb.Draw(back, Vector2.Zero, Color.White);
        else _ui.Fill(sb, new Rectangle(0, 0, 640, 480), Color.Black);
        _menu.Draw(sb, _ui, false, frame);
    }

    /// <summary>The pointer as the last update saw it (for the commands' bar while the keys work the army).</summary>
    public Point MousePoint { get; set; }

    static readonly Rectangle StatusRect = new(288, 152, 264, 288);

    /// <summary>
    /// The army (FUN_004203c0, FUN_0041f27c): names at x 100 and 出撃 / ―― at x 232 from y 48, the bar on the pointer's
    /// part of its row (or, keys moving, the chosen unit's name); 22 a page with the footer when there are more.
    /// </summary>
    void DrawArmy(SpriteBatch sb, int frame)
    {
        _ui.Panel(sb, ArmyRect);
        int first = _unitPage * ArmyRows;
        for (int k = 0; k < ArmyRows && first + k < _unitIds.Count; k++)
        {
            int row = first + k, id = _unitIds[row];
            bool onName = _hoverRow == row ? !_hoverSortie : _hoverRow < 0 && _armyKeys && !_commandKeys && row == _unitRow;
            bool onSortie = _hoverRow == row && _hoverSortie;
            var cn = onName ? _ui.MenuRow(sb, NameRow(k), true, true, frame) : Ui.Ui.PanelText;
            T(sb, _db.Units[_state.Roster[id].Unit].Name, 100, 48 + 16 * k, cn);
            var cs = onSortie ? _ui.MenuRow(sb, SortieRow(k), true, true, frame) : Ui.Ui.PanelText;
            T(sb, _state.Roster[id].Sortie ? W(39) : W(40), 232, 48 + 16 * k, cs);   // 出撃 / ――
        }
        if (ArmyPages > 1)
            MenuList.PageFooter(sb, _ui, ArmyRect, 48 + 16 * ArmyRows, _unitPage, ArmyPages, _armyPressedFrames > 0 ? _armyPressed : -1);
    }

    /// <summary>FUN_004208cc: 「出撃ユニット数」 at (444,48) and 「%2d／%2d」, the units set to 出撃 and the map's start positions, at (504,64).</summary>
    void DrawSortieCount(SpriteBatch sb)
    {
        _ui.Panel(sb, new Rectangle(432, 40, 120, 48));
        T(sb, W(38), 444, 48, Ui.Ui.PanelText);
        T(sb, $"{_state.Roster.Count(e => e.Sortie),2}／{SortieMax,2}", 504, 64, Ui.Ui.PanelText);
    }

    /// <summary>FUN_00421030: the gold, 「%14d」 and 「,肉」, in a panel at (368,96), its text ending at x 548.</summary>
    void DrawGold(SpriteBatch sb)
    {
        _ui.Panel(sb, new Rectangle(368, 96, 192, 32));
        string unit = W(31);
        T(sb, $"{_state.Gold,14}{unit}", 464 - 6 * Bin.Sjis.GetByteCount(unit), 104, Ui.Ui.PanelText);
    }

    /// <summary>The description window (FUN_0041487c, FUN_004142d4) at (80,32), 480 × 48: 「name：」 at (92,40), the text at (92,56).</summary>
    void DrawDescription(SpriteBatch sb)
    {
        var d = new Rectangle(80, 32, 480, 48);
        _ui.Panel(sb, d);
        string? name = null, desc = null;
        Item? item = null;
        if (_mode == Mode.Skills && _list.Index < _rowSpells.Count) (name, desc) = (_rowSpells[_list.Index].Name, _rowSpells[_list.Index].Desc);
        else if (_mode != Mode.Skills && _list.Index < _rowItems.Count && _rowItems[_list.Index] >= 0)
        {
            item = _db.Items[_rowItems[_list.Index]];
            (name, desc) = (item.Name, item.Desc);
        }
        if (name is null) return;
        T(sb, $"{name}：", 92, 40, Ui.Ui.PanelText);
        if (item is not null && _ui.SmallText.Get(Ui.ItemInfo.Summary(item, _db)) is { } st)   // modern addition
            st.Draw(sb, new Vector2(d.Right - 16 - st.Width, d.Y + 10), Ui.Ui.StatText);
        T(sb, desc!, 92, 56, Ui.Ui.PanelText);
    }
}
