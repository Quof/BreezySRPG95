using Srpg95.Data;

namespace Srpg95.Rules;

public enum EffectKind
{
    Damage, Miss, Resisted, Heal, MpHeal, PowerUp, DefUp, MdefUp,
    Poison, Sleep, AttackDown, DefenseDown, CurePoison, WakeUp, Cleanse, ExtraTurn,
    /// <summary>A medicine undid a lowered attack / defense (FUN_0040b188: 「攻撃力が回復した」, 「物理防御が回復した」).</summary>
    AttackRestored, DefenseRestored,
}

/// <summary>
/// What a spell or item does to one unit: for a spell's damage and healing as rolled (FUN_0041ba0c), the unit's HP after
/// it and how far its HP bar moves.
/// </summary>
public readonly record struct Effect(BattleUnit Target, EffectKind Kind, int Amount, bool Killed, int HpAfter = 0, int Moved = 0);

/// <summary>Magic and battle items (help "Magic Editor", "Item Editor", "Battle Algorithm"; FORMAT_NOTES §4, §5).</summary>
public sealed partial class Battlefield
{
    Magic? Spell(int id) => id >= 0 && id < Db.Magic.Length ? Db.Magic[id] : null;

    /// <summary>Spells from the class's skill list whose level the unit has reached (0 ends the list).</summary>
    public IEnumerable<Magic> KnownSpells(BattleUnit u) => KnownSpells(Class(u), u.Level);

    IEnumerable<Magic> KnownSpells(ClassDef c, int level)
    {
        for (int i = 0; i < c.SkillLevels.Length && c.SkillLevels[i] > 0; i++)
            if (c.SkillLevels[i] <= level && Spell(c.SkillIds[i]) is { } m) yield return m;
    }

    public static bool CanCast(BattleUnit u, Magic m) => u.Mp >= m.MpCost;

    /// <summary>Tiles the spell can be aimed at from (x,y): the range grid (centre = caster), or the whole map.</summary>
    public IEnumerable<(int X, int Y)> SpellRange(Magic m, int x, int y) =>
        m.RangeWholeMap ? AllTiles() : GridTiles(m.RangeGrid, x, y);

    /// <summary>Tiles the spell hits when aimed at (x,y): the effect grid around the aim, or the whole map.</summary>
    public IEnumerable<(int X, int Y)> SpellArea(Magic m, int x, int y) =>
        m.EffectWholeMap ? AllTiles() : GridTiles(m.EffectGrid, x, y);

    IEnumerable<(int X, int Y)> AllTiles()
    {
        for (int y = 0; y < Map.Height; y++)
            for (int x = 0; x < Map.Width; x++) yield return (x, y);
    }

    IEnumerable<(int X, int Y)> GridTiles(bool[,] g, int cx, int cy)
    {
        for (int r = 0; r < 21; r++)
            for (int c = 0; c < 21; c++)
                if (g[r, c] && InBounds(cx + c - 10, cy + r - 10)) yield return (cx + c - 10, cy + r - 10);
    }

    /// <summary>
    /// FUN_00411934: whether a spell cast by <paramref name="caster"/> affects <paramref name="t"/>. An unattackable
    /// neutral unit is never affected. Target 0 hits everyone in the area (the caster too, if its tile is in it);
    /// target 1 only the other side for Damage/Degrade and only the caster's side for Raise, sides as the exe counts
    /// them: enemies against everyone else (allies and neutral units are on the same side).
    /// </summary>
    public bool Affects(BattleUnit caster, Magic m, BattleUnit t)
    {
        if (t.Side == Side.Neutral && t.Unattackable) return false;
        if (m.Target != 1) return true;
        bool opposing = (caster.Side == Side.Enemy) != (t.Side == Side.Enemy);
        return m.Type is 0 or 1 ? opposing : !opposing;
    }

