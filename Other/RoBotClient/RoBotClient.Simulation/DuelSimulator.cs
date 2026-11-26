using System.Diagnostics;
using System.Reflection;
using RebuildSharedData.Data;
using RebuildSharedData.Enum;
using RebuildSharedData.Enum.EntityStats;
using RoRebuildServer.Data;
using RoRebuildServer.Data.Monster;
using RoRebuildServer.EntityComponents.Character;
using RoRebuildServer.EntityComponents;
using RoRebuildServer.EntityComponents.Items;
using RoRebuildServer.EntitySystem;
using RoRebuildServer.Networking;
using RoRebuildServer.Simulation.Skills.SkillHandlers.Swordsman;
using RoRebuildServer.Simulation.StatusEffects.Setup;
using RoRebuildServer.Simulation.Util;

namespace RoBotClient.Simulation;

/// <summary>One equipped item as the bot sees it in its inventory: base item, refine and socketed cards.</summary>
public readonly record struct EquippedItem(EquipSlot Slot, int ItemId, int Refine, int[] Cards);

/// <summary>Everything the server derives a player's combat stats from, and that the bot knows about itself:
/// base level, job, base stats, learned skills and equipment. Derived stats (ATK, HIT, FLEE, crit, DEF, ASPD,
/// Double Attack chance...) are not inputs: the server's own Player.UpdateStats computes them from these.</summary>
public sealed record PlayerBuild(
    int Level, int JobLevel, int JobId,
    int Str, int Agi, int Vit, int Int, int Dex, int Luk,
    IReadOnlyDictionary<CharacterSkill, int> LearnedSkills,
    IReadOnlyList<EquippedItem> Equipment,
    int AmmoItemId = 0,
    IReadOnlyDictionary<int, int>? Inventory = null)
{
    /// <summary>What decides combat: the build without <see cref="Inventory"/> (unequipped item id → count, carried for
    /// the offline hunt; it changes with every pickup or potion), for comparing builds.</summary>
    public PlayerBuild CombatOnly() => this with { Inventory = null };
}

/// <summary>What the player does in one attack slot: an auto-attack (Skill = None) or a targeted skill.</summary>
public readonly record struct PlayerAction(CharacterSkill Skill, int Level)
{
    public static readonly PlayerAction AutoAttack = new(CharacterSkill.None, 0);
}

/// <summary>The fight as the player sees it when an attack slot opens. MonsterHp counts only damage that has landed.</summary>
public readonly record struct DuelMoment(int ActionIndex, double Seconds, int MonsterHp, int MonsterMaxHp, int Hp, int MaxHp, int Sp, int MaxSp,
    double SecondsSinceLastSkill);

/// <summary>A status effect on the player when the fight starts, as the server's AddStatusEffect takes it: its type,
/// Value1 (an Awakening Potion's IncreasedAttackSpeed is 15) and the seconds it has left.</summary>
public readonly record struct ActiveStatus(CharacterStatusEffect Status, int Value1, double Seconds);

/// <summary>Chooses the action for the next attack slot. A skill the SP can't pay for falls back to an auto-attack,
/// as the server refuses the cast.</summary>
public delegate PlayerAction ActionPolicy(in DuelMoment moment);

/// <param name="PlayerHp">HP when the fight starts; 0 = full.</param>
/// <param name="PlayerSp">SP when the fight starts; null = full. 0 is an empty SP bar, not full: Bot3 engaged with 0 SP and
/// was simulated casting a full bar of Bashes.</param>
/// <param name="MonsterHp">Target HP when the fight starts; 0 = full.</param>
/// <param name="MonsterAttacksFirst">The monster is already attacking when we start (an aggressive one that reached
/// us); otherwise it starts swinging when our first hit lands, as a passive monster retaliates.</param>
/// <param name="Seed">Fixes the server's GameRandom stream for this request (deterministic replays and tests).</param>
public sealed record DuelRequest(
    PlayerBuild Player, int MonsterClassId,
    int PlayerHp = 0, int? PlayerSp = null, int MonsterHp = 0,
    ActionPolicy? Policy = null,
    bool MonsterAttacksFirst = false,
    int Trials = 1000, int? Seed = null, double MaxSeconds = 300,
    IReadOnlyList<ActiveStatus>? PlayerStatuses = null);

