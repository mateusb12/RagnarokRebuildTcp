using System.Text;
using System.Text.Json;
using RebuildSharedData.Enum;

namespace RoBotClient.Simulation;

/// <summary>One metric of one group of duels: observed against predicted, per duel.</summary>
/// <param name="Bias">Mean of (observed - predicted). Positive: the real fight took more of it.</param>
/// <param name="BiasLow">Lower end of the 95% confidence interval of the bias (t distribution).</param>
/// <param name="Dispersion">Spread of the residuals over the spread the simulator predicted for them. 1 when the
/// simulator's randomness matches the server's; well above 1 means real fights vary for reasons it doesn't model.</param>
public sealed record MetricCheck(string Metric, int Count, double MeanObserved, double MeanPredicted, double Bias,
    double BiasLow, double BiasHigh, double MeanAbsoluteError, double Dispersion, double PracticalThreshold, string Verdict);

public sealed record GroupCheck(string Dimension, string Group, int Duels, IReadOnlyList<MetricCheck> Metrics);

/// <summary>The offline reconstruction of a build against what the server reported for it.</summary>
public sealed record StaticCheck(string Character, string Build, int Duels, IReadOnlyList<string> Mismatches);

/// <summary>Forecast error against the observed duel, for the old BattleSimulator and the server-code simulator.</summary>
public sealed record ForecastComparison(string Metric, int Count, double OldBias, double OldMeanAbsoluteError,
    double ServerBias, double ServerMeanAbsoluteError);

public sealed record ValidationReport(
    int Records, IReadOnlyDictionary<string, int> Outcomes, IReadOnlyDictionary<string, int> Exclusions, int Eligible,
    IReadOnlyList<StaticCheck> StaticChecks, IReadOnlyList<GroupCheck> Groups, IReadOnlyList<ForecastComparison> Forecasts,
    IReadOnlyDictionary<string, int> ForecastAvailability, IReadOnlyList<string> Alerts, int Deaths, double PredictedDeaths, TimeSpan Elapsed);

/// <summary>
/// Checks the server-code simulator against duels real bots fought on the live server (DuelRecord, bot-duels/).
///
/// Mechanics are checked by replay: each eligible duel is re-simulated from the bot's recorded build, HP, SP and
/// its recorded sequence of actions (auto-attack or skill, per attack slot), so the bot's decisions are not under
/// test, only the combat they set off. Metrics, all over the same window, from our first attack action to the
/// killing blow (the monster's death packet):
///   Attacks         attack actions we made (swings and skill casts, misses included; a double attack is one action)
///   Seconds         first action to kill
///   DamageTaken     gross damage of the target's attacks on us in the window (before healing and regeneration)
///   DamagePerAction mean damage of our actions on the target (misses as 0; a double attack's two hits summed)
/// Death and win rates are compared over complete duels (won or died).
///
/// Each duel gives (observed, predicted mean, predicted spread). Per group: bias with a 95% confidence interval,
/// mean absolute error and the dispersion (RMS z-score: about 1 when duels scatter as predicted). A group is judged only with at least MinimumDuels duels.
/// </summary>
public static class DuelValidator
{
    /// <summary>Below this many duels a group's verdict is "insufficient evidence": the t interval of a smaller
    /// sample is too wide to tell a 10% bias from noise for most metrics, and the residuals' normality (which the
    /// interval assumes) can't be leaned on.</summary>
    public const int MinimumDuels = 10;

    /// <summary>A bias is reported as a discrepancy only when its 95% interval excludes zero (it is real) AND it
    /// exceeds this share of the predicted mean (it matters). 10%: the bot ranks targets and maps by exp per second,
    /// which moves one-for-one with kill time; differences below ~10% are within the run-to-run variation of a
    /// hunting session and rarely change which target or map is chosen.</summary>
    public const double PracticalRelativeThreshold = 0.10;

    /// <summary>Absolute floors under the relative threshold, so tiny means don't turn noise-sized differences into
    /// alerts: one attack action, one second (about one attack interval), 25 HP of damage taken, 5 damage per action.</summary>
    private static readonly Dictionary<string, double> PracticalFloor = new()
    {
        ["Attacks"] = 1, ["Seconds"] = 1, ["DamageTaken"] = 25, ["DamagePerAction"] = 5,
    };