    /// <summary>
    /// The units a spell aimed at (aimX, aimY) affects (FUN_00411a48), in the order the exe goes through them: by rows from
    /// the top, each from the left (FUN_0041c8c0). A click casts only when there is one; the aim must be in range, except
    /// for a spell that covers the whole map (FUN_00410050).
    /// </summary>
    public List<BattleUnit> SpellTargets(BattleUnit caster, Magic m, int aimX, int aimY)
    {
        var area = SpellArea(m, aimX, aimY).ToHashSet();
        return Alive.Where(t => area.Contains((t.X, t.Y)) && Affects(caster, m, t)).OrderBy(t => t.Y).ThenBy(t => t.X).ToList();
    }

    bool Resists(BattleUnit t, int effect)
    {
        if (effect is < 0 or > 3) return false;
        if (Class(t).Resist[effect] != 0) return true;
        return Item(t.Accessory) is { Type: ItemType.Accessory } acc && acc.S.AccSafe[effect] != 0;
    }

    /// <summary>FUN_0041ba0c: damage spells roll Skl×2 + accuracy − (dSkl×3 − weight) − terrain; status spells roll accuracy alone.</summary>
    int MagicHit(BattleUnit caster, Magic m, BattleUnit t)
    {
        if (m.Type == 1) return m.Accuracy;
        return Combat.MagicHitChance(Stats(caster).Skl, m, Stats(t).Skl, Item(t.Armor), TerrainAt(t.X, t.Y).DefenseBonus);
    }

    /// <summary>Spell damage (no roll): (Mag + damage, attributes applied) − magic defense − terrain/10, 0–99. No criticals.</summary>
    public int PredictSpellDamage(BattleUnit caster, Magic m, BattleUnit t)
    {
        var arm = Item(t.Armor);
        int power = Combat.ApplyAttributes(Stats(caster).Mag + m.Damage, m.AttackAttr, Class(t).Weakness, arm?.S.ArmDefenseAttr);
        return Combat.MagicDamage(power, Stats(t).MagicDefense, TerrainAt(t.X, t.Y).DefenseBonus);
    }

    /// <summary>
    /// The weakness aid (modern): a damage spell whose attributes hit a weakness of <paramref name="t"/>'s class that its
    /// armour doesn't guard, so that its power goes up by half (<see cref="Combat.Effective"/>).
    /// </summary>
    public bool SpellEffective(Magic m, BattleUnit t) =>
        m.Type == 0 && Combat.Effective(m.AttackAttr, Class(t).Weakness, Item(t.Armor)?.S.ArmDefenseAttr);

    /// <summary>The same for a unit's weapon against <paramref name="def"/> (its attack up by half).</summary>
    public bool WeaponEffective(BattleUnit atk, BattleUnit def) =>
        Item(atk.Weapon) is { } w && Combat.Effective(w.S.WpnAttackAttr, Class(def).Weakness, Item(def.Armor)?.S.ArmDefenseAttr);

    /// <summary>
    /// A spell at (aimX, aimY), rolled when it's cast (FUN_0041c8c0): the caster's TP after it — the cost off, none from a
    /// magic item, which works otherwise the same (FUN_0040fda8 calls the same function) — and an effect for each unit it
    /// affects, in the map's row order (FUN_0041ba0c). EXP (FUN_0041b6ec, FUN_0041ba0c): Damage spells add an attack's
    /// worth for every enemy they hurt (kill or hit); Degrade/Raise give the spell's Exp Value per cast; only a player unit
    /// standing after it gets it. Nothing changes until <see cref="ApplyCost"/>, <see cref="ApplyEffect"/> and
    /// <see cref="ApplyGrowth"/>.
    /// </summary>
    public ExchangeResult RollCast(BattleUnit caster, Magic m, int aimX, int aimY, bool fromItem = false)
    {
        var r = new ExchangeResult { MpAfter = fromItem ? caster.Mp : Math.Max(0, caster.Mp - m.MpCost) };
        int power = Combat.SpellEffect(m, Stats(caster).Mag);
        foreach (var t in SpellTargets(caster, m, aimX, aimY))
            r.Effects.Add(Roll(caster, m, t, power));
        if (caster.Side != Side.Ally) return r;

        // damage spells: EXP summed over every enemy (computer unit) damaged — not neutral units or one's own side —
        // unlike attacks, which take the largest; kills collect rewards; status / raise spells: the spell's Exp Value
        int exp = 0;
        if (m.Type == 0)
            foreach (var e in r.Effects.Where(e => e.Kind == EffectKind.Damage && e.Amount > 0 && e.Target.Side == Side.Enemy))
            {
                exp += Combat.Exp(caster.Level, e.Target.Level, e.Killed);
                if (e.Killed) CollectReward(r, e.Target);
            }
        else exp = m.ExpValue;
        if (exp > 0) r.ExpGained[caster] = exp;
        return r;
    }

    /// <summary>
    /// What a spell would do to one unit, without rolling (the battle forecast, a modern aid): <see cref="Roll"/>'s effect
    /// for a hit — damage, healing as far as the HP goes, a status, a buff's size — and the chance it lands (0 resisted).
    /// </summary>
    public (EffectKind Kind, int Amount, int Hit) SpellForecast(BattleUnit caster, Magic m, BattleUnit t)
    {
        int power = Combat.SpellEffect(m, Stats(caster).Mag);
        switch (m.Type)
        {
            case 0: return (EffectKind.Damage, PredictSpellDamage(caster, m, t), Math.Clamp(MagicHit(caster, m, t), 0, 100));
            case 1:
                if (Resists(t, m.DegradeEffect)) return (EffectKind.Resisted, 0, 0);
                int hit = Math.Clamp(MagicHit(caster, m, t), 0, 100);
                return m.DegradeEffect switch
                {
                    0 => (EffectKind.Poison, 0, hit),
                    1 => (EffectKind.Sleep, 0, hit),
                    2 => (EffectKind.AttackDown, power, hit),
                    _ => (EffectKind.DefenseDown, power, hit),
                };
            default:
                return m.RaiseEffect switch
                {
                    0 => (EffectKind.Heal, Math.Max(0, Math.Min(power, t.MaxHp - t.Hp)), 100),
                    1 => (EffectKind.PowerUp, power, 100),
                    2 => (EffectKind.DefUp, power, 100),
                    3 => (EffectKind.CurePoison, 0, 100),
                    4 => (EffectKind.Cleanse, 0, 100),
                    _ => (EffectKind.ExtraTurn, 0, 100),
                };
        }
    }

    /// <summary>One unit's share of a spell, rolled (FUN_0041ba0c).</summary>
    Effect Roll(BattleUnit caster, Magic m, BattleUnit t, int power)
    {
        switch (m.Type)
        {
            case 0:
            {
                if (Rng.Next(100) >= MagicHit(caster, m, t)) return new Effect(t, EffectKind.Miss, 0, false, t.Hp);
                int dmg = PredictSpellDamage(caster, m, t), moved = Math.Min(dmg, t.Hp);
                return new Effect(t, EffectKind.Damage, dmg, t.Hp - moved == 0, t.Hp - moved, moved);
            }
            case 1:
                if (Resists(t, m.DegradeEffect)) return new Effect(t, EffectKind.Resisted, 0, false);
                if (Rng.Next(100) >= MagicHit(caster, m, t)) return new Effect(t, EffectKind.Miss, 0, false);
                return m.DegradeEffect switch
                {
                    0 => new Effect(t, EffectKind.Poison, 0, false),
                    1 => new Effect(t, EffectKind.Sleep, 0, false),
                    2 => new Effect(t, EffectKind.AttackDown, power, false),
                    _ => new Effect(t, EffectKind.DefenseDown, power, false),
                };
            default:
                switch (m.RaiseEffect)
                {
                    case 0:
                        int restored = Math.Max(0, Math.Min(power, t.MaxHp - t.Hp));
                        return new Effect(t, EffectKind.Heal, restored, false, t.Hp + restored, restored);
                    case 1: return new Effect(t, EffectKind.PowerUp, power, false);
                    case 2: return new Effect(t, EffectKind.DefUp, power, false);
                    case 3: return new Effect(t, EffectKind.CurePoison, 0, false);
                    case 4: return new Effect(t, EffectKind.Cleanse, 0, false);
                    default: return new Effect(t, EffectKind.ExtraTurn, 0, false);
                }
        }
    }

    /// <summary>The caster's TP after the spell, at the end of its cast step (FUN_00419574).</summary>
    public static void ApplyCost(BattleUnit caster, ExchangeResult r)
    {
        if (r.MpAfter is int mp) caster.Mp = mp;
    }

    /// <summary>
    /// One unit's share of a spell, at the end of its step in the battle scene (FUN_00419574): a damage spell sets the HP
    /// rolled for it (a miss too), as healing does; poison and sleep last 3 of the unit's turns (asleep for 2, acting again
    /// on the 3rd; counted down at its phase start); buffs don't stack — a down only replaces a smaller one, an up a lower
    /// one; a cure, a cleanse, or another action for anyone but the caster.
    /// </summary>
    public void ApplyEffect(BattleUnit caster, Magic m, Effect e)
    {
        var t = e.Target;
        switch (e.Kind)
        {
            case EffectKind.Damage or EffectKind.Heal:
            case EffectKind.Miss when m.Type == 0:
                t.Hp = e.HpAfter;
                if (t.Hp == 0 && !t.Dead) { t.Dead = true; t.KilledBy = caster.Unit; }
                break;
            case EffectKind.Poison: t.PoisonTurns = 3; break;
            case EffectKind.Sleep: t.SleepTurns = 3; break;
            case EffectKind.AttackDown: t.PowerUp = Math.Min(t.PowerUp, -e.Amount); break;
            case EffectKind.DefenseDown: t.DefUp = Math.Min(t.DefUp, -e.Amount); break;
            case EffectKind.PowerUp: t.PowerUp = Math.Max(t.PowerUp, e.Amount); break;
            case EffectKind.DefUp: t.DefUp = Math.Max(t.DefUp, e.Amount); break;
            case EffectKind.CurePoison: t.PoisonTurns = 0; break;
            case EffectKind.Cleanse: Cleanse(t); break;
            case EffectKind.ExtraTurn:
                if (t != caster) t.Acted = false;
                caster.NoMoreTurns = true;   // +0x30: the giver gets none back this phase (FUN_0041ba0c)
                break;
        }
    }

    /// <summary>A spell rolled and applied at once (the battle map applies it step by step in its scene).</summary>
    public ExchangeResult Cast(BattleUnit caster, Magic m, int aimX, int aimY, bool fromItem = false)
    {
        var r = RollCast(caster, m, aimX, aimY, fromItem);
        ApplyCost(caster, r);
        foreach (var e in r.Effects) ApplyEffect(caster, m, e);
        ApplyGrowth(r);
        caster.Acted = true;
        return r;
    }

    static void Cleanse(BattleUnit t)
    {
        t.PoisonTurns = 0;
        t.SleepTurns = 0;
        if (t.PowerUp < 0) t.PowerUp = 0;
        if (t.DefUp < 0) t.DefUp = 0;
    }

    // ---------------- items ----------------

    /// <summary>
    /// The battle item list (FUN_0040f650's list): medicine, stat-up and magic items the class may use, and class-change
    /// items whose from → to pairs include the class once the unit has the item's level. Stat-up and class-change
    /// items work on the user itself (FUN_0040b074).
    /// </summary>
    public bool CanUseInBattle(BattleUnit u, Item it) => it.Type switch
    {
        ItemType.Medicine or ItemType.StatUp => UnitChanges.Usable(it, u.ClassId),
        ItemType.Magic => UnitChanges.Usable(it, u.ClassId) && Spell(it.S.MagicSpell) is not null,
        ItemType.ClassChange => u.Level >= it.S.ClassChangeLevel && UnitChanges.ClassChangeTarget(it, u.ClassId) >= 0,
        _ => false,
    };

    public Magic? ItemSpell(Item it) => it.Type == ItemType.Magic ? Spell(it.S.MagicSpell) : null;

    /// <summary>The tiles a medicine reaches from its user (FUN_00411ba0): the distances its range type allows.</summary>
    public IEnumerable<(int X, int Y)> MedicineRange(BattleUnit user, Item it)
    {
        var (min, max) = it.S.MedRange >= 0 && it.S.MedRange < Data.Item.MedicineRanges.Length ? Data.Item.MedicineRanges[it.S.MedRange] : (0, 0);
        for (int y = user.Y - max; y <= user.Y + max; y++)
            for (int x = user.X - max; x <= user.X + max; x++)
                if (Dist(user.X, user.Y, x, y) is var d && d >= min && d <= max && InBounds(x, y)) yield return (x, y);
    }

    /// <summary>
    /// Units a medicine can be given to (FUN_00411c90): in its reach and on the user's side — the exe's sides, the player's
    /// units and neutral ones together — but not a neutral unit that can't be attacked.
    /// </summary>
    public IEnumerable<BattleUnit> MedicineTargets(BattleUnit user, Item it)
    {
        var reach = MedicineRange(user, it).ToHashSet();
        return Alive.Where(t => reach.Contains((t.X, t.Y)) && Friendly(user, t) && !(t.Side == Side.Neutral && t.Unattackable));
    }

    /// <summary>
    /// A medicine on a unit (FUN_0040b188), its effects in the order of the original's messages: HP / TP back by the
    /// item's amounts up to the maximum (the effect, like the message, carries the item's own amount); the defense
    /// buffs raised to the item's values if lower; poison and sleep cured; a lowered attack, then defense, undone.
    /// </summary>
    public ExchangeResult UseMedicine(BattleUnit user, Item it, BattleUnit t)
    {
        var r = new ExchangeResult();
        var s = it.S;
        if (s.MedHp > 0) { t.Hp = Math.Min(t.MaxHp, t.Hp + s.MedHp); r.Effects.Add(new Effect(t, EffectKind.Heal, s.MedHp, false)); }
        if (s.MedMp > 0) { t.Mp = Math.Min(t.MaxMp, t.Mp + s.MedMp); r.Effects.Add(new Effect(t, EffectKind.MpHeal, s.MedMp, false)); }
        if (s.MedDefUp > 0) { t.DefUp = Math.Max(t.DefUp, s.MedDefUp); r.Effects.Add(new Effect(t, EffectKind.DefUp, s.MedDefUp, false)); }
        if (s.MedMdefUp > 0) { t.MdefUp = Math.Max(t.MdefUp, s.MedMdefUp); r.Effects.Add(new Effect(t, EffectKind.MdefUp, s.MedMdefUp, false)); }
        if (s.MedCure[0] != 0 && t.PoisonTurns > 0) { t.PoisonTurns = 0; r.Effects.Add(new Effect(t, EffectKind.CurePoison, 0, false)); }
        if (s.MedCure[1] != 0 && t.SleepTurns > 0) { t.SleepTurns = 0; r.Effects.Add(new Effect(t, EffectKind.WakeUp, 0, false)); }
        if (s.MedCure[2] != 0 && t.PowerUp < 0) { t.PowerUp = 0; r.Effects.Add(new Effect(t, EffectKind.AttackRestored, 0, false)); }
        if (s.MedCure[3] != 0 && t.DefUp < 0) { t.DefUp = 0; r.Effects.Add(new Effect(t, EffectKind.DefenseRestored, 0, false)); }
        user.Acted = true;
        return r;
    }
}
