using RebuildSharedData.Enum;
using RoBotClient.Simulation;

namespace RoBotClient.Bot.Behavior;

// Shadow mode for the server-code duel simulator (RoBotClient.Simulation). Every engagement records the bot's combat
// state, the old BattleSimulator forecast and the server-code forecast into a DuelRecord; BotSession appends what
// really happened and BotManager stores it under bot-duels/ for DuelValidator. Decisions still use Forecast().
public sealed partial class BotBehavior
{
    private const int ShadowTrials = 300;

    // Loads the server's data and Script.dll once per process; null when ready, otherwise why it isn't.
    private static readonly Lazy<string?> ServerSimulatorError = new(() =>
    {
        try
        {
            var directory = ServerCombatRuntime.FindServerProjectDirectory(AppContext.BaseDirectory)
                            ?? ServerCombatRuntime.FindServerProjectDirectory(Environment.CurrentDirectory);
            if (directory == null) return "RoRebuildServer project directory not found";
            ServerCombatRuntime.EnsureInitialized(directory);
            return null;
        }
        catch (Exception e)
        {
            return $"{e.GetType().Name}: {e.Message}";
        }
    });

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
            if (ServerSimulatorError.Value is { } error)
                record.ServerForecastError = error;
            else
            try
            {
                var prediction = DuelSimulator.Simulate(new DuelRequest(build, target.ClassId,
                    PlayerHp: snap.SelfHp, PlayerSp: snap.SelfSp, MonsterHp: target.MaxHp > 0 ? target.Hp : 0,
                    Policy: ScriptedSkillPolicy(snap, target, rules, out var policyNote),
                    MonsterAttacksFirst: _bot.HasHitMe(target.Id), Trials: ShadowTrials));
                var notes = new List<string>();
                if (policyNote != null) notes.Add(policyNote);
                if (snap.SelfStatuses.Count > 0) notes.Add("own status effects not simulated");
                if (prediction.UnsupportedEffects) notes.Add("status effects appeared and were ignored");
                record.ServerForecast = new ForecastSummary(prediction.WinRate, prediction.Attacks.Mean, prediction.Seconds.Mean,
                    prediction.DamageTaken.Mean, prediction.Attacks.StdDev, prediction.Seconds.StdDev, prediction.DamageTaken.StdDev,
                    prediction.DamagePerAction.Mean, notes.Count > 0 ? string.Join("; ", notes) : null);
            }
            catch (Exception e)
            {
                // Kept with the record (and its Build) so the failure can be replayed offline.
                record.ServerForecastError = e.ToString();
            }
            _bot.BeginDuel(record);
        }
        catch (Exception e)
        {
            OnLog?.Invoke($"duel record error: {e}");
        }
    }

    /// <summary>The script's enemy skill rules as the simulator's action policy: per attack slot, the first rule
    /// off its local throttle whose conditions hold and that SkillPolicy finds worth its SP, exactly as
    /// TickSkillsAsync decides. Rules for skills the simulator doesn't run are skipped and named in the note.</summary>
    private ActionPolicy? ScriptedSkillPolicy(Snapshot snap, MonsterInfo target, List<SkillRule> rules, out string? note)
    {
        note = null;
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
                    moment.MonsterHp, false, autoDamage, moment.Sp, SkillPolicy.SpIsFree(moment.Sp, moment.MaxSp, snap.SelfInt),
                    null, interval);
                if (chosen > 0) return new PlayerAction(rule.Skill, chosen);
            }
            return PlayerAction.AutoAttack;
        };
    }
}