public readonly record struct Distribution(int Count, double Mean, double StdDev, double P10, double P50, double P90)
{
    public static Distribution Of(List<double> values)
    {
        if (values.Count == 0) return default;
        values.Sort();
        var mean = values.Average();
        var variance = values.Count > 1 ? values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1) : 0;
        double At(double q) => values[Math.Clamp((int)Math.Round(q * (values.Count - 1)), 0, values.Count - 1)];
        return new Distribution(values.Count, mean, Math.Sqrt(variance), At(0.1), At(0.5), At(0.9));
    }
}

/// <summary>The derived stats the server computed for the two simulated entities. The player half is what the live
/// server also sends the bot (UpdatePlayerData), so it can be checked against the real character.</summary>
/// <param name="ClientAttackMin">ATK as Player.SendPlayerUpdateData reports it: attack power range plus refine bonus.</param>
public sealed record CombatSheet(
    int MaxHp, int MaxSp, int ClientAttackMin, int ClientAttackMax,
    int Def, int MDef, int AddHit, int AddFlee, int AddCrit, int PerfectDodge, int CritPerMille, int DoubleAttackChance,
    int WeaponClass, float AttackDelay, float AttackMotion, float DamageDelay,
    int MonsterMaxHp, float MonsterAttackDelay, float MonsterDamageDelay, bool MonsterFightsBack);

/// <param name="Attacks">Attack actions (swings and skill casts, misses included) until the killing blow; won trials.</param>
/// <param name="Seconds">From the first attack action to the killing blow landing; won trials.</param>
/// <param name="DamageTaken">Gross damage of the monster's swings made before the killing blow landed; won trials.</param>
/// <param name="DamagePerAction">Damage of each of our actions on the target (0 for misses), all trials.</param>
/// <param name="DuelDamagePerAction">Each won trial's mean damage per action. Not the same as DamagePerAction: a duel
/// stops at the kill, so lucky duels (crits, double attacks) are short and high and unlucky ones long and low; this is
/// the distribution one real duel's mean is a draw from.</param>
/// <param name="SkillCasts">Skill actions per won trial, the rest of Attacks being auto-attacks.</param>
/// <param name="UnsupportedEffects">A status effect other than poison appeared on either side (card procs, stun...).
/// The server runs it, but its effects (lost turns, stat changes the simulator's clock ignores) are not trusted.</param>
public sealed record DuelPrediction(
    int Trials, double WinRate, double DeathRate, double TimeoutRate,
    Distribution Attacks, Distribution Seconds, Distribution DamageTaken, Distribution DamagePerAction,
    Distribution DuelDamagePerAction, double MissRate, double CritRate, double DoubleAttackRate, double SkillActionShare,
    Distribution SkillCasts, CombatSheet Sheet, bool UnsupportedEffects, TimeSpan Elapsed);

