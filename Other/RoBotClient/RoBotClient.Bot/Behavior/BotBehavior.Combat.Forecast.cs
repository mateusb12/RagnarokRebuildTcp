using RebuildSharedData.ClientTypes;
using RebuildSharedData.Data;
using RebuildSharedData.Enum;
using RoBotClient.Bot.GameData;
using RoBotClient.Bot.Session;
using RoBotClient.Bot.State;

namespace RoBotClient.Bot.Behavior;

public sealed partial class BotBehavior
{
    /// <summary>Public MCP entry — runs the bot's full Forecast pipeline against a database mob class.
    /// Same pipeline TickAsync uses for PickTarget, so MCP `simulate_fight` reflects every per-bot
    /// modifier: NoAutoAttack zeros melee, SkillScript drives ExtraSkillDps, ItemScript potions add an
    /// HP buffer, immobile-mob free-win rule applies. Returns a default BattleForecast (CanWin=false)
    /// when the class id is unknown.</summary>
    public BattleForecast ForecastAgainst(int monsterClassId)
    {
        var db = _data?.Monster(monsterClassId);
        if (db == null) return default;
        var snap = _bot.WithState(Snapshot.From);
        // MonsterInfo is the internal target type; Forecast() reads only ClassId / MaxHp from it. The
        // position is irrelevant — the sim doesn't pathfind, it just decides if the fight is winnable
        // given stats + ranges + script.
        var mi = new MonsterInfo(0, monsterClassId, db.Name, default, db.HP, db.HP);
        return Forecast(snap, mi);
    }

    /// <summary>Public MCP entry for a pack: the same group forecast the bot engages with (ForecastGroupOf).</summary>
    public GroupForecast ForecastGroupAgainst(IEnumerable<int> monsterClassIds)
    {
        var snap = _bot.WithState(Snapshot.From);
        var group = new List<MonsterInfo>();
        foreach (var id in monsterClassIds)
            if (_data?.Monster(id) is { } db) group.Add(new MonsterInfo(0, id, db.Name, default, db.HP, db.HP));
        return ForecastGroupOf(snap, group);
    }

    /// <summary>What the simulator is actually using for a forecast — exposed so the MCP / debug UI can
    /// show "why did canWin come out as X?". Captures every input the script-aware pipeline depends on
    /// so a wrong answer can be diagnosed without re-reading server packets or guessing config.</summary>
    public sealed record SimDiagnostics(
        bool NoAutoAttack,
        int AttackRange,
        int MagicAtkMin,
        int MagicAtkMax,
        double PotionHpBuffer,
        IReadOnlyList<(string Skill, int Level, string Target)> OffensiveScriptedSkills,
        IReadOnlyList<(int ItemId, int InventoryCount, int ApproxHealPerUse)> HpRescueItems);

    /// <summary>Build the diagnostics blob. Cheap — runs the same parsing the Forecast does, no packet
    /// I/O. Safe to call from MCP responses.</summary>
    public SimDiagnostics BuildSimDiagnostics()
    {
        var snap = _bot.WithState(Snapshot.From);
        var rules = GetSkillRules();
        var offensive = new List<(string, int, string)>();
        var maxKnown = new Dictionary<CharacterSkill, int>();
        foreach (var (s, lv) in snap.KnownSkills)
            if (lv > maxKnown.GetValueOrDefault(s)) maxKnown[s] = lv;
        foreach (var r in rules)
        {
            if (r.Target == SkillCastTarget.SelfCast || r.Target == SkillCastTarget.Ally) continue;
            if (!maxKnown.TryGetValue(r.Skill, out var known) || known <= 0) continue;
            var effective = r.Level <= 0 ? known : Math.Min(r.Level, known);
            offensive.Add((r.Skill.ToString(), effective, r.Target.ToString()));
        }
        ReloadItemScriptIfChanged();
        var rescues = new List<(int, int, int)>();
        var invCounts = _bot.WithState(w => w.Self.UnequippedCounts());
        foreach (var r in _itemRules)
        {
            var hpTrig = false;
            for (var i = 0; i < r.Conditions.Count; i++)
            {
                var c = r.Conditions[i];
                if (string.Equals(c.Lhs, "hppct", StringComparison.OrdinalIgnoreCase) && (c.Op == "<" || c.Op == "<="))
                { hpTrig = true; break; }
            }
            if (!hpTrig) continue;
            var heal = HealPerUse(r.ItemId);
            if (heal <= 0) continue;
            var count = invCounts.GetValueOrDefault(r.ItemId);
            rescues.Add((r.ItemId, count, heal));
        }
        return new SimDiagnostics(
            _config.NoAutoAttack,
            InferAttackRange(snap),
            snap.SelfMagicAtkMin,
            snap.SelfMagicAtkMax,
            EstimatePotionHpBuffer(snap),
            offensive,
            rescues);
    }

