using Srpg95.Data;
using Srpg95.Rules;

namespace Srpg95.Data.Tests;

/// <summary>Battle-rule sanity checks on real map 1 (ルーティ and ジュナ vs ナス).</summary>
public class RulesTests
{
    static string? Game()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "えるまに", "SRPGEXEC.EXE"))) return Path.Combine(d.FullName, "えるまに");
        return null;
    }

    static Battlefield Map1(out Database db)
    {
        var g = new GameFolder(Game()!);
        db = Database.Load(g);
        var map = MapData.Load(g.MapPath(0));
        var f = new Battlefield(map, db, g.Chipset(map.Tileset)) { Rng = new Random(1) };
        foreach (var o in map.Objects.Where(o => o.InUse && o.Type != ObjectType.Other))
        {
            var u = db.Units[o.Unit];
            f.Units.Add(new BattleUnit
            {
                Unit = o.Unit, Side = o.Type == ObjectType.Ally ? Side.Ally : Side.Enemy, MapObject = o.Index,
                ClassId = u.ClassId, Level = u.Level, Base = (int[])u.BaseStats.Clone(), Growth = u.GrowthPct,
                Hp = u.BaseStats[0], Mp = u.BaseStats[1], Weapon = u.Weapon, Armor = u.Armor, Accessory = u.Accessory,
                X = o.X, Y = o.Y, MovePatterns = o.MovePatterns, Name = u.Name,
            });
        }
        return f;
    }

    [Fact]
    public void Movement_respects_terrain_and_budget()
    {
        if (Game() is null) return;
        var f = Map1(out _);
        var juna = f.Units.Single(u => u.Unit == 243);
        var reach = f.Reachable(juna);
        int move = f.Stats(juna).Movement;
        Assert.Equal(6, move);                                   // class 1 movement 6 (save record [19])
        Assert.All(reach.Values, v => Assert.InRange(v.Cost, 0, move));
        Assert.Contains((7, 11), reach.Keys);                    // T16: ジュナ moved (7,12) -> (7,11)
        Assert.DoesNotContain((8, 12), reach.Keys);              // occupied by ルーティ
        // the river row (y 6) is only crossable at the bridge: nothing reachable north of it in one move
        Assert.DoesNotContain(reach.Keys, k => k.Y < 6);
    }

    [Fact]
    public void Status_values_match_ジュナ_on_map_1()
    {
        if (Game() is null) return;
        var f = Map1(out _);
        var s = f.Stats(f.Units.Single(u => u.Unit == 243));
        // her status window (T17 screenshot): 攻撃力 11, 攻撃精度 17, 物理防御 3, 特殊防御 2
        Assert.Equal((11, 17, 3, 2), (s.Attack, s.Speed, s.Defense, s.MagicDefense));
    }

    [Fact]
    public void Exchange_and_ai_produce_consistent_state()
    {
        if (Game() is null) return;
        var f = Map1(out _);
        var juna = f.Units.Single(u => u.Unit == 243);
        var enemy = f.Units.First(u => u.Side == Side.Enemy && u.Unit == 4);
        enemy.X = juna.X; enemy.Y = juna.Y - 1;
        var r = f.Attack(juna, enemy);
        Assert.NotEmpty(r.Strikes);
        Assert.True(juna.Acted);
        Assert.All(r.Strikes, s => Assert.True(s.Damage >= 0));
        var plan = EnemyAi.Plan(f, f.Units.Single(u => u.Unit == 3), new bool[500]);
        Assert.True(f.Reachable(plan.Unit).ContainsKey(plan.Destination));
    }

    [Fact]
    public void An_attack_is_rolled_first_and_lands_as_rolled()
    {
        if (Game() is null) return;
        var f = Map1(out _);
        var juna = f.Units.Single(u => u.Unit == 243);
        var enemy = f.Units.First(u => u.Unit == 4);
        enemy.X = juna.X; enemy.Y = juna.Y - 1;
        int hp = enemy.Hp, junaHp = juna.Hp, total = juna.Level * 100 + juna.Exp;
        // rolled when chosen (FUN_0041b4b0), changing nobody; each strike on the HP the earlier ones left
        var r = f.RollAttack(juna, enemy);
        Assert.Equal((hp, junaHp, total, false), (enemy.Hp, juna.Hp, juna.Level * 100 + juna.Exp, juna.Acted));
        Assert.Equal(hp - r.Strikes[0].Moved, r.Strikes[0].HpAfter);
        // whatever happens before it lands (a "before" event), the target ends on the HP rolled for it
        enemy.Hp = 1;
        foreach (var s in r.Strikes) f.ApplyStrike(s);
        Assert.Equal(r.Strikes.Last(s => s.Target == enemy).HpAfter, enemy.Hp);
        // the EXP only after the scene
        Assert.Equal(total, juna.Level * 100 + juna.Exp);
        f.ApplyGrowth(r);
        Assert.Equal(total + (juna.Hp > 0 && r.ExpGained.TryGetValue(juna, out var gained) ? gained : 0), juna.Level * 100 + juna.Exp);
    }

    [Fact]
    public void A_spell_is_rolled_first_and_lands_effect_by_effect()
    {
        if (Game() is null) return;
        var f = Map1(out var db);
        var rutee = f.Units.Single(u => u.Unit == 242);
        rutee.Level = 12;
        rutee.Mp = 20;
        var ion = db.Magic[45];
        var enemy = f.Units.First(u => u.Unit == 4);
        (enemy.X, enemy.Y) = f.SpellRange(ion, rutee.X, rutee.Y).First(a => f.UnitAt(a.X, a.Y) is null);
        int hp = enemy.Hp;
        var r = f.RollCast(rutee, ion, enemy.X, enemy.Y);
        Assert.Equal((20, hp), (rutee.Mp, enemy.Hp));
        Assert.Equal(20 - ion.MpCost, r.MpAfter);
        Battlefield.ApplyCost(rutee, r);
        var e = Assert.Single(r.Effects);
        f.ApplyEffect(rutee, ion, e);
        Assert.Equal((20 - ion.MpCost, e.HpAfter), (rutee.Mp, enemy.Hp));   // a miss leaves the HP it was rolled on
    }

    [Fact]
    public void A_spell_lands_by_rows_then_columns()
    {
        if (Game() is null) return;
        var f = Map1(out var db);
        var rutee = f.Units.Single(u => u.Unit == 242);
        var juna = f.Units.Single(u => u.Unit == 243);
        var minion = f.Units.First(u => u.Unit == 4);   // listed after ジュナ, but a row above her
        Assert.True(f.Units.IndexOf(minion) > f.Units.IndexOf(juna));
        (minion.X, minion.Y) = (rutee.X, rutee.Y - 1);
        var hit = f.SpellTargets(rutee, db.Magic[4], rutee.X, rutee.Y);   // ルーチェ＝フォルツァ: everyone around her
        Assert.Contains(juna, hit);
        Assert.Same(minion, hit[0]);
        Assert.Equal(hit.OrderBy(t => t.Y).ThenBy(t => t.X), hit);
    }

    [Fact]
    public void Spells_are_learned_by_class_level()
    {
        if (Game() is null) return;
        var f = Map1(out _);
        var rutee = f.Units.Single(u => u.Unit == 242);   // class 0: ルーチェ＝フォルツァ at 8, イオンビーム at 12
        Assert.Empty(f.KnownSpells(rutee));
        rutee.Level = 12;
        Assert.Equal(new[] { 4, 45 }, f.KnownSpells(rutee).Select(m => m.Index));
        // level 11 -> 12 teaches イオンビーム
        rutee.Level = 11;
        rutee.Exp = 99;
        var enemy = f.Units.First(u => u.Unit == 4);
        enemy.X = rutee.X; enemy.Y = rutee.Y - 1;
        enemy.SleepTurns = 3;                      // asleep: always hit, so EXP (and the level-up) is certain
        var ex = f.Attack(rutee, enemy);
        Assert.Equal(12, rutee.Level);
        Assert.Equal("イオンビーム", Assert.Single(ex.Learned[rutee]).Name);
    }

    [Fact]
    public void Weakness_aid_marks_what_hits_an_unguarded_weakness()
    {
        if (Game() is null) return;
        var f = Map1(out var db);
        var nasu = f.Units.First(u => u.Unit == 4);   // ナスの分裂君: class ナス, weak to B, D and F
        var eraser = db.Magic[54];                     // イレイザー: damage, attribute B
        var fireball = db.Magic[51];                   // ファイアボール: damage, attribute C
        Assert.True(f.SpellEffective(eraser, nasu));
        Assert.False(f.SpellEffective(fireball, nasu));
        // only damage spells: attributes do nothing for the others
        if (db.Magic.FirstOrDefault(m => m.Type != 0 && m.AttackAttr[1] != 0) is { } support) Assert.False(f.SpellEffective(support, nasu));
        // armour that guards the attribute takes the weakness away, as it takes away the extra damage
        if (db.Items.FirstOrDefault(i => i.Type == ItemType.Armor && i.S.ArmDefenseAttr[1] != 0) is { } guard)
        {
            nasu.Armor = guard.Index;
            Assert.False(f.SpellEffective(eraser, nasu));
        }
    }

    [Fact]
    public void Damage_spell_costs_mp_hits_the_area_and_gives_exp()
    {
        if (Game() is null) return;
        var f = Map1(out var db);
        var rutee = f.Units.Single(u => u.Unit == 242);
        rutee.Level = 12;
        rutee.Mp = 20;
        var ion = db.Magic[45];
        var enemy = f.Units.First(u => u.Unit == 4);
        // put the enemy on a tile the spell can be aimed at
        var aim = f.SpellRange(ion, rutee.X, rutee.Y).First(a => f.UnitAt(a.X, a.Y) is null);
        enemy.X = aim.X; enemy.Y = aim.Y;
        Assert.Contains(enemy, f.SpellTargets(rutee, ion, aim.X, aim.Y));
        int hpBefore = enemy.Hp;
        var r = f.Cast(rutee, ion, aim.X, aim.Y);
        Assert.Equal(20 - ion.MpCost, rutee.Mp);
        var e = Assert.Single(r.Effects);
        Assert.Same(enemy, e.Target);
        if (e.Kind == EffectKind.Damage)
        {
            Assert.Equal(Math.Min(hpBefore, f.PredictSpellDamage(rutee, ion, enemy)), hpBefore - enemy.Hp);
            Assert.True(r.ExpGained.ContainsKey(rutee));
        }
        Assert.True(rutee.Acted);
        Assert.DoesNotContain(r.Effects, x => x.Target.Side == Side.Ally);   // イオンビーム is "enemies only"
    }

    [Fact]
    public void Sleep_skips_two_turns_and_wakes_on_the_third()
    {
        if (Game() is null) return;
        var f = Map1(out _);
        var enemy = f.Units.First(u => u.Unit == 4);
        enemy.SleepTurns = 3;                      // what a landed Sleep spell sets during the ally phase
        var acted = new List<bool>();
        for (int turn = 0; turn < 3; turn++)
        {
            f.NextPhase();                         // enemy phase starts
            acted.Add(enemy.SleepTurns > 0);       // still asleep at its phase start = can't move (FUN_00410b90)
            f.NextPhase();                         // back to the allies
        }
        Assert.Equal(new[] { true, true, false }, acted);
    }

    [Fact]
    public void Potion_heals_up_to_max_hp_within_range()
    {
        if (Game() is null) return;
        var f = Map1(out var db);
        var juna = f.Units.Single(u => u.Unit == 243);
        var rutee = f.Units.Single(u => u.Unit == 242);
        var potion = db.Items[40];                 // ポーション: HP 8, range 1
        Assert.True(f.CanUseInBattle(juna, potion));
        Assert.Contains(rutee, f.MedicineTargets(juna, potion));   // (7,12) and (8,12) are adjacent
        rutee.Hp = rutee.MaxHp - 3;
        var r = f.UseMedicine(juna, potion, rutee);
        Assert.Equal(rutee.MaxHp, rutee.Hp);
        Assert.Equal(potion.S.MedHp, Assert.Single(r.Effects).Amount);   // the message shows the item's amount (FUN_0040b188)
        Assert.True(juna.Acted);
    }

    [Fact]
    public void Enemy_casts_a_spell_when_it_beats_its_weapon()
    {
        if (Game() is null) return;
        var f = Map1(out _);
        var nasu = f.Units.Single(u => u.Unit == 3);   // class 2: ナスビーム at level 10
        nasu.Level = 10;
        nasu.Mp = 30;
        nasu.Weapon = -1;                              // bare-handed, so the spell clearly wins
        foreach (var a in f.Units.Where(u => u.Side == Side.Ally)) { a.X = nasu.X; a.Y = nasu.Y + 3 + (a.Unit - 242); }
        var plan = EnemyAi.Plan(f, nasu, new bool[500]);
        var m = Assert.IsType<Magic>(plan.Spell);
        Assert.Equal("ナスビーム", m.Name);
        Assert.NotEmpty(f.SpellTargets(nasu, m, plan.Aim.X, plan.Aim.Y));
    }

    [Fact]
    public void An_enemys_threat_is_where_it_can_stop_and_what_it_hits_from_there()
    {
        if (Game() is null) return;
        var f = Map1(out _);
        var nasu = f.Units.Single(u => u.Unit == 3);   // class 2: ナスビーム at level 10
        var off = new bool[500];
        nasu.MovePatterns[0] = new MovePattern(-1, 0, 0, 0, -1);   // rush
        nasu.Mp = 0;                                                // no spell yet: the weapon alone
        var (stops, hits) = EnemyAi.Threat(f, nasu, off);
        Assert.Equal(f.Reachable(nasu).Keys.ToHashSet(), stops);   // the clicked enemy's movement range, as the original shows it
        var st = f.Stats(nasu);
        var ring = stops.SelectMany(s => Enumerable.Range(0, f.Map.Width * f.Map.Height).Select(i => (X: i % f.Map.Width, Y: i / f.Map.Width))
            .Where(t => Math.Abs(t.X - s.X) + Math.Abs(t.Y - s.Y) is var d && d >= st.RangeMin && d <= st.RangeMax)).ToHashSet();
        Assert.Equal(ring, hits);
        // a harmful spell it has the TP for reaches further than the weapon
        nasu.Level = 10;
        nasu.Mp = 30;
        Assert.True(EnemyAi.Threat(f, nasu, off).Hits.IsProperSupersetOf(hits));
        // the None pattern stays where it is and hits only from there; asleep through its coming phase, nothing
        nasu.Mp = 0;
        nasu.MovePatterns[0] = new MovePattern(-1, 3, 0, 0, -1);
        var (still, near) = EnemyAi.Threat(f, nasu, off);
        Assert.Equal(new[] { (nasu.X, nasu.Y) }, still);
        Assert.Equal(EnemyAi.Hits(f, nasu, still), near);
        nasu.SleepTurns = 2;
        Assert.Empty(EnemyAi.Threat(f, nasu, off).Stops);
    }

    [Fact]
    public void Paths_through_an_ally_are_whole_and_never_end_on_a_unit()
    {
        if (Game() is null) return;
        var f = Map1(out _);
        var juna = f.Units.Single(u => u.Unit == 243);
        var rutee = f.Units.Single(u => u.Unit == 242);
        (juna.X, juna.Y) = (7, 9);       // the user's screenshot
        (rutee.X, rutee.Y) = (8, 10);
        var reach = f.Reachable(rutee);
        Assert.DoesNotContain((7, 9), reach.Keys);   // can't stop on ジュナ
        foreach (var dest in reach.Keys)
        {
            var path = Battlefield.PathTo(reach, dest);
            var at = (rutee.X, rutee.Y);
            foreach (var step in path)
            {
                Assert.Equal(1, Math.Abs(step.X - at.Item1) + Math.Abs(step.Y - at.Item2));   // one tile at a time
                at = step;
            }
            Assert.Equal(dest, at);
        }
        Assert.Contains((7, 7), reach.Keys);   // reached by walking through ジュナ
    }

    [Fact]
    public void Ai_attacks_the_unit_it_can_kill_and_walks_whole_paths()
    {
        if (Game() is null) return;
        var f = Map1(out _);
        var juna = f.Units.Single(u => u.Unit == 243);
        var rutee = f.Units.Single(u => u.Unit == 242);
        var minion = f.Units.First(u => u.Side == Side.Enemy && u.Unit != 3);
        foreach (var e in f.Units.Where(u => u.Side == Side.Enemy && u != minion)) e.Hidden = true;
        // both allies two tiles away; ルーティ can be killed, so she's worth 100 more (FUN_004162c4)
        (minion.X, minion.Y) = (8, 10);
        (juna.X, juna.Y) = (6, 10);
        (rutee.X, rutee.Y) = (10, 10);
        rutee.Hp = 1;
        var plan = EnemyAi.Plan(f, minion, new bool[500]);
        Assert.Same(rutee, plan.Target);
        Assert.True(f.InRange(minion, plan.Destination.X, plan.Destination.Y, rutee));
        Assert.True(f.Reachable(minion).ContainsKey(plan.Destination));
        var at = (minion.X, minion.Y);
        foreach (var step in plan.Path) { Assert.Equal(1, Math.Abs(step.X - at.Item1) + Math.Abs(step.Y - at.Item2)); at = step; }
        Assert.Equal(plan.Destination, at);
    }

    [Fact]
    public void Ai_rushes_toward_a_target_out_of_reach()
    {
        if (Game() is null) return;
        var f = Map1(out _);
        var minion = f.Units.First(u => u.Side == Side.Enemy && u.Unit != 3 && u.MovePatterns.FirstOrDefault().Action == 0);
        foreach (var e in f.Units.Where(u => u.Side == Side.Enemy && u != minion)) e.Hidden = true;
        int before = f.Units.Where(u => u.Side == Side.Ally).Min(a => Math.Abs(minion.X - a.X) + Math.Abs(minion.Y - a.Y));
        var plan = EnemyAi.Plan(f, minion, new bool[500]);
        if (plan.Target is not null) return;   // already in reach on this map: nothing to check
        Assert.NotEmpty(plan.Path);
        Assert.True(f.Reachable(minion).ContainsKey(plan.Destination));
        Assert.Null(f.UnitAt(plan.Destination.X, plan.Destination.Y));
        var nearest = f.Units.Where(u => u.Side == Side.Ally).Min(a => Math.Abs(plan.Destination.X - a.X) + Math.Abs(plan.Destination.Y - a.Y));
        Assert.True(nearest < before);
    }

    [Fact]
    public void Enemy_phases_never_stack_units()
    {
        Assert.NotNull(Game());
        for (int seed = 0; seed < 20; seed++)
        {
            var f = Map1(out _);
            f.Rng = new Random(seed);
            for (int turn = 0; turn < 8; turn++)
                foreach (var e in f.Alive.Where(u => u.Side == Side.Enemy).OrderBy(u => u.MapObject).ToList())
                {
                    if (e.Dead) continue;
                    var plan = EnemyAi.Plan(f, e, new bool[500]);
                    (e.X, e.Y) = plan.Destination;
                    var stacked = f.Alive.GroupBy(u => (u.X, u.Y)).FirstOrDefault(g => g.Count() > 1);
                    Assert.True(stacked is null, $"seed {seed} turn {turn}: {string.Join(", ", stacked?.Select(u => u.Name) ?? [])} on {stacked?.Key}");
                    if (plan.Target is { Dead: false } t && f.InRange(e, e.X, e.Y, t)) f.Attack(e, t);
                }
        }
    }

    [Fact]
    public void Ai_spell_scores_follow_the_exe()
    {
        if (Game() is null) return;
        var f = Map1(out var db);
        var nasu = f.Units.Single(u => u.Unit == 3);
        var juna = f.Units.Single(u => u.Unit == 243);
        var heal = db.Magic.First(m => m.Type == 2 && m.RaiseEffect == 0 && !m.EffectWholeMap && m.Value > 0);
        (juna.X, juna.Y) = (nasu.X, nasu.Y + 1);
        // healing an enemy of the caster's own side: min(power, missing) × 10 + 50; a player unit in the area −150
        var minion = f.Units.First(u => u.Side == Side.Enemy && u != nasu);
        (minion.X, minion.Y) = (nasu.X + 5, nasu.Y);
        minion.Hp = 1;
        int power = heal.Value + heal.Relation * f.Stats(nasu).Mag / 10;
        int expected = Math.Min(power, minion.MaxHp - 1) * 10 + 50;
        var area = f.SpellArea(heal, minion.X, minion.Y).ToHashSet();
        int others = f.Alive.Count(u => u != minion && area.Contains((u.X, u.Y)) && u.Side != Side.Enemy);
        Assert.Equal(Math.Max(0, expected - 150 * others), EnemyAi.SpellScore(f, nasu, heal, (minion.X, minion.Y)));
    }

    /// <summary>A grid of the given (row, column) cells, [10, 10] being the centre.</summary>
    static bool[,] Grid(params (int Row, int Col)[] cells)
    {
        var g = new bool[21, 21];
        foreach (var (r, c) in cells) g[r, c] = true;
        return g;
    }

    [Fact]
    public void Ai_damage_spell_scores_count_only_the_units_it_affects()
    {
        if (Game() is null) return;
        var f = Map1(out var db);
        var nasu = f.Units.Single(u => u.Unit == 3);
        var juna = f.Units.Single(u => u.Unit == 243);
        var minion = f.Units.First(u => u.Side == Side.Enemy && u != nasu);
        foreach (var o in f.Units.Where(u => u != nasu && u != juna && u != minion)) o.Hidden = true;
        (nasu.X, nasu.Y) = (5, 3);
        (juna.X, juna.Y) = (5, 6);
        (minion.X, minion.Y) = (6, 6);
        // a damage spell over a cross of five tiles, at the other side only (target 1): the minion beside ジュナ is in
        // the area but isn't affected (FUN_00411934 doesn't mark its tile), so it costs nothing (FUN_0041664c)
        var beam = db.Magic.First(m => m.Type == 0 && m.Target == 1) with
            { Damage = 30, EffectWholeMap = false, EffectGrid = Grid((10, 10), (9, 10), (11, 10), (10, 9), (10, 11)) };
        var aim = (juna.X, juna.Y);
        int one = (beam.Damage + f.Stats(nasu).Mag - f.Stats(juna).MagicDefense) * 5 + 80;
        Assert.True(one > 150);
        Assert.Equal(one, EnemyAi.SpellScore(f, nasu, beam, aim));
        // aimed at everyone in the area (target 0), it would hit the minion as well, and that costs 150
        Assert.Equal(one - 150, EnemyAi.SpellScore(f, nasu, beam with { Target = 0 }, aim));
    }

    [Fact]
    public void Ai_scores_a_spell_over_the_whole_map_at_every_aim()
    {
        if (Game() is null) return;
        var f = Map1(out var db);
        var nasu = f.Units.Single(u => u.Unit == 3);
        foreach (var o in f.Units.Where(u => u != nasu)) o.Hidden = true;
        nasu.MovePatterns[0] = new MovePattern(-1, 0, 0, 0, -1);   // rush
        // its only spell: a heal over the whole map, aimed two tiles away. It counts the caster only where the aim is
        // in range from where the caster stands, so most aims are worth nothing and the first one scanned is among them
        var heal = db.Magic.First(m => m.Type == 2 && m.RaiseEffect == 0) with
        {
            Index = 0, MpCost = 1, Value = 50, Relation = 0, RangeWholeMap = false, EffectWholeMap = true,
            RangeGrid = Grid((8, 10), (12, 10), (10, 8), (10, 12)),
        };
        db.Magic[0] = heal;
        var cls = f.Class(nasu);
        Array.Clear(cls.SkillLevels);
        Array.Clear(cls.SkillIds);
        cls.SkillLevels[0] = 1;
        nasu.Mp = 10;
        nasu.Hp = 1;
        var first = f.Reachable(nasu).Keys.SelectMany(s => f.SpellRange(heal, s.X, s.Y)).OrderBy(a => a.Y).ThenBy(a => a.X).First();
        Assert.Equal(0, EnemyAi.SpellScore(f, nasu, heal, first));
        var plan = EnemyAi.Plan(f, nasu, new bool[500]);
        Assert.Same(heal, plan.Spell);
        // cast from where it stands, the cheapest tile with the aim in range (FUN_00416c44)
        Assert.Equal((nasu.X, nasu.Y), plan.Destination);
        Assert.Empty(plan.Path);
        // the whole map is the map's tiles: a wounded unit of its side standing past the edge isn't counted
        var near = f.SpellRange(heal, nasu.X, nasu.Y).First();
        int alone = EnemyAi.SpellScore(f, nasu, heal, near);
        Assert.True(alone > 0);
        var away = f.Units.Single(u => u.Unit == 243);
        (away.Hidden, away.Side, away.X, away.Y, away.Hp) = (false, Side.Enemy, f.Map.Width, 0, 1);
        Assert.Equal(alone, EnemyAi.SpellScore(f, nasu, heal, near));
    }

    [Fact]
    public void Ai_gives_another_action_only_to_a_unit_that_can_use_it()
    {
        if (Game() is null) return;
        var f = Map1(out var db);
        var nasu = f.Units.Single(u => u.Unit == 3);
        var minion = f.Units.First(u => u.Side == Side.Enemy && u != nasu);
        var juna = f.Units.Single(u => u.Unit == 243);
        foreach (var o in f.Units.Where(u => u != nasu && u != minion && u != juna)) o.Hidden = true;
        (nasu.X, nasu.Y) = (3, 6);
        (minion.X, minion.Y) = (5, 6);
        (juna.X, juna.Y) = (8, 8);
        var haste = db.Magic.First(m => m.Type == 2 && m.RaiseEffect == 5) with
            { EffectWholeMap = false, RangeWholeMap = false, EffectGrid = Grid((9, 10), (10, 10), (11, 10)), RangeGrid = Grid((10, 12)) };
        var aim = (minion.X, minion.Y);
        Assert.Equal(0, EnemyAi.SpellScore(f, nasu, haste, aim));            // it hasn't had its turn yet
        minion.Acted = true;
        Assert.InRange(EnemyAi.SpellScore(f, nasu, haste, aim), 95, 114);    // rand(20) + 95
        minion.NoMoreTurns = true;                                           // it sat its turn out, or gave one away (+0x30)
        Assert.Equal(0, EnemyAi.SpellScore(f, nasu, haste, aim));
        minion.NoMoreTurns = false;
        // that replaces what the units before it added up, in the exe's order (rows from the top, each from the left): a
        // player unit above it in the area costs nothing, one below it 150
        (juna.X, juna.Y) = (5, 5);
        Assert.InRange(EnemyAi.SpellScore(f, nasu, haste, aim), 95, 114);
        (juna.X, juna.Y) = (5, 7);
        Assert.Equal(0, EnemyAi.SpellScore(f, nasu, haste, aim));
        // the caster gives one away: the minion acts again, the caster gets none back until the next phase
        (juna.X, juna.Y) = (8, 8);
        nasu.Mp = haste.MpCost;
        f.Cast(nasu, haste, aim.X, aim.Y);
        Assert.False(minion.Acted);
        Assert.True(nasu.NoMoreTurns);
        f.NextPhase();
        Assert.False(nasu.NoMoreTurns);
    }

    [Fact]
    public void Ai_units_with_somewhere_to_go_take_their_turn_even_without_a_step()
    {
        if (Game() is null) return;
        var f = Map1(out _);
        var minion = f.Units.First(u => u.Side == Side.Enemy && u.Unit != 3 && u.MovePatterns.Length > 0);
        foreach (var o in f.Units.Where(u => u != minion)) o.Hidden = true;
        var off = new bool[500];
        // Specific, already on its tile: a turn without a step (FUN_0041589c returns 1): stop-on marks, contacts, the checkpoint
        minion.MovePatterns[0] = new MovePattern(-1, 2, (short)minion.X, (short)minion.Y, -1);
        var plan = EnemyAi.Plan(f, minion, off);
        Assert.True(plan.Acts);
        Assert.Empty(plan.Path);
        Assert.Equal((minion.X, minion.Y), plan.Destination);
        // waiting with nobody to attack: nowhere to go, its turn is over at once (returns 0, +0x30 set)
        minion.MovePatterns[0] = new MovePattern(-1, 1, 0, 0, -1);
        Assert.False(EnemyAi.Plan(f, minion, off).Acts);
        // asleep: the same, wherever it was headed
        minion.MovePatterns[0] = new MovePattern(-1, 2, (short)minion.X, (short)minion.Y, -1);
        minion.SleepTurns = 1;
        Assert.False(EnemyAi.Plan(f, minion, off).Acts);
    }

    [Fact]
    public void The_computers_phase_goes_up_the_records_then_round_again_for_another_action()
    {
        if (Game() is null) return;
        var f = Map1(out _);
        int record = 1;
        foreach (var u in f.Units) u.Slot = record++;
        var enemies = f.Units.Where(u => u.Side == Side.Enemy).OrderBy(u => u.Slot).ToList();
        Assert.True(enemies.Count >= 3);
        foreach (var e in enemies.Skip(3)) e.Hidden = true;
        var (e1, e2, e3) = (enemies[0], enemies[1], enemies[2]);
        var neutral = f.Units.First(u => u.Side == Side.Ally);
        neutral.Side = Side.Neutral;
        int group = 1, slot = 0;
        BattleUnit? Next()
        {
            var u = EnemyAi.NextInPhase(f, ref group, ref slot);
            if (u is not null) u.Acted = true;
            return u;
        }
        Assert.Same(e1, Next());
        Assert.Same(e2, Next());
        e1.Acted = false;                 // e2 gave e1 another action
        Assert.Same(e3, Next());          // the counter goes on up the records first,
        Assert.Same(e1, Next());          // then round again for it
        Assert.Same(neutral, Next());     // then the neutral units' round
        neutral.Acted = false;
        Assert.Null(Next());              // just the one
    }
}