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
                SelfStatuses = snap.SelfStatuses.Select(s => s.ToString()).ToList(),
                SkillRules = rules.Select(r => $"{r.Skill} {r.Level}" + (r.EveryCooldown > 0 ? $" every {r.EveryCooldown}" : "")
                    + (r.Conditions.Count > 0 ? " if " + string.Join(" and ", r.Conditions.Select(c => $"{c.Lhs} {c.Op} {c.Value}")) : "")).ToList(),
                Build = build,
                Live = live,
                OldForecast = new ForecastSummary(forecast.CanWin ? 1 : 0, forecast.AttacksToKill, forecast.SecondsToKillMonster,
                    forecast.ExpectedDamageTaken, DamagePerAction: forecast.ExpectedDamagePerSwing,
                    Note: "deterministic: WinRate is CanWin"),
            };
            var policy = ScriptedSkillPolicy(snap, target, rules, out var policyNote, out var policyKey);
            var notes = new List<string>();
            if (policyNote != null) notes.Add(policyNote);
            if (snap.SelfStatuses.Count > 0) notes.Add("own status effects not simulated");
            var request = new DuelRequest(build, target.ClassId,
                PlayerHp: snap.SelfHp, PlayerSp: snap.SelfSp, MonsterHp: target.MaxHp > 0 ? target.Hp : 0,
                Policy: policy, MonsterAttacksFirst: _bot.HasHitMe(target.Id), Trials: ShadowTrials);
            record.PendingForecast = ForecastWorker.Submit(request, policyKey, ForecastPriority.Urgent)
                .ContinueWith(t => AttachServerForecast(record, t.Result, notes), TaskContinuationOptions.ExecuteSynchronously);
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
                prediction.DuelDamagePerAction.Mean, notes.Count > 0 ? string.Join("; ", notes) : null);
        }
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

        var autoDamage = _config.NoAutoAttack ? 0 : Forecast(snap, target).ExpectedDamagePerSwing; // PolicyInputs
        var interval = snap.SelfAttackInterval > 0.05 ? snap.SelfAttackInterval : 1.5;
        var probe = snap.Clone();
        var selfInt = snap.SelfInt;
        key = string.Join(";", usable.Select(u => $"{u.Rule.Skill}:{u.Level}:{u.Rule.Level > 0}:{u.Rule.EveryCooldown:R}["
            + string.Join(",", u.Rule.Conditions.Select(c => $"{c.Lhs}{c.Op}{c.Value:R}"
                + (MomentConditions.Contains(c.Lhs) ? "" : $"={LhsValue(c.Lhs, probe, target):R}"))) + "]"))
            + $"|auto={autoDamage:R}|interval={interval:R}|int={selfInt}|maxhp={probe.SelfMaxHp}|maxsp={probe.SelfMaxSp}";
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
                    null, interval);
                if (chosen > 0) return new PlayerAction(rule.Skill, chosen);
            }
            return PlayerAction.AutoAttack;
        };
    }
}