    /// <param name="poisoned">Forecast against the mob with Poison's DEF/VIT drop applied (Envenom's payoff).</param>
    private BattleForecast Forecast(Snapshot snap, MonsterInfo m, bool poisoned = false)
    {
        var me = MeCombatant(snap);
        var mDb = _data?.Monster(m.ClassId);
        // Effective HP buffer from healing potions actually in inventory AND covered by an item-script
        // rule that'll fire during the fight. Extends secsToKillMe — a Mage carrying 30 White Potions and
        // a `use 504 if hppct < 60` rule is way harder to kill than the raw HP number suggests.
        var hpBuffer = (int)EstimatePotionHpBuffer(snap);
        if (hpBuffer > 0) me = me with { Hp = me.Hp + hpBuffer };
        var defender = BuildMonsterMods(snap, m);
        var mods = BuildPlayerMods(snap) with
        {
            DefenderElement = defender.DefenderElement,
            DefenderElementLevel = defender.DefenderElementLevel,
            DefenderSize = defender.DefenderSize,
            DefenderRace = defender.DefenderRace,
            MonsterSkillDps = defender.MonsterSkillDps,
            AttackerAttackRange = InferAttackRange(snap),
            DefenderAttackRange = mDb?.Range ?? 1,
            // Only grants the out-of-range free kill; a mob that answers arrows with a spell doesn't get it.
            DefenderIsImmobile = IsMonsterImmobile(mDb) && !(mDb != null && _data!.PunishesRangedAttacks(mDb.Code)),
        };
        var mob = MonsterCombatant(m);
        if (poisoned)
        {
            var (def, vit) = SkillPolicy.PoisonedDefense(mob.Def, mob.Vit, string.Equals(mDb?.Special, "Boss", StringComparison.OrdinalIgnoreCase));
            mob = mob with { Def = def, Vit = vit };
        }
        var f = BattleSimulator.Forecast(me, mob, mods, _config.WinMargin, _config.MaxRoundsToKill);
        // A monster with no modeled skills that has hit us far harder than a swing (something we don't model)
        // gets one more of those mid-fight. Modeled skills are already in MonsterSkillDps.
        var burst = _bot.MaxHitTakenFrom(m.Name);
        if (mods.MonsterSkillDps == 0 && burst > f.MonsterDamagePerHit * 2)
            f = BattleSimulator.Forecast(me with { Hp = Math.Max(0, me.Hp - burst) }, mob, mods, _config.WinMargin, _config.MaxRoundsToKill);
        return f;
    }

    /// <summary>Effective attack range for the bot. Prefers the server-synced <c>CharacterStat.Range</c>
    /// (which reflects the equipped weapon — bow = ~9, melee weapon = 1). When not synced (older server
    /// build or before first stat-update), falls back to a skill heuristic: a bot with bow skills
    /// (DoubleStrafe / ArrowShower) or any magic Bolt skill is treated as ranged at 9 tiles, otherwise
    /// melee at 1.</summary>
    private int InferAttackRange(Snapshot snap)
    {
        if (snap.SelfAttackRange > 0) return snap.SelfAttackRange;
        // The server never sends Range (not in PlayerUpdateStats), so rebuild it like Player.UpdateStats does for a bow:
        // weapon range + Vulture's Eye level. Without this a bow user with no bow skill learned looked like melee (range 1).
        if (_data != null && _data.Gear.TryGetValue(snap.SelfWeaponItemId, out var weapon) && weapon.Type == "Bow" && weapon.Range > 0)
        {
            var vultureEye = 0;
            foreach (var (skill, level) in snap.KnownSkills) if (skill == CharacterSkill.VultureEye) vultureEye = Math.Max(vultureEye, level);
            return weapon.Range + vultureEye;
        }
        foreach (var (skill, _) in snap.KnownSkills)
        {
            // No bow skills here: without a bow equipped (handled above) they fail with "wrong weapon equipped",
            // and counting them made a bowless Archer think it outranged Mandragora while it walked up to knife it.
            switch (skill)
            {
                case CharacterSkill.FireBolt:
                case CharacterSkill.ColdBolt:
                case CharacterSkill.LightningBolt:
                case CharacterSkill.SoulStrike:
                case CharacterSkill.FireBall:
                case CharacterSkill.FrostDiver:
                case CharacterSkill.NapalmBeat:
                case CharacterSkill.HeavensDrive:
                case CharacterSkill.HolyLight:
                case CharacterSkill.LexDivina:
                    return 9;
            }
        }
        return 1;
    }

    /// <summary>True when the monster is a fixed-position mob — Mandragora-style plants (negative
    /// MoveSpeed) and Geographer-style AiAggressiveImmobile sprites. The simulator zeros incoming
    /// damage when this is true and the bot can shoot from outside the mob's attack range.</summary>
    private static bool IsMonsterImmobile(MonsterDbEntry? db) => HuntPolicy.IsImmobile(db);

    /// <summary>Look up DemonBane / BeastBane max-learned level from the bot's KnownSkills snapshot. The
    /// server adds (DemonBane*3) pre-DEF damage vs Demon/Undead and (BeastBane*5) vs Beast/Insect — see
    /// <see cref="BattleSimulator.RaceMasteryBonus"/>. Levels max out around 10 in this server.</summary>
    private static (int demonBane, int beastBane) MasteryLevels(Snapshot snap)
    {
        var demon = 0;
        var beast = 0;
        foreach (var (skill, level) in snap.KnownSkills)
        {
            if (skill == CharacterSkill.DemonBane && level > demon) demon = level;
            if (skill == CharacterSkill.BeastBane && level > beast) beast = level;
        }
        return (demon, beast);
    }

    private Combatant MeCombatant(Snapshot snap)
    {
        // NoAutoAttack: bot is a pure caster — the simulator must NOT count basic-attack damage. Zeroing
        // AtkMin/AtkMax collapses melee DPS to 0; the fight is decided entirely by ExtraSkillDps.
        var atkMin = _config.NoAutoAttack ? 0 : snap.SelfAtkMin;
        var atkMax = _config.NoAutoAttack ? 0 : snap.SelfAtkMax;
        return new Combatant(
            snap.SelfLevel, atkMin, atkMax, snap.SelfDef,
            snap.SelfVit, snap.SelfDex, snap.SelfAgi, snap.SelfHit, snap.SelfFlee,
            snap.SelfAttackInterval, snap.SelfHp, snap.SelfMaxHp);
    }

    private Combatant MonsterCombatant(MonsterInfo m)
    {
        var db = _data?.Monster(m.ClassId);
        var hp = m.MaxHp > 0 ? m.MaxHp : (db?.HP ?? 0);
        return db != null
            ? new Combatant(db.Level, db.AtkMin, db.AtkMax, db.Def, db.Vit, db.Dex, db.Agi, 0, 0, _data!.MonsterAttackDelay(m.ClassId), hp, hp)
            : new Combatant(1, 1, 1, 0, 0, 0, 0, 0, 0, 0, hp, hp);
    }

    /// <summary>Look up monster element/size/race from the exported DB and stash them on the modifier
    /// record. Defaults to "no effect" if the DB is missing or the strings don't match the enum.</summary>
    private CombatModifiers BuildMonsterMods(Snapshot snap, MonsterInfo m)
    {
        var db = _data?.Monster(m.ClassId);
        if (db == null) return CombatModifiers.Default;
        var (defEle, defLvl) = ElementChart.Parse(db.Element ?? "");
        return new CombatModifiers(
            DefenderElement: defEle,
            DefenderElementLevel: defLvl,
            DefenderSize: WeaponSizeChart.ParseSize(db.Size ?? ""),
            DefenderRace: WeaponSizeChart.ParseRace(db.Race ?? ""),
            MonsterSkillDps: BattleSimulator.MonsterSkillDps(MeCombatant(snap), snap.SelfInt, snap.SelfMDef,
                MonsterCombatant(m), _data!.MonsterInt(m.ClassId), _data.MonsterCasts(db.Code)));
    }

    /// <summary>Build the player-side attacker modifier for a group forecast (where defender mods are per-
    /// monster and supplied separately). Folds in attacker element + weapon class + crit + skill DPS.</summary>
    /// <summary>Player.CalculateMeleeAttack: a dagger swing hits twice when Next(0, 100) &lt;= DoubleAttackChance, which
    /// DoubleAttackHandler raises 5 per level. ponytail: gear that grants Double Attack (DoubleAttackModifiers) isn't read.</summary>
    private double DoubleAttackChance(Snapshot snap)
    {
        if (_data == null || !_data.Gear.TryGetValue(snap.SelfWeaponItemId, out var weapon) || weapon.Type != "Dagger") return 0;
        var level = 0;
        foreach (var (skill, lv) in snap.KnownSkills) if (skill == CharacterSkill.DoubleAttack) level = Math.Max(level, lv);
        return level > 0 ? Math.Min(1, (5 * level + 1) / 100.0) : 0;
    }

    private CombatModifiers BuildPlayerMods(Snapshot snap)
    {
        var (demon, beast) = MasteryLevels(snap);
        return WithSkillRotation(new CombatModifiers(
            AttackerElement: SimElement.Neutral,
            AttackerWeapon: SimWeaponClass.OneHandSword, // TODO read equipped weapon class once exposed
            AttackerLuk: snap.SelfLuk,
            AttackerAddCrit: snap.SelfAddCrit,
            AttackerDemonBaneLevel: demon,
            AttackerBeastBaneLevel: beast,
            AttackerDoubleAttackChance: DoubleAttackChance(snap)), snap);
    }

    /// <summary>The skill rotation every forecast (duel, pack, exp/h) credits, from the SkillScript only: no script means
    /// TickSkillsAsync casts nothing. Physical single-target skills use SkillPolicy.DamageMultiplier, the numbers the
    /// cast decision uses: the one adding the most over the SP on hand, as casts that each replace a swing.
    /// Magic has no SkillPolicy model yet, so it stays a DPS heuristic on MATK.</summary>
    // TODO: AoE (Magnum Break, Arrow Shower) and Sonic Blow get no forecast credit. Model them in SkillPolicy first
    //       (Sonic Blow: 8 hits x (0.5 + 0.05·lv) plus a +1 s cooldown, so it doesn't just replace one swing), then credit here.
    private CombatModifiers WithSkillRotation(CombatModifiers mods, Snapshot snap)
    {
        var maxKnown = new Dictionary<CharacterSkill, int>();
        foreach (var (s, lv) in snap.KnownSkills)
            if (lv > maxKnown.GetValueOrDefault(s)) maxKnown[s] = lv;
        // Several rules for one skill: credit the highest level, what fires under good conditions.
        var levels = new Dictionary<CharacterSkill, int>();
        foreach (var r in GetSkillRules())
        {
            if (r.Target is SkillCastTarget.SelfCast or SkillCastTarget.Ally) continue;
            if (!maxKnown.TryGetValue(r.Skill, out var known) || known <= 0) continue;
            var effective = r.Level <= 0 ? known : Math.Min(r.Level, known);
            if (effective > levels.GetValueOrDefault(r.Skill)) levels[r.Skill] = effective;
        }

        double bestGain = 0, multiplier = 0, magicBonus = 0;
        var casts = 0;
        foreach (var (skill, level) in levels)
        {
            var m = SkillPolicy.DamageMultiplier(skill, level);
            if (m > 1 && SkillPolicy.Aoe(skill) == null)
            {
                var n = snap.SelfSp / SkillPolicy.SpCost(_data?.SkillInfo(skill)?.SpCost, level);
                if ((m - 1) * n > bestGain) (bestGain, multiplier, casts) = ((m - 1) * n, m, n);
            }
            magicBonus += SpAffordable(snap, skill, level) * MagicWeightPerLevel(skill) * level;
        }
        // TODO: magic is still a heuristic (~2 s per bolt at MATK average, per-level weights). Add a SkillPolicy magic
        //       model (MATK vs MDEF, cast time, SP) so mage forecasts and cast decisions share one source of truth.
        var magicDps = Math.Clamp(magicBonus, 0, 3) * (snap.SelfMagicAtkMin + snap.SelfMagicAtkMax) / 2.0 / 2.0;
        return mods with { SkillHitMultiplier = multiplier, SkillCasts = casts, ExtraSkillDps = magicDps };
    }

    private static double MagicWeightPerLevel(CharacterSkill skill) => skill switch
    {
        CharacterSkill.FireBolt or CharacterSkill.ColdBolt or CharacterSkill.LightningBolt => 0.40,
        CharacterSkill.SoulStrike or CharacterSkill.HolyLight => 0.30,
        CharacterSkill.FireBall or CharacterSkill.NapalmBeat => 0.20,
        _ => 0,
    };

    /// <summary>Share of a skill's damage the bot can deliver right now: none without the SP to cast it. Bot3 took
    /// Horn after Horn on a "4 s kill" forecast that counted Bash at 2 SP; the real fights took 15 s and it died.</summary>
    // ponytail: SP for 3 casts = full credit, linear below. Model SP per cast vs fight length if forecasts still drift.
    private double SpAffordable(Snapshot snap, CharacterSkill skill, int level)
    {
        var costs = _data?.SkillInfo(skill)?.SpCost;
        if (costs == null || costs.Length == 0) return 1;
        var cost = costs[Math.Clamp(level, 1, costs.Length) - 1];
        return cost <= 0 ? 1 : Math.Clamp(snap.SelfSp / (3.0 * cost), 0, 1);
    }

    /// <summary>Average HP one use restores, from the server's item effect script (0 = doesn't heal HP).</summary>
    private int HealPerUse(int itemId) => _data?.HpRecovery(itemId) ?? 0;

    /// <summary>The "effective HP buffer" from the potions carried (HuntPolicy.PotionHpBuffer): a Mage with 50 White
    /// Potions and `use 504 if hppct < 60` is much harder to kill than the raw HP number.</summary>
    private double EstimatePotionHpBuffer(Snapshot snap)
    {
        if (_data == null) return 0;
        ReloadItemScriptIfChanged();
        return HuntPolicy.PotionHpBuffer(_data, _config, _itemRules, _bot.WithState(w => w.Self.UnequippedCounts()), snap.SelfMaxHp);
    }

    // The pack that piles on if we engage this target: same-type assist monsters that can see it, plus any
    // already-aggressive monster whose aggro range covers us. Lets us refuse mobs that are individually
    // winnable but collectively deadly (#8).
    // Without the range to snipe it, the bot stands next to the target, inside any neighbouring fixed monster's reach.
    private List<MonsterInfo> EngagementGroup(Snapshot snap, MonsterInfo target) =>
        HuntPolicy.EngagementGroup(_data, snap.Monsters, snap.SelfPos, target, standsAtTarget: !SnipesFixed(snap, target));

    private GroupForecast ForecastGroupOf(Snapshot snap, List<MonsterInfo> group)
    {
        var mons = new List<Combatant>(group.Count);
        var mods = new List<CombatModifiers>(group.Count);
        foreach (var g in group)
        {
            mons.Add(MonsterCombatant(g));
            mods.Add(BuildMonsterMods(snap, g));
        }
        var me = MeCombatant(snap);
        return BattleSimulator.ForecastGroup(me, mons, mods, BuildPlayerMods(snap),
            _config.WinMargin, _config.MaxRoundsToKill);
    }
}