/// <summary>
/// Offline 1v1 duels executed by the game server's own combat code (RoRebuildServer.dll, the build the live server
/// runs). Entities are created the way World.CreatePlayer / World.CreateMonster and Player.Init create them, so
/// equipment, refine, cards and passive skills go through the server's own stat pipeline. Each attack is resolved
/// by the server method the live game uses for it:
/// <list type="bullet">
/// <item>auto-attack: Player.CalculateMeleeAttack (crits, Double Attack, dual wield, katar), via CombatEntity.CalculateMeleeAttack;</item>
/// <item>Bash, Envenom: BashHandler / EnvenomHandler.Process, the server's skill handlers (Envenom's poison is
/// run by the server's StatusPoison through CombatEntity.Update every 0.25 s);</item>
/// <item>monster swing: CombatEntity.CalculateMeleeAttack.</item>
/// </list>
/// What is modelled here rather than executed: the clock. Attack slots repeat at the server's AttackDelayTime
/// (Player.PerformAttack) or the cooldown ApplyCooldownForAttackAction sets, damage lands DamageInfo.Time after the
/// action, and HP is subtracted directly (ApplyQueuedCombatResult needs a live map).
///
/// TODO(hunting milestone): searching, walking to targets, map geometry and respawns.
/// TODO(recovery milestone): HP/SP regeneration, sitting, potions between and during fights.
/// TODO(monster skills): monsters only auto-attack here; their TryCast skills are not executed yet.
/// </summary>
public static class DuelSimulator
{
    internal static readonly FieldInfo RandomField = typeof(GameRandom).GetField("local", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingFieldException(nameof(GameRandom), "local");

    private static readonly Dictionary<CharacterSkill, RoRebuildServer.Simulation.Skills.SkillHandlerBase> SkillHandlers = new()
    {
        [CharacterSkill.Bash] = new BashHandler(),
        [CharacterSkill.Envenom] = new RoRebuildServer.Simulation.Skills.SkillHandlers.Thief.EnvenomHandler(),
    };

    /// <summary>Skills the simulator executes besides auto-attack.</summary>
    public static bool IsSimulated(CharacterSkill skill) => SkillHandlers.ContainsKey(skill);
    private static int nextEntityId = 1_000_000_000;

    public static DuelPrediction Simulate(DuelRequest request)
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("forecast duels (DuelSimulator)", byCaller: true);
        if (!ServerCombatRuntime.IsInitialized)
            throw new InvalidOperationException("ServerCombatRuntime.EnsureInitialized must run first.");
        if (!DataManager.MonsterIdLookup.TryGetValue(request.MonsterClassId, out var monsterInfo))
            throw new ArgumentException($"Unknown monster class {request.MonsterClassId}.");

        lock (ServerCombatRuntime.Gate)
        {
            var stopwatch = Stopwatch.StartNew();
            var previousRandom = RandomField.GetValue(null);
            if (request.Seed is { } seed) RandomField.SetValue(null, new Random(seed));
            var (previousTime, previousTimeFloat) = (Time.ElapsedTime, Time.ElapsedTimeFloat);
            var player = CreatePlayer(request.Player);
            var monster = CreateMonster(monsterInfo);
            try
            {
                ApplyStatuses(player.CombatEntity, request.PlayerStatuses);
                return Run(request, player, monster, stopwatch);
            }
            finally
            {
                ClearStatusEffects(player.CombatEntity);
                ClearStatusEffects(monster.CombatEntity);
                (Time.ElapsedTime, Time.ElapsedTimeFloat) = (previousTime, previousTimeFloat);
                EntityManager.Recycle(player.Entity);
                EntityManager.Recycle(monster.Entity);
                ServerCombatRuntime.ResetPacketQueue();
                if (request.Seed != null) RandomField.SetValue(null, previousRandom);
            }
        }
    }

    public static bool IsKnownMonster(int monsterClassId) => DataManager.MonsterIdLookup.ContainsKey(monsterClassId);

    /// <summary>MonsterBase.MoveSpeed (seconds per cell) when its AI (MonsterAI.csv) ever starts a random move; else -1 (plants, eggs).</summary>
    public static double MonsterMoveSpeed(int monsterClassId) =>
        DataManager.MonsterIdLookup.TryGetValue(monsterClassId, out var m)
        && DataManager.GetAiStateMachine(m.AiType).Any(entry => entry.OutputCheck == MonsterOutputCheck.OutRandomMoveStart) ? m.MoveSpeed : -1;

    /// <summary>The server-derived stats of this build against this monster, without fighting.</summary>
    public static CombatSheet Inspect(PlayerBuild build, int monsterClassId, IReadOnlyList<ActiveStatus>? statuses = null)
    {
        var monsterInfo = DataManager.MonsterIdLookup[monsterClassId];
        lock (ServerCombatRuntime.Gate)
        {
            var player = CreatePlayer(build);
            var monster = CreateMonster(monsterInfo);
            try
            {
                ApplyStatuses(player.CombatEntity, statuses);
                return Sheet(player, monster);
            }
            finally
            {
                ClearStatusEffects(player.CombatEntity);
                EntityManager.Recycle(player.Entity);
                EntityManager.Recycle(monster.Entity);
                ServerCombatRuntime.ResetPacketQueue();
            }
        }
    }

    private static DuelPrediction Run(DuelRequest request, Player player, Monster monster, Stopwatch stopwatch)
    {
        var self = player.CombatEntity;
        var enemy = monster.CombatEntity;
        var sheet = Sheet(player, monster);
        var monsterInterval = MonsterAttackInterval(enemy);
        var startHp = request.PlayerHp > 0 ? request.PlayerHp : sheet.MaxHp;
        var startSp = request.PlayerSp is { } sp0 ? Math.Min(sp0, sheet.MaxSp) : sheet.MaxSp;
        var startMonsterHp = request.MonsterHp > 0 ? request.MonsterHp : sheet.MonsterMaxHp;

        var attacks = new List<double>();
        var seconds = new List<double>();
        var damageTaken = new List<double>();
        var damagePerAction = new List<double>();
        var duelDamagePerAction = new List<double>();
        var skillCasts = new List<double>();
        int wins = 0, deaths = 0, timeouts = 0, actions = 0, misses = 0, crits = 0, doubles = 0, skillActions = 0;
        var unsupported = false;
        var landings = new PriorityQueue<(int Damage, bool OnMonster), double>();

        for (var trial = 0; trial < request.Trials; trial++)
        {
            int hp = startHp, sp = startSp, monsterHp = startMonsterHp, trialActions = 0, taken = 0, trialDealt = 0, trialSkills = 0;
            double nextPlayerAction = 0, lastSkillAt = double.NegativeInfinity;
            var nextMonsterAction = request.MonsterAttacksFirst && sheet.MonsterFightsBack ? 0 : double.PositiveInfinity;
            var nextStatusTick = double.PositiveInfinity;
            landings.Clear();
            if (!ClearStatusEffects(self) || !ClearStatusEffects(enemy))
                throw new InvalidOperationException("A status effect survived RemoveAll; the next trial would inherit it.");
            var outcome = 0; // 1 win, -1 death, 0 still fighting
            var clock = 0.0;
            SetClock(clock);
            ApplyStatuses(self, request.PlayerStatuses); // cleared above; they start each trial with the time they had left
            if (HasStatusEffects(self, enemy)) nextStatusTick = StatusTickSeconds;

            while (outcome == 0)
            {
                // Next event: a landing, our action, the monster's action or a status tick, earliest first
                // (landings win ties).
                landings.TryPeek(out _, out var landingAt);
                if (landings.Count == 0) landingAt = double.PositiveInfinity;
                clock = Math.Min(Math.Min(landingAt, nextStatusTick), Math.Min(nextPlayerAction, nextMonsterAction));
                if (clock > request.MaxSeconds) break;
                SetClock(clock);

                if (landingAt <= clock)
                {
                    var (damage, onMonster) = landings.Dequeue();
                    if (onMonster)
                    {
                        monsterHp -= damage;
                        if (damage > 0) MonsterLastDamageField.SetValue(monster, Time.ElapsedTimeFloat);
                        if (double.IsPositiveInfinity(nextMonsterAction) && sheet.MonsterFightsBack) nextMonsterAction = clock; // retaliation
                        if (monsterHp <= 0) outcome = 1;
                    }
                    else
                    {
                        hp -= damage;
                        if (hp <= 0) outcome = -1;
                    }
                    continue;
                }

                if (nextStatusTick <= clock)
                {
                    // The server's own status update (poison ticks, expirations, pending effects), on each side's
                    // current HP; damage it deals is queued on its owner and lands now.
                    enemy.SetStat(CharacterStat.Hp, Math.Max(1, monsterHp));
                    self.SetStat(CharacterStat.Hp, Math.Max(1, hp));
                    enemy.Update();
                    self.Update();
                    monsterHp -= TakeQueuedDamage(enemy);
                    hp -= TakeQueuedDamage(self);
                    if (monsterHp <= 0) outcome = 1;
                    else if (hp <= 0) outcome = -1;
                    nextStatusTick = HasStatusEffects(self, enemy) ? clock + StatusTickSeconds : double.PositiveInfinity;
                    continue;
                }

                if (nextPlayerAction <= nextMonsterAction)
                {
                    var moment = new DuelMoment(trialActions, clock, monsterHp, sheet.MonsterMaxHp, hp, sheet.MaxHp, sp, sheet.MaxSp, clock - lastSkillAt);
                    var action = request.Policy?.Invoke(moment) ?? PlayerAction.AutoAttack;
                    var cost = action.Skill == CharacterSkill.None ? 0 : DataManager.GetSpForSkill(action.Skill, action.Level);
                    if (cost > sp) action = PlayerAction.AutoAttack;
                    var (result, cooldown) = Act(player, enemy, action);
                    sp -= action.Skill == CharacterSkill.None ? 0 : cost;
                    var dealt = result.IsDamageResult ? result.Damage * result.HitCount + Math.Max(0, (int)result.DamageOffHand) : 0;
                    landings.Enqueue((dealt, true), clock + result.Time - Time.ElapsedTimeFloat);
                    damagePerAction.Add(dealt);
                    trialDealt += dealt;
                    trialActions++;
                    actions++;
                    if (dealt == 0) misses++;
                    if (result.Result == AttackResult.CriticalDamage) crits++;
                    if (result.HitCount >= 2 && action.Skill == CharacterSkill.None) doubles++;
                    if (action.Skill != CharacterSkill.None)
                    {
                        skillActions++;
                        trialSkills++;
                        lastSkillAt = clock;
                    }
                    nextPlayerAction = clock + cooldown;
                }
                else
                {
                    var result = enemy.CalculateMeleeAttack(self);
                    var dealt = result.IsDamageResult ? result.Damage * result.HitCount : 0;
                    // Counted when the swing happens, as the bot sees it (the Attack packet carries the damage); a swing
                    // already in the air when its monster dies still lands.
                    taken += dealt;
                    landings.Enqueue((dealt, false), clock + result.Time - Time.ElapsedTimeFloat);
                    nextMonsterAction = clock + monsterInterval;
                }
                enemy.DamageQueue.Clear();
                self.DamageQueue.Clear();
                if (HasStatusEffects(self, enemy))
                {
                    if (double.IsPositiveInfinity(nextStatusTick)) nextStatusTick = clock + StatusTickSeconds;
                    if (HasUnsupportedStatus(self) || HasUnsupportedStatus(enemy)) unsupported = true;
                }
            }

            if (outcome == 1)
            {
                wins++;
                attacks.Add(trialActions);
                seconds.Add(clock);
                damageTaken.Add(taken);
                if (trialActions > 0) duelDamagePerAction.Add((double)trialDealt / trialActions);
                skillCasts.Add(trialSkills);
            }
            else if (outcome == -1) deaths++;
            else timeouts++;
        }

        var trials = Math.Max(1, request.Trials);
        var swings = Math.Max(1, actions);
        return new DuelPrediction(request.Trials, (double)wins / trials, (double)deaths / trials, (double)timeouts / trials,
            Distribution.Of(attacks), Distribution.Of(seconds), Distribution.Of(damageTaken), Distribution.Of(damagePerAction),
            Distribution.Of(duelDamagePerAction),
            (double)misses / swings, (double)crits / swings, (double)doubles / swings, (double)skillActions / swings,
            Distribution.Of(skillCasts), sheet, unsupported, stopwatch.Elapsed);
    }

    /// <summary>One attack slot through the server's code path for it. Returns the damage result and the cooldown
    /// the server puts on the next attack slot.</summary>
    private static (DamageInfo Result, double Cooldown) Act(Player player, CombatEntity target, PlayerAction action)
    {
        switch (action.Skill)
        {
            case CharacterSkill.None:
                // Player.PerformAttack: CalculateMeleeAttack, then AttackCooldown = now + AttackDelayTime.
                return (player.CombatEntity.CalculateMeleeAttack(target), player.GetTiming(TimingStat.AttackDelayTime));
            case CharacterSkill.Bash or CharacterSkill.Envenom:
                // The server's handler queues its result on the target and sets the cooldown through
                // ApplyCooldownForAttackAction; read both back. Envenom's poison lands as a status effect, which
                // the run loop reports as UnsupportedEffects until status effects are simulated.
                player.Character.AttackCooldown = 0;
                target.DamageQueue.Clear();
                SkillHandlers[action.Skill].Process(player.CombatEntity, target, target.Character.Position, action.Level, false, false);
                var cooldown = player.Character.AttackCooldown - Time.ElapsedTime;
                // ExecuteCombatResult queues only results that do something: a miss, or an element the target is
                // immune to, leaves the queue empty. That action dealt 0, landing now.
                if (target.DamageQueue.Count == 0)
                    return (new DamageInfo { Result = AttackResult.Miss, Time = Time.ElapsedTimeFloat }, cooldown);
                if (target.DamageQueue.Count > 1)
                    throw new InvalidOperationException($"{action.Skill} handler queued {target.DamageQueue.Count} results.");
                return (target.DamageQueue[0], cooldown);
            default:
                throw new NotSupportedException($"{action.Skill} is not simulated yet (supported: auto-attack, Bash, Envenom).");
        }
    }

    // ponytail: status effects are updated every quarter second, not on the server's own 1 s schedule phase; a
    // poison tick can land up to 0.25 s late. Finer steps cost more updates per poisoned second.
    private const double StatusTickSeconds = 0.25;

    /// <summary>Status effects whose effect the simulator reproduces by running the server's own handlers over
    /// simulated time. Anything else (stun, provoke, card procs...) still flags the trial UnsupportedEffects:
    /// it may need behaviour the duel loop doesn't model (a stunned monster stops swinging, for one).</summary>
    /// IncreasedAttackSpeed (Concentration, Awakening, Berserk Potion): its handler only adds Value1 to AspdBonus, which
    /// the server's own stat update turns into AttackDelayTime; the static fidelity check compares that with the live one.
    private static readonly HashSet<CharacterStatusEffect> SimulatedStatuses = new() { CharacterStatusEffect.Poison, CharacterStatusEffect.IncreasedAttackSpeed };

    /// <summary>Whether a status effect on the player at the start of a fight can be given to the simulator.</summary>
    public static bool IsSimulatedStatus(CharacterStatusEffect status) => SimulatedStatuses.Contains(status);

    // The server's own entry point for them (an item script's AddStatusEffect(StatusIncreasedAttackSpeed, 30m, 15)).
    private static void ApplyStatuses(CombatEntity entity, IReadOnlyList<ActiveStatus>? statuses)
    {
        if (statuses == null) return;
        foreach (var status in statuses)
            entity.AddStatusEffect(StatusEffectState.NewStatusEffect(status.Status, (float)status.Seconds, status.Value1));
    }

    internal static readonly FieldInfo MonsterLastDamageField =
        typeof(Monster).GetField("timeSinceLastDamage", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static void SetClock(double seconds)
    {
        Time.ElapsedTime = seconds;
        Time.ElapsedTimeFloat = (float)seconds;
    }

    private static bool HasStatusEffects(CombatEntity self, CombatEntity enemy) =>
        self.TryGetStatusContainer(out _) || enemy.TryGetStatusContainer(out _);

    // ponytail: the client-visible status list is the only enumeration the container offers; a hidden status
    // would slip through. Upgrade path: a server-side accessor for the active status types.
    private static bool HasUnsupportedStatus(CombatEntity entity) =>
        entity.TryGetStatusContainer(out var container)
        && container.GetClientSerializationContainer().Keys.Any(status => !SimulatedStatuses.Contains(status));

    private static int TakeQueuedDamage(CombatEntity entity)
    {
        var total = 0;
        foreach (var damage in entity.DamageQueue)
            if (damage.IsDamageResult) total += damage.Damage * Math.Max(1, (int)damage.HitCount);
        entity.DamageQueue.Clear();
        return total;
    }

    /// <summary>Removes every status effect (running their expiry handlers, which undo their stat changes) and
    /// returns the container to its pool, so a trial starts from the state the previous one began with.</summary>
    internal static bool ClearStatusEffects(CombatEntity entity)
    {
        if (!entity.TryGetStatusContainer(out var container)) return true;
        container.RemoveAll();
        entity.Update();
        entity.DamageQueue.Clear();
        return !entity.TryGetStatusContainer(out _);
    }

    /// <summary>The cooldown CombatEntity.ApplyCooldownForAttackAction puts between a monster's swings.</summary>
    internal static double MonsterAttackInterval(CombatEntity monster)
    {
        monster.Character.AttackCooldown = 0;
        monster.ApplyCooldownForAttackAction();
        var interval = monster.Character.AttackCooldown - Time.ElapsedTime;
        monster.Character.AttackCooldown = 0;
        return interval;
    }

    private static CombatSheet Sheet(Player player, Monster monster)
    {
        var self = player.CombatEntity;
        var (atk1, atk2) = self.CalculateAttackPowerRange(false);
        var probe = self.PrepareTargetedSkillResult(monster.CombatEntity);
        var monsterProbe = monster.CombatEntity.PrepareTargetedSkillResult(self);
        var weapon = player.Equipment.MainHandWeapon;
        return new CombatSheet(
            self.GetStat(CharacterStat.MaxHp), self.GetStat(CharacterStat.MaxSp),
            atk1 + weapon.MinRefineAtkBonus, atk2 + weapon.MaxRefineAtkBonus,
            self.GetEffectiveStat(CharacterStat.Def), self.GetEffectiveStat(CharacterStat.MDef),
            self.GetStat(CharacterStat.AddHit), self.GetStat(CharacterStat.AddFlee), self.GetStat(CharacterStat.AddCrit),
            self.GetEffectiveStat(CharacterStat.PerfectDodge), self.GetBaseCritRate(), self.GetStat(CharacterStat.DoubleAttackChance),
            player.MainWeaponClass, player.GetTiming(TimingStat.AttackDelayTime), player.GetTiming(TimingStat.AttackMotionTime),
            probe.Time - Time.ElapsedTimeFloat,
            monster.CombatEntity.GetStat(CharacterStat.MaxHp), (float)MonsterAttackInterval(monster.CombatEntity),
            monsterProbe.Time - Time.ElapsedTimeFloat,
            FightsBack(monster));
    }

    /// <summary>Whether the monster's AI state machine (MonsterAI.csv) can ever perform an attack: plants and the like never do.</summary>
    internal static bool FightsBack(Monster monster) =>
        DataManager.GetAiStateMachine(monster.MonsterBase.AiType).Any(entry => entry.OutputCheck == MonsterOutputCheck.OutPerformAttack);

    // World.CreatePlayer + the character-load half of it, minus the map, network and database parts.
    internal static Player CreatePlayer(PlayerBuild build)
    {
        var entity = EntityManager.New(EntityType.Player);
        var character = entity.Get<WorldObject>();
        var player = entity.Get<Player>();
        var combat = entity.Get<CombatEntity>();

        character.Id = Interlocked.Increment(ref nextEntityId);
        character.IsActive = true;
        character.Entity = entity;
        character.Position = new Position(50, 50);
        character.ClassId = 0;
        character.MoveSpeed = 0.15f;
        character.Type = CharacterType.Player;
        character.Init(ref entity);
        character.Map = ServerCombatRuntime.Arena;
        combat.Init(ref entity, character);
        combat.SetStat(CharacterStat.Level, 1);

        player.Connection = new NetworkConnection(null!); // never connected: every packet send is skipped
        player.Entity = entity;
        player.CombatEntity = combat;
        player.Character = character;
        player.Name = "[SIM]";
        player.LearnedSkills = new Dictionary<CharacterSkill, int>(build.LearnedSkills);

        player.SetData(PlayerStat.Level, build.Level);
        player.SetData(PlayerStat.JobLevel, build.JobLevel);
        player.SetData(PlayerStat.Job, build.JobId);
        player.SetData(PlayerStat.Str, build.Str);
        player.SetData(PlayerStat.Agi, build.Agi);
        player.SetData(PlayerStat.Vit, build.Vit);
        player.SetData(PlayerStat.Int, build.Int);
        player.SetData(PlayerStat.Dex, build.Dex);
        player.SetData(PlayerStat.Luk, build.Luk);
        player.SetData(PlayerStat.Status, 1); // an existing character: Init must not hand out the starter kit

        // ItemEquipState.DeSerialize: equipment is bag ids into the inventory; Init's RunAllOnEquip applies it.
        player.Equipment = new ItemEquipState();
        foreach (var item in build.Equipment)
        {
            var unique = new UniqueItem { Id = item.ItemId, Count = 1, Refine = (byte)item.Refine, UniqueId = Guid.NewGuid() };
            for (var slot = 0; slot < 4 && slot < item.Cards.Length; slot++)
                unique.SetSlotData(slot, item.Cards[slot]);
            var bagId = player.AddItemToInventory(new ItemReference(unique));
            player.Equipment.ItemSlots[(int)item.Slot] = bagId;
            player.Equipment.ItemIds[(int)item.Slot] = item.ItemId;
        }
        player.Equipment.AmmoId = build.AmmoItemId > 0 ? build.AmmoItemId : -1;

        player.ApplyDataToCharacter();
        player.Init();
        return player;
    }

    // World.CreateMonster minus map placement and minions.
    internal static Monster CreateMonster(MonsterDatabaseInfo info)
    {
        var entity = EntityManager.New(EntityType.Monster);
        var character = entity.Get<WorldObject>();
        var combat = entity.Get<CombatEntity>();
        var monster = entity.Get<Monster>();

        character.Id = Interlocked.Increment(ref nextEntityId);
        character.IsActive = true;
        character.ClassId = info.Id;
        character.Entity = entity;
        character.Position = new Position(51, 50);
        character.MoveSpeed = info.MoveSpeed;
        character.Type = CharacterType.Monster;
        character.Init(ref entity);
        character.Map = ServerCombatRuntime.Arena;
        combat.Entity = entity;
        combat.Init(ref entity, character);
        monster.Initialize(ref entity, character, combat, info, info.AiType, null, "sim");
        return monster;
    }
}
