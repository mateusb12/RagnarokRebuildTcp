using RebuildSharedData.Enum;
using RoBotClient.Simulation;

namespace RoBotClient.Bot.Behavior;

// Shadow mode for the server-code duel simulator (RoBotClient.Simulation). Every engagement records the bot's combat
// state, the old BattleSimulator forecast and the server-code forecast into a DuelRecord; BotSession appends what
// really happened and BotManager stores it under bot-duels/ for DuelValidator. Decisions still use Forecast().
public sealed partial class BotBehavior
{
    private const int ShadowTrials = 300;

    // The moment-driven inputs a skill condition can read: the simulator supplies these per action, so their value at
    // the engagement is not part of the policy (see ScriptedSkillPolicy).
    private static readonly HashSet<string> MomentConditions = new(StringComparer.OrdinalIgnoreCase)
        { "target.hp", "target.maxhp", "target.hppct", "self.hp", "self.hppct", "self.sp", "self.sppct" };

    /// <summary>Everything is captured here, on the tick: the build and stats, the monster, and the skill policy
    /// over a copy of this tick's snapshot. ForecastWorker simulates it later on its own thread, so the bot's newer
    /// state can't leak into it, and the result is attached to this duel's record.</summary>
    private void RecordDuelStart(Snapshot snap, MonsterInfo target, BattleForecast forecast)
    {
        try
        {
            if (_decision is { } decision) forecast = decision.Old; // OldForecast is the old simulator's, whatever decided
            var (build, live) = _bot.CaptureCombatState();
            var rules = GetSkillRules().Where(r => r.Target == SkillCastTarget.Enemy).ToList();
            var record = new DuelRecord
            {
                EngagedAtUtc = DateTime.UtcNow,
                Character = _bot.WithState(w => w.Self.Name),
                Map = snap.Map,
                MonsterId = target.Id,
                MonsterClassId = target.ClassId,
                MonsterName = target.Name,
                MonsterHpAtEngage = target.Hp,
                MonsterMaxHpAtEngage = target.MaxHp,
                HpAtEngage = snap.SelfHp,
                SpAtEngage = snap.SelfSp,
                InParty = _bot.InParty,
                DecisionSource = _config.ServerForecast == ServerForecastMode.On ? _decision?.Source ?? "old: not a PickTarget decision" : null,
                SelfStatuses = snap.SelfStatuses.Select(s => s.ToString()).ToList(),
                OwnStatuses = _bot.OwnStatuses(MaxFightSeconds, out _),
                SkillRules = rules.Select(r => $"{r.Skill} {r.Level}" + (r.EveryCooldown > 0 ? $" every {r.EveryCooldown}" : "")
                    + (r.Conditions.Count > 0 ? " if " + string.Join(" and ", r.Conditions.Select(c => $"{c.Lhs} {c.Op} {c.Value}")) : "")).ToList(),
                Build = build,
                Look = _bot.WithState(w => new Appearance(w.Self.IsMale, w.Self.HeadType, w.Self.HairColor)),
                Live = live,
                OldForecast = new ForecastSummary(forecast.CanWin ? 1 : 0, forecast.AttacksToKill, forecast.SecondsToKillMonster,
                    forecast.ExpectedDamageTaken, DamagePerAction: forecast.ExpectedDamagePerSwing,
                    Note: "deterministic: WinRate is CanWin"),
            };
            if (_config.ServerForecast != ServerForecastMode.Off)
            {
                var request = ServerRequest(snap, target, build, out var policyNote, out var policyKey);
                var notes = new List<string>();
                if (policyNote != null) notes.Add(policyNote);
                if (record.OwnStatuses == null) notes.Add("own status effects not simulated");
                record.PendingForecast = ForecastWorker.Submit(request, policyKey, ForecastPriority.Urgent)
                    .ContinueWith(t => AttachServerForecast(record, t.Result, notes), TaskContinuationOptions.ExecuteSynchronously);
            }
            _bot.BeginDuel(record);
        }
        catch (Exception e)
        {
            OnLog?.Invoke($"duel record error: {e}");
        }
    }

    private static void AttachServerForecast(DuelRecord record, ForecastResult result, List<string> notes)
    {
        lock (record)
        {
            record.ServerForecastReadyAt = (DateTime.UtcNow - record.EngagedAtUtc).TotalSeconds;
            record.ServerForecastCacheHit = result.CacheHit;
            if (result.Prediction is not { } prediction)
            {
                record.ServerForecastError = result.Error;
                return;
            }
            if (prediction.UnsupportedEffects) notes = notes.Append("status effects appeared and were ignored").ToList();
            // DamagePerAction: the duel's own mean (what one real duel is compared with), not the pooled per-hit mean.
            record.ServerForecast = new ForecastSummary(prediction.WinRate, prediction.Attacks.Mean, prediction.Seconds.Mean,
                prediction.DamageTaken.Mean, prediction.Attacks.StdDev, prediction.Seconds.StdDev, prediction.DamageTaken.StdDev,
                prediction.DuelDamagePerAction.Mean, notes.Count > 0 ? string.Join("; ", notes) : null, prediction.SkillCasts.Mean);
        }
    }

    /// <summary>The server simulation of fighting <paramref name="target"/> from this tick's state. Engagement records
    /// and On-mode decisions build it the same way, so a decision's forecast is the cache entry its duel record gets.</summary>
    private DuelRequest ServerRequest(Snapshot snap, MonsterInfo target, PlayerBuild build, out string? policyNote, out string policyKey)
    {
        var rules = GetSkillRules().Where(r => r.Target == SkillCastTarget.Enemy).ToList();
        var policy = ScriptedSkillPolicy(snap, target, rules, out policyNote, out policyKey);
        return new DuelRequest(build, target.ClassId,
            PlayerHp: snap.SelfHp, PlayerSp: snap.SelfSp, MonsterHp: target.MaxHp > 0 ? target.Hp : 0,
            Policy: policy, MonsterAttacksFirst: _bot.HasHitMe(target.Id), Trials: ShadowTrials, MaxSeconds: MaxFightSeconds,
            PlayerStatuses: _bot.OwnStatuses(MaxFightSeconds, out _)); // unsimulated ones: ServerUnsupported, or the record's note
    }

    private const double MaxFightSeconds = 300; // DuelRequest's default

    // ---- ServerForecast On: decisions ----

    private const int ShortlistSize = 4; // PickTarget's nearest huntable monsters get a server forecast
    // ponytail: guessed. Monster skill damage below this share of its total is within the duel validator's tolerance
    // (shadow duels against Caramel, Metaller, Drainliar stayed consistent); re-fit from per-monster On-mode errors.
    private const double MaxMonsterSkillShare = 0.2;

    private readonly Dictionary<int, Task<ForecastResult>> _serverInFlight = new(); // by monster id; tick thread only
    private Snapshot? _buildSnap;
    private (PlayerBuild Build, LiveStats Live)? _snapBuild;
    private (string Source, BattleForecast Old)? _decision; // PickTarget's chosen engagement, for its duel record
    private readonly HashSet<int> _disagreementLogged = new();
    private string _lastPickChange = "";

    private HashSet<int>? ServerShortlist(Snapshot snap) => _config.ServerForecast != ServerForecastMode.On ? null
        : snap.Monsters.Where(IsHuntable).OrderBy(m => Dist(snap.SelfPos, m.Pos)).Take(ShortlistSize).Select(m => m.Id).ToHashSet();

    /// <summary>Every tick, fighting included: queue the shortlist's simulations for the exact current state. PickTarget
    /// runs a tick after a kill, and HP and SP usually settled before the last blow, so that state is often cached by
    /// then. Monsters in combat aren't simulated (see ServerLookup); one in flight per monster bounds the load.</summary>
    private void PrefetchServerForecasts(Snapshot snap)
    {
        if (ServerShortlist(snap) is not { } shortlist) return;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var m in snap.Monsters)
            if (shortlist.Contains(m.Id)) ServerLookup(snap, m, Forecast(snap, m), out _);
        DecisionTiming.PrefetchMs.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>The forecast a decision uses. Off and Shadow: the old one. On: the server's, when its result for this
    /// exact state is cached; otherwise the old one, and <paramref name="source"/> says why (pending, unsupported,
    /// failed). Never waits: a miss queues the simulation and a later tick uses it if nothing changed.</summary>
    private BattleForecast DecisionForecast(Snapshot snap, MonsterInfo m, BattleForecast old, string use, out string source)
    {
        source = "old";
        if (_config.ServerForecast != ServerForecastMode.On) return old;
        var forecast = ServerLookup(snap, m, old, out source);
        ForecastWorker.Count($"{use}: {(source.StartsWith("old: failed") ? "old: failed" : source)}");
        if (source == "server" && forecast.CanWin != old.CanWin)
        {
            ForecastWorker.Count($"{use}: disagree, server says {(forecast.CanWin ? "win" : "lose")}");
            if (_disagreementLogged.Count > 500) _disagreementLogged.Clear();
            if (_disagreementLogged.Add(m.Id))
                OnLog?.Invoke($"Forecasts disagree on {m.Name} ({use}, my HP {snap.SelfHp}): old says {(old.CanWin ? "win" : "lose")} "
                    + $"(~{old.ExpectedDamageTaken:F0} HP lost, {old.AttacksToKill} attacks), server says {(forecast.CanWin ? "win" : "lose")} "
                    + $"(~{forecast.ExpectedDamageTaken:F0} HP lost, {forecast.AttacksToKill} attacks, win {forecast.WinChancePercent:F0}%).");
        }
        return forecast;
    }

    private BattleForecast ServerLookup(Snapshot snap, MonsterInfo m, BattleForecast old, out string source)
    {
        try
        {
            if (!ServerCombatRuntime.IsInitialized) { ForecastWorker.Start(); source = "old: pending (simulator starting)"; return old; }
            // Mid-fight every hit is a new state, so the server's answer is nearly always one tick behind: the verdict
            // flipped between engines each tick (engage, flee, engage) on an Orc Warrior. Contact keeps the old forecast.
            // HasHitMe goes by damage attribution, which can credit a neighbour's hits to the last attacker; a monster at
            // melee distance counts as in contact too.
            if (m.Id == _targetId || _bot.HasHitMe(m.Id) || Dist(snap.SelfPos, m.Pos) <= 2) { source = "old: in combat (state changes every hit)"; return old; }
            if (ServerUnsupported(snap, m, old) is { } why) { source = $"old: unsupported ({why})"; return old; }
            // One simulation per monster in flight: a fight changes HP every hit, and each state is a new request.
            if (_serverInFlight.TryGetValue(m.Id, out var flight) && !flight.IsCompleted) { source = "old: pending"; return old; }
            var request = ServerRequest(snap, m, BuildOf(snap).Build, out var policyNote, out var policyKey);
            if (policyNote != null) { source = $"old: unsupported ({policyNote})"; return old; }
            var task = ForecastWorker.Submit(request, policyKey, ForecastPriority.Urgent);
            if (!task.IsCompleted) { _serverInFlight[m.Id] = task; source = "old: pending"; return old; }
            _serverInFlight.Remove(m.Id);
            if (task.Result.Prediction is not { } p) { source = $"old: failed ({task.Result.Error?.Split('\n')[0]})"; return old; }
            if (p.UnsupportedEffects) { source = "old: unsupported (status effects in the simulation)"; return old; }
            // The simulated character must be the one the server reports: after a reconnect Bot3 still had an Awakening
            // Potion it couldn't see (AttackDelay 0.819 live, 0.967 simulated).
            if (DuelValidator.StatMismatches(p.Sheet, BuildOf(snap).Live) is [var mismatch, ..])
            {
                source = $"old: unsupported (simulated stats differ from the server's: {mismatch.Split(':')[0]})";
                return old;
            }
            source = "server";
            return ServerBattleForecast(snap, old, p);
        }
        catch (Exception e)
        {
            source = $"old: failed ({e.GetType().Name}: {e.Message})";
            return old;
        }
    }

    /// <summary>Why the server simulation can't stand for this fight; the duel validator excludes the same cases.</summary>
    private string? ServerUnsupported(Snapshot snap, MonsterInfo m, BattleForecast old)
    {
        if (_bot.InParty) return "in a party";
        if (_bot.OwnStatuses(MaxFightSeconds, out var statusWhy) == null) return $"own status effects ({statusWhy})";
        if (InferAttackRange(snap) > 1) return "ranged attacker";
        if (_config.NoAutoAttack) return "no auto-attack";
        if (!DuelSimulator.IsKnownMonster(m.ClassId)) return "monster unknown to the server data";
        if (IsMonsterImmobile(_data?.Monster(m.ClassId))) return "fixed monster";
        // The simulated monster only auto-attacks. Where the old model says its casts are a real share of its damage,
        // or it has hit us far harder than a swing, the server forecast would miss that damage.
        var skillDps = BuildMonsterMods(snap, m).MonsterSkillDps;
        var meleeDps = old.MonsterDamagePerHit * old.MonsterHitChance 
            / (_data?.MonsterAttackDelay(m.ClassId) is > 0.05 and var delay ? delay : 1.6); // BattleSimulator's default interval
        if (skillDps > MaxMonsterSkillShare * (skillDps + meleeDps)) return "monster skills";
        if (_bot.MaxHitTakenFrom(m.Name) > old.MonsterDamagePerHit * 2) return "unexplained hits above its swing";
        if (HealingOn(snap, m) > 0) return "healer near the target";
        return null;
    }

    private (PlayerBuild Build, LiveStats Live) BuildOf(Snapshot snap)
    {
        if (!ReferenceEquals(_buildSnap, snap)) { _snapBuild = _bot.CaptureCombatState(); _buildSnap = snap; }
        return _snapBuild!.Value;
    }

    /// <summary>The old forecast with the server's numbers. CanWin keeps the old rule (expected HP lost within
    /// WinMargin of HP plus the potion buffer, attacks within MaxRoundsToKill) on the simulated means, and without
    /// potions also caps simulated deaths and timeouts.</summary>
    private BattleForecast ServerBattleForecast(Snapshot snap, BattleForecast old, DuelPrediction p)
    {
        var buffer = EstimatePotionHpBuffer(snap);
        var canWin = HuntPolicy.ServerCanWin(p, snap.SelfHp, buffer, _config.WinMargin, _config.MaxRoundsToKill);
        return old with
        {
            CanWin = canWin, SecondsToKillMonster = p.Seconds.Mean, ExpectedDamageTaken = p.DamageTaken.Mean,
            AttacksToKill = (int)Math.Ceiling(p.Attacks.Mean), WinChancePercent = 100 * p.WinRate, MyHitChance = 1 - p.MissRate,
        };
    }

    /// <summary>Counts and logs when the server forecast changed PickTarget's choice from what the old one picks.</summary>
    private void NoteServerPick(MonsterInfo? chosen, MonsterInfo? oldPick)
    {
        if (chosen?.Id == oldPick?.Id) return;
        ForecastWorker.Count(chosen == null ? "pick: changed, server idles" : oldPick == null ? "pick: changed, old would idle" : "pick: changed target");
        var line = $"Server forecast changed the pick: {chosen?.Name ?? "nothing"} instead of {oldPick?.Name ?? "nothing"} (old).";
        if (line != _lastPickChange) OnLog?.Invoke(_lastPickChange = line);
    }

    /// <summary>The script's enemy skill rules as the simulator's action policy: per attack slot, the first rule
    /// off its local throttle whose conditions hold and that SkillPolicy finds worth its SP, exactly as
    /// TickSkillsAsync decides. Rules for skills the simulator doesn't run are skipped and named in the note.
    /// The policy reads only a copy of this tick's snapshot. <paramref name="key"/> names everything it depends on
    /// besides the moment (rules, levels, the engagement values of the conditions the simulator doesn't drive,
    /// auto-attack damage, attack interval, INT), so equal keys choose identically: ForecastWorker caches on it.</summary>
    private ActionPolicy? ScriptedSkillPolicy(Snapshot snap, MonsterInfo target, List<SkillRule> rules, out string? note, out string key)
    {
        note = null;
        key = "none";
        var usable = new List<(SkillRule Rule, int Level)>();
        foreach (var rule in rules)
        {
            if (!KnowsSkill(rule.Skill, out var known)) continue;
            if (!DuelSimulator.IsSimulated(rule.Skill))
            {
                note = (note == null ? "" : note + ", ") + $"{rule.Skill} not simulated";
                continue;
            }
            usable.Add((rule, rule.Level > 0 ? Math.Min(rule.Level, known) : known));
        }
        if (usable.Count == 0) return null;

        var targetForecast = Forecast(snap, target);
        var autoDamage = _config.NoAutoAttack ? 0 : targetForecast.ExpectedDamagePerSwing; // PolicyInputs
        var hitDamage = _config.NoAutoAttack ? 0 : targetForecast.ExpectedDamagePerHit;
        var interval = snap.SelfAttackInterval > 0.05 ? snap.SelfAttackInterval : 1.5;
        var probe = snap.Clone();
        var selfInt = snap.SelfInt;
        key = string.Join(";", usable.Select(u => $"{u.Rule.Skill}:{u.Level}:{u.Rule.Level > 0}:{u.Rule.EveryCooldown:R}["
            + string.Join(",", u.Rule.Conditions.Select(c => $"{c.Lhs}{c.Op}{c.Value:R}"
                + (MomentConditions.Contains(c.Lhs) ? "" : $"={LhsValue(c.Lhs, probe, target):R}"))) + "]"))
            + $"|auto={autoDamage:R}|hit={hitDamage:R}|interval={interval:R}|int={selfInt}|maxhp={probe.SelfMaxHp}|maxsp={probe.SelfMaxSp}";
        return (in DuelMoment moment) =>
        {
            probe.SelfHp = moment.Hp;
            probe.SelfSp = moment.Sp;
            var probeTarget = target with { Hp = moment.MonsterHp, MaxHp = moment.MonsterMaxHp };
            foreach (var (rule, level) in usable)
            {
                if (moment.SecondsSinceLastSkill < (rule.EveryCooldown > 0 ? rule.EveryCooldown : 1.5)) continue;
                if (FailedCondition(rule.Conditions, probe, probeTarget) != null) continue;
                var chosen = SkillPolicy.ChooseLevel(rule.Skill, level, rule.Level > 0, _data?.SkillInfo(rule.Skill)?.SpCost,
                    moment.MonsterHp, false, autoDamage, moment.Sp, SkillPolicy.SpIsFree(moment.Sp, moment.MaxSp, selfInt),
                    null, interval, hitDmg: hitDamage);
                if (chosen > 0) return new PlayerAction(rule.Skill, chosen);
            }
            return PlayerAction.AutoAttack;
        };
    }
}