    public const int TrialsPerDuel = 400;

    private static readonly string[] JobNames = { "Novice", "Swordsman", "Archer", "Mage", "Acolyte", "Thief", "Merchant" };

    public static ValidationReport Validate(IReadOnlyList<DuelRecord> records)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var outcomes = records.GroupBy(r => r.Outcome.ToString()).ToDictionary(g => g.Key, g => g.Count());
        var exclusions = new Dictionary<string, int>();
        var replays = new List<(DuelRecord Record, DuelPrediction Prediction)>();
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            if (ExclusionReason(record) is { } reason)
            {
                exclusions[reason] = exclusions.GetValueOrDefault(reason) + 1;
                continue;
            }
            replays.Add((record, Replay(record, seed: index)));
        }

        var groups = new List<GroupCheck> { Check("all", "all", replays) };
        foreach (var (dimension, key) in new (string, Func<DuelRecord, string>)[]
                 {
                     ("class", r => JobName(r.Build!.JobId)), ("monster", r => r.MonsterName), ("skill", SkillMix),
                     ("weapon", Weapon), ("character", r => r.Character),
                 })
            groups.AddRange(replays.GroupBy(x => key(x.Record)).OrderBy(g => g.Key)
                .Select(g => Check(dimension, g.Key, g.ToList())));

        var alerts = groups.SelectMany(g => g.Metrics.Where(m => m.Verdict.StartsWith("DISCREPANCY") || m.Verdict.StartsWith("DISPERSION"))
            .Select(m => $"{g.Dimension}={g.Group} {m.Metric}: {m.Verdict} (bias {m.Bias:+0.##;-0.##} [{m.BiasLow:0.##}, {m.BiasHigh:0.##}], n={m.Count})")).ToList();
        var complete = replays.Where(x => x.Record.Outcome is DuelOutcome.Won or DuelOutcome.Died).ToList();
        return new ValidationReport(records.Count, outcomes, exclusions, replays.Count, StaticChecks(records),
            groups, CompareForecasts(replays.Select(x => x.Record).ToList()), ForecastAvailability(replays.Select(x => x.Record)), alerts,
            complete.Count(x => x.Record.Outcome == DuelOutcome.Died), complete.Sum(x => x.Prediction.DeathRate), stopwatch.Elapsed);
    }

    /// <summary>Why a duel can't be compared with a clean 1v1 simulation, or null if it can.</summary>
    public static string? ExclusionReason(DuelRecord record)
    {
        if (record.Build == null || record.Live == null) return "no build recorded";
        if (record.Outcome is DuelOutcome.Abandoned or DuelOutcome.LostSight or DuelOutcome.Stolen)
            return $"incomplete ({record.Outcome})";
        if (record.Actions.Count == 0) return "no attack action of ours";
        if (record.BuildAtEnd != null) return "build changed during the duel (equipment, level or skills)";
        if (record.InParty) return "in a party";
        if (record.HelpedByPlayer) return "another player attacked the target";
        if (record.MonsterMaxHpAtEngage > 0 && record.MonsterHpAtEngage < record.MonsterMaxHpAtEngage) return "target already damaged";
        if (record.OtherAttackers > 0) return "other monsters attacked us";
        // A status the simulator was given (OwnStatuses) is run by the server's own handler; the static fidelity check
        // compares what it does to the stats (AttackDelay for an attack speed potion) with the server's live values.
        if (record.SelfStatuses.Any(s => record.OwnStatuses?.Any(o => o.Status.ToString() == s) != true)) return "own status effects at engage";
        // Poison on the target is simulated (Envenom); any other status change is not.
        if (record.StatusChanges.Any(change => !change.EndsWith("target +Poison") && !change.EndsWith("target -Poison")))
            return "status effects during the duel";
        if (record.Actions.FirstOrDefault(a => a.Skill != CharacterSkill.None && !DuelSimulator.IsSimulated(a.Skill)) is { } other)
            return $"unsimulated skill ({other.Skill})";
        if (!DuelSimulator.IsKnownMonster(record.MonsterClassId)) return "monster class unknown to the server data";
        if (DuelSimulator.Inspect(record.Build, record.MonsterClassId).WeaponClass == (int)WeaponClass.Bow)
            return "ranged attacker (the simulator assumes melee contact)";
        return null;
    }

    /// <summary>The duel re-simulated with the bot's recorded actions, slot by slot; auto-attack past their end.</summary>
    public static DuelPrediction Replay(DuelRecord record, int seed, int trials = TrialsPerDuel)
    {
        var actions = record.Actions;
        var firstAction = record.FirstActionAt ?? 0;
        return DuelSimulator.Simulate(new DuelRequest(record.Build!, record.MonsterClassId,
            // Every recorded cast was paid for on the server, SP regenerated mid-fight included (the simulator has no
            // regeneration), so the replay must not drop casts for SP: give it SP to spare.
            PlayerHp: record.HpAtEngage, PlayerSp: 1_000_000,
            Policy: (in DuelMoment moment) => moment.ActionIndex < actions.Count
                ? new PlayerAction(actions[moment.ActionIndex].Skill, actions[moment.ActionIndex].Level)
                : PlayerAction.AutoAttack,
            MonsterAttacksFirst: record.TargetActions.Any(a => a.At <= firstAction),
            Trials: trials, Seed: seed, PlayerStatuses: record.OwnStatuses));
    }

    /// <summary>Observed value of each metric for a won duel, null when the duel doesn't define it.</summary>
    public static (double Attacks, double Seconds, double DamageTaken, double DamagePerAction) Observe(DuelRecord record)
    {
        var start = record.FirstActionAt ?? 0;
        return (record.Actions.Count, record.EndedAt - start,
            record.TargetActions.Where(a => a.At >= start && a.At <= record.EndedAt).Sum(a => a.Damage),
            record.Actions.Average(a => a.Damage));
    }

    private static GroupCheck Check(string dimension, string group, List<(DuelRecord Record, DuelPrediction Prediction)> duels)
    {
        var won = duels.Where(x => x.Record.Outcome == DuelOutcome.Won && x.Prediction.Attacks.Count > 0).ToList();
        var observed = won.Select(x => Observe(x.Record)).ToList();
        var metrics = new List<MetricCheck>
        {
            Metric("Attacks", won.Select((x, i) => (observed[i].Attacks, x.Prediction.Attacks.Mean, x.Prediction.Attacks.StdDev))),
            Metric("Seconds", won.Select((x, i) => (observed[i].Seconds, x.Prediction.Seconds.Mean, x.Prediction.Seconds.StdDev))),
            Metric("DamageTaken", won.Select((x, i) => (observed[i].DamageTaken, x.Prediction.DamageTaken.Mean, x.Prediction.DamageTaken.StdDev))),
            // Compared with the simulated duels' own means, not per-action spread / sqrt(n): the kill ends a duel, so
            // its length depends on its damage (one crit can end a plant duel at 410 per action).
            Metric("DamagePerAction", won.Select((x, i) => (observed[i].DamagePerAction, x.Prediction.DuelDamagePerAction.Mean,
                x.Prediction.DuelDamagePerAction.StdDev))),
        };
        return new GroupCheck(dimension, group, duels.Count, metrics);
    }

    private static MetricCheck Metric(string name, IEnumerable<(double Observed, double Predicted, double PredictedStdDev)> items)
    {
        var list = items.ToList();
        var count = list.Count;
        if (count == 0) return new MetricCheck(name, 0, 0, 0, 0, 0, 0, 0, 0, 0, "insufficient evidence (n=0)");
        var residuals = list.Select(x => x.Observed - x.Predicted).ToList();
        var bias = residuals.Average();
        var residualStdDev = count > 1 ? Math.Sqrt(residuals.Sum(r => (r - bias) * (r - bias)) / (count - 1)) : 0;
        var halfWidth = count > 1 ? TCritical(count - 1) * residualStdDev / Math.Sqrt(count) : double.PositiveInfinity;
        // RMS of each duel's z-score (residual / its predicted spread): groups mix monsters on very different scales
        // (a plant at ~2 per action, others at ~200), so pooled spreads would be dominated by the large ones.
        // Duels predicted with no spread at all (every trial identical) can't be scored and are left out.
        var scored = list.Where(x => x.PredictedStdDev > 0).Select(x => (x.Observed - x.Predicted) / x.PredictedStdDev).ToList();
        var dispersion = scored.Count > 0 ? Math.Sqrt(scored.Average(z => z * z)) : double.NaN;
        var meanPredicted = list.Average(x => x.Predicted);
        var threshold = Math.Max(PracticalRelativeThreshold * Math.Abs(meanPredicted), PracticalFloor[name]);
        var mae = residuals.Average(Math.Abs);
        var significant = bias - halfWidth > 0 || bias + halfWidth < 0;
        var verdict = count < MinimumDuels ? $"insufficient evidence (n={count} < {MinimumDuels})"
            : significant && Math.Abs(bias) >= threshold ? $"DISCREPANCY: bias beyond {threshold:0.##} and significant"
            : significant ? "small bias (significant, below the practical threshold)"
            // Dispersion is judged from 20 duels: the ratio of two spreads estimated from fewer is itself too noisy.
            // It alerts only when the typical per-duel error (MAE) also reaches the practical threshold: a nearly
            // deterministic prediction (bare hands: same damage every hit) has a spread near 0, so one crit makes the
            // ratio huge while every duel is still predicted within a fraction of the threshold.
            : count >= 2 * MinimumDuels && (dispersion > 1.5 || dispersion < 0.67) && mae >= threshold ? $"DISPERSION {dispersion:0.##}: spread not as predicted"
            : count >= 2 * MinimumDuels && (dispersion > 1.5 || dispersion < 0.67) ? $"spread ratio {dispersion:0.##} (MAE below the practical threshold)"
            : "consistent";
        return new MetricCheck(name, count, list.Average(x => x.Observed), meanPredicted, bias, bias - halfWidth, bias + halfWidth,
            mae, dispersion, threshold, verdict);
    }

    // Two-sided 95% critical values of Student's t.
    private static double TCritical(int degreesOfFreedom) => degreesOfFreedom switch
    {
        1 => 12.71, 2 => 4.30, 3 => 3.18, 4 => 2.78, 5 => 2.57, 6 => 2.45, 7 => 2.36, 8 => 2.31, 9 => 2.26, 10 => 2.23,
        < 15 => 2.16, < 20 => 2.11, < 30 => 2.06, < 60 => 2.02, _ => 1.98,
    };

    private static List<StaticCheck> StaticChecks(IReadOnlyList<DuelRecord> records) => records
        .Where(r => r.Build != null && r.Live != null && DuelSimulator.IsKnownMonster(r.MonsterClassId))
        .GroupBy(r => (r.Character, Build: JsonSerializer.Serialize(r.Build!.CombatOnly()), Live: JsonSerializer.Serialize(r.Live),
            Statuses: string.Join(",", r.OwnStatuses?.Select(s => $"{s.Status}:{s.Value1}") ?? [])))
        .Select(g =>
        {
            var record = g.First();
            var mismatches = StatMismatches(DuelSimulator.Inspect(record.Build!, record.MonsterClassId, record.OwnStatuses), record.Live!);
            return new StaticCheck(g.Key.Character, Describe(record.Build!) + (g.Key.Statuses.Length > 0 ? $" with {g.Key.Statuses}" : ""), g.Count(), mismatches);
        }).ToList();

    /// <summary>Where the simulated character's stats differ from the ones the live server sent the bot: a status or
    /// item the simulator wasn't given (a buff the bot can't see after a reconnect), or a build read wrongly.</summary>
    public static List<string> StatMismatches(CombatSheet sheet, LiveStats live)
    {
        var mismatches = new List<string>();
        void Compare(string stat, double offline, double server, double tolerance = 0)
        {
            if (Math.Abs(offline - server) > tolerance) mismatches.Add($"{stat}: offline {offline:0.###}, server {server:0.###}");
        }
        Compare("MaxHp", sheet.MaxHp, live.MaxHp);
        Compare("MaxSp", sheet.MaxSp, live.MaxSp);
        Compare("Attack", sheet.ClientAttackMin, live.AttackMin);
        Compare("Attack2", sheet.ClientAttackMax, live.AttackMax);
        Compare("Def", sheet.Def, live.Def);
        Compare("MDef", sheet.MDef, live.MDef);
        Compare("AddHit", sheet.AddHit, live.Hit);
        Compare("AddFlee", sheet.AddFlee, live.Flee);
        Compare("AddCrit", sheet.AddCrit, live.Crit);
        Compare("PerfectDodge", sheet.PerfectDodge, live.PerfectDodge);
        Compare("AttackDelay", sheet.AttackDelay, live.AttackSpeed, 0.001); // sent as a float
        return mismatches;
    }

    /// <summary>A live server forecast that was ready before the duel ended: a real prediction. One that arrived
    /// later (the worker was busy) was still computed from the state at engagement, but is reported apart and
    /// kept out of the forecast comparison. Records from before the worker have no ready time: they were computed
    /// synchronously at engagement.</summary>
    public static bool ForecastBeforeOutcome(DuelRecord record) =>
        record.ServerForecast != null && (record.ServerForecastReadyAt == null || record.ServerForecastReadyAt <= record.EndedAt);

    private static Dictionary<string, int> ForecastAvailability(IEnumerable<DuelRecord> eligible) => eligible
        .Where(r => r.Outcome == DuelOutcome.Won)
        .GroupBy(r => ForecastBeforeOutcome(r) ? "ready before the outcome"
            : r.ServerForecast != null ? "ready only after the outcome (excluded below)"
            : r.ServerForecastError != null ? "failed" : "missing")
        .ToDictionary(g => g.Key, g => g.Count());

    private static List<ForecastComparison> CompareForecasts(List<DuelRecord> eligible)
    {
        var both = eligible.Where(r => r.Outcome == DuelOutcome.Won && r.OldForecast != null && ForecastBeforeOutcome(r)).ToList();
        // Old forecasts don't predict skill casts (NaN columns); server forecasts made before SkillCasts was recorded are left out.
        ForecastComparison Compare(string metric, Func<ForecastSummary, double?> predicted, Func<DuelRecord, double> observed)
        {
            var duels = both.Where(r => predicted(r.ServerForecast!) != null).ToList();
            if (duels.Count == 0) return new ForecastComparison(metric, 0, 0, 0, 0, 0);
            var oldErrors = duels.Select(r => observed(r) - (predicted(r.OldForecast!) ?? double.NaN)).ToList();
            var serverErrors = duels.Select(r => observed(r) - predicted(r.ServerForecast!)!.Value).ToList();
            return new ForecastComparison(metric, duels.Count, oldErrors.Average(), oldErrors.Average(Math.Abs),
                serverErrors.Average(), serverErrors.Average(Math.Abs));
        }
        static int Casts(DuelRecord r) => r.Actions.Count(a => a.Skill != CharacterSkill.None);
        return new List<ForecastComparison>
        {
            Compare("Attacks", f => f.Attacks, r => Observe(r).Attacks),
            Compare("Seconds", f => f.Seconds, r => Observe(r).Seconds),
            Compare("DamageTaken", f => f.DamageTaken, r => Observe(r).DamageTaken),
            Compare("DamagePerAction", f => f.DamagePerAction, r => Observe(r).DamagePerAction),
            Compare("SkillCasts", f => f.SkillCasts, r => Casts(r)),
            Compare("AutoAttacks", f => f.Attacks - f.SkillCasts, r => r.Actions.Count - Casts(r)),
        };
    }

    private static string JobName(int jobId) => jobId >= 0 && jobId < JobNames.Length ? JobNames[jobId] : $"Job {jobId}";

    /// <summary>Which mechanics the duel exercised: auto-attack, Bash, double attacks (an auto-attack of two hits).</summary>
    private static string SkillMix(DuelRecord record)
    {
        var parts = new List<string> { "auto" };
        parts.AddRange(record.Actions.Where(a => a.Skill != CharacterSkill.None).Select(a => a.Skill.ToString()).Distinct().Order());
        if (record.Actions.Any(a => a.Skill == CharacterSkill.None && a.HitCount >= 2)) parts.Add("double attack");
        return string.Join("+", parts);
    }

    private static string Weapon(DuelRecord record) =>
        record.Build!.Equipment.FirstOrDefault(e => e.Slot == EquipSlot.RightHand) is { ItemId: > 0 } weapon
            ? $"item {weapon.ItemId} +{weapon.Refine}" + (weapon.Cards.Any(c => c > 0) ? " carded" : "")
            : "bare hands";

    private static string Describe(PlayerBuild build) =>
        $"{JobName(build.JobId)} Lv{build.Level}/{build.JobLevel} {build.Str}/{build.Agi}/{build.Vit}/{build.Int}/{build.Dex}/{build.Luk}, " +
        string.Join(" ", build.Equipment.Select(e => $"{e.ItemId}" + (e.Refine > 0 ? $"+{e.Refine}" : "")));

    public static string ToMarkdown(ValidationReport report)
    {
        var text = new StringBuilder();
        text.AppendLine($"# Duel validation ({report.Records} recorded duels, {report.Eligible} eligible, {report.Elapsed.TotalSeconds:0.0}s)");
        text.AppendLine();
        text.AppendLine("Outcomes: " + string.Join(", ", report.Outcomes.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value}")));
        text.AppendLine($"Deaths in eligible complete duels: {report.Deaths} observed, {report.PredictedDeaths:0.##} predicted.");
        text.AppendLine();
        text.AppendLine("## Excluded");
        foreach (var (reason, count) in report.Exclusions.OrderByDescending(x => x.Value)) text.AppendLine($"- {reason}: {count}");
        text.AppendLine();
        text.AppendLine("## Static fidelity (offline build vs server-reported stats)");
        foreach (var check in report.StaticChecks)
            text.AppendLine($"- {check.Character} ({check.Duels} duels) {check.Build}: " +
                            (check.Mismatches.Count == 0 ? "all match" : string.Join("; ", check.Mismatches)));
        text.AppendLine();
        text.AppendLine("## Replay checks (observed - predicted)");
        text.AppendLine("| group | duels | metric | n | observed | predicted | bias [95% CI] | MAE | dispersion | threshold | verdict |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var group in report.Groups)
        foreach (var m in group.Metrics)
            text.AppendLine($"| {group.Dimension}={group.Group} | {group.Duels} | {m.Metric} | {m.Count} | {m.MeanObserved:0.##} | {m.MeanPredicted:0.##} | " +
                            (m.Count > 1 ? $"{m.Bias:+0.##;-0.##;0} [{m.BiasLow:0.##}, {m.BiasHigh:0.##}]" : "-") +
                            $" | {m.MeanAbsoluteError:0.##} | {m.Dispersion:0.##} | {m.PracticalThreshold:0.##} | {m.Verdict} |");
        text.AppendLine();
        text.AppendLine("## Forecasts made at engage (scripted policy, before the fight) vs observed");
        text.AppendLine("Live server forecasts of the eligible won duels: "
                        + string.Join(", ", report.ForecastAvailability.OrderByDescending(x => x.Value).Select(x => $"{x.Value} {x.Key}")) + ".");
        text.AppendLine();
        text.AppendLine("| metric | n | old bias | old MAE | server bias | server MAE |");
        text.AppendLine("|---|---|---|---|---|---|");
        foreach (var f in report.Forecasts)
            text.AppendLine($"| {f.Metric} | {f.Count} | {f.OldBias:0.##} | {f.OldMeanAbsoluteError:0.##} | {f.ServerBias:0.##} | {f.ServerMeanAbsoluteError:0.##} |");
        text.AppendLine();
        text.AppendLine("## Alerts");
        if (report.Alerts.Count == 0) text.AppendLine("none");
        foreach (var alert in report.Alerts) text.AppendLine($"- {alert}");
        return text.ToString();
    }
}
