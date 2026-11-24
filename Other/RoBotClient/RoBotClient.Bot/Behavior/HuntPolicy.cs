using RoBotClient.Bot.GameData;
using RoBotClient.Simulation;

namespace RoBotClient.Bot.Behavior;

/// <summary>The hunting decision rules that don't need a live session, shared by BotBehavior (PickTarget, IsReachable,
/// the server-forecast CanWin, the map estimate) and the offline HuntSimulator, so both decide the same way.</summary>
public static class HuntPolicy
{
    // ponytail: guessed cap on simulated deaths + timeouts for a fight without potions to count as winnable; the
    // WinMargin damage check bounds the mean, this bounds the tail. Re-fit from On-mode deaths if any appear.
    public const double MaxServerLossRate = 0.05;

    /// <summary>Solo target score: base exp per second of walking up, killing and sitting off the HP lost.</summary>
    public static double ExpPerSecond(int exp, double secondsToKill, int distance, double secondsPerCell, double restSeconds) =>
        exp / Math.Max(0.1, secondsToKill + distance * secondsPerCell + restSeconds);

    /// <summary>Seconds of sitting to regenerate <paramref name="damage"/> HP. Mirrors the server's Player.HpRegenTick:
    /// (MaxHp/50 + Vit/5)*(200+Vit)/200 per tick, a tick every 3 s sitting and doubled.
    /// ponytail: ignores potions, AddHpRecoveryPercent and Increased HP Recovery, so it overstates the cost
    /// for bots with those; fold them in if such bots start avoiding fights they'd win cheaply.</summary>
    public static double RestSeconds(int maxHp, int vit, double damage)
    {
        var perTick = Math.Max(1, (maxHp / 50 + vit / 5) * (200 + vit) / 200);
        return damage / (perTick * 2 / 3.0);
    }

    /// <summary>A target is worth walking to when the A* walk isn't a long loop around a wall: a mob across a wall is
    /// in view but the walk loops away from it (Bot2 ping-ponged with a Stainer 60-76 cells of walk away).</summary>
    public static bool DetourAcceptable(int pathSteps, int straightDistance) => pathSteps <= Math.Max(30, 2.5 * straightDistance);

    /// <summary>The old CanWin rule on a server prediction: expected HP lost within WinMargin of HP plus the potion
    /// buffer, attacks within MaxRoundsToKill, and without potions simulated deaths and timeouts capped too.</summary>
    public static bool ServerCanWin(DuelPrediction p, int hp, double potionBuffer, double winMargin, int maxRoundsToKill) =>
        ServerWinVeto(p, hp, potionBuffer, winMargin, maxRoundsToKill) == null;

    /// <summary>The win rule's first failing condition, in words (null: winnable). ServerCanWin is this returning null.</summary>
    public static string? ServerWinVeto(DuelPrediction p, int hp, double potionBuffer, double winMargin, int maxRoundsToKill) =>
        !(p.WinRate > 0) ? "never wins in the forecast duels"
        : !(hp > 0) ? "no HP left"
        : !(p.Attacks.Mean <= maxRoundsToKill || p.DamageTaken.Mean <= 0) ? $"needs {p.Attacks.Mean:F0} attacks, over MaxRoundsToKill {maxRoundsToKill}"
        : !(p.DamageTaken.Mean <= winMargin * (hp + potionBuffer)) ? $"expected damage taken {p.DamageTaken.Mean:F0} is over WinMargin {winMargin} x HP {hp + potionBuffer:F0} = {winMargin * (hp + potionBuffer):F0}"
        : !(potionBuffer > 0 || p.DeathRate + p.TimeoutRate <= MaxServerLossRate) ? $"loses or times out {p.DeathRate + p.TimeoutRate:P1} of duels, over {MaxServerLossRate:P1}"
        : null;

    // ---- rest: BotBehavior.TickRestAsync and the offline HuntSimulator sit and stand by these ----

    /// <summary>Rested: both fractions at or above this stands the bot up.</summary>
    public const double RestedFraction = 0.95;

    public static bool KillsWithSpells(BotBehaviorConfig cfg, int jobId) => cfg.NoAutoAttack || jobId is 3 or 8 or 14; // Mage, Wizard, Sage

    /// <summary>A Novice (job 0) needs Basic Mastery level 2 to sit (PacketSitStand); everyone else always can.</summary>
    public static bool CanSit(int jobId, int basicMasteryLevel) => jobId != 0 || basicMasteryLevel >= 2;

    /// <summary>The HP and SP fractions the rest rules read. SP only matters to bots that kill with spells: a Thief
    /// out of SP for Envenom just keeps stabbing.</summary>
    public static (double Hp, double Sp) RestFractions(BotBehaviorConfig cfg, int jobId, int hp, int maxHp, int sp, int maxSp) =>
        (maxHp > 0 ? (double)hp / maxHp : 1.0, maxSp > 0 && KillsWithSpells(cfg, jobId) ? (double)sp / maxSp : 1.0);

    /// <summary>Low enough to sit for it: HP or SP under RestBelowPercent, for a bot configured to rest.</summary>
    public static bool LowOnHpSp(BotBehaviorConfig cfg, (double Hp, double Sp) f) =>
        cfg.RestWhenNoPotions && (f.Hp < cfg.RestBelowPercent || f.Sp < cfg.RestBelowPercent);

    /// <summary>Sit down: low, or the last pick found fights winnable at full HP but not now (rest to win); able to
    /// sit; and nothing that will attack is near.</summary>
    public static bool ShouldSit(BotBehaviorConfig cfg, (double Hp, double Sp) f, bool restToWin, bool canSit, bool threatened) =>
        (LowOnHpSp(cfg, f) || restToWin) && canSit && !threatened;

    /// <summary>Stand up: not resting by config any more, rested, hit while sitting (HP only climbs sitting), or threatened.</summary>
    public static bool ShouldStand(BotBehaviorConfig cfg, (double Hp, double Sp) f, bool restToWin, bool hit, bool threatened) =>
        (!cfg.RestWhenNoPotions && !restToWin) || (f.Hp >= RestedFraction && f.Sp >= RestedFraction) || hit || threatened;

    /// <summary>The live hunt filters: alive, not ignored, on the hunt list when there is one, under MaxTargetHp.</summary>
    public static bool IsHuntable(BotBehaviorConfig cfg, int classId, int hp, int maxHp) =>
        !(maxHp > 0 && hp <= 0) && !cfg.IgnoreClassIds.Contains(classId)
        && (cfg.HuntClassIds.Count == 0 || cfg.HuntClassIds.Contains(classId))
        && !(cfg.MaxTargetHp > 0 && maxHp > cfg.MaxTargetHp);

    /// <summary>The target choice, shared by live PickTarget and the offline HuntSimulator: per visible monster the
    /// hunt filters, the environment's own exclusions, the portal buffer, the forecast win rule (ForceHuntClassIds
    /// overrides it), no-safe-cell and group-pull vetoes; then the best score (solo: exp/s incl. walk and rest; party:
    /// nearest, grey monsters last) that the environment can reach. Observations, forecasts and paths come from
    /// &lt;paramref name="world"/&gt;, so neither a session nor a clock is needed here. &lt;paramref name="ranking"/&gt;, when
    /// given, receives every scored candidate (lower score first is better; Ok false: kept only for the shadow pick),
    /// for an observer to explain the choice; it changes nothing.</summary>
    public static TargetPick<T, TF> PickTarget<T, TF>(IReadOnlyList<T> visible, ITargetWorld<T, TF> world, BotBehaviorConfig cfg,
        GameDatabase? data, string map, bool wantShadow = false, List<(T M, double Score, bool Ok)>? ranking = null) where T : class
    {
        var candidates = new List<(double Score, T M, TargetForecast<TF> F, bool Ok, double ShadowScore, bool ShadowOk)>();
        foreach (var m in visible)
        {
            var o = world.Observe(m);
            if (!IsHuntable(cfg, o.ClassId, o.Hp, o.MaxHp)) { world.Rejected(m, TargetReject.NotHuntable); continue; }
            if (world.Excluded(m) is { } why) { world.Rejected(m, why); continue; }
            if (cfg.PortalSafeDistance > 0 && data != null && data.IsNearPortal(map, o.X, o.Y, cfg.PortalSafeDistance))
            { world.Rejected(m, TargetReject.NearPortal); continue; }
            if (world.Forecast(m) is not var (f, shadow)) { world.Rejected(m, TargetReject.Unknown); continue; }
            if (world.NoSafeCell(m)) { world.Rejected(m, TargetReject.NoSafeCell); continue; }
            var forced = cfg.ForceHuntClassIds.Contains(o.ClassId);
            bool ok = forced || f.CanWin, shadowOk = forced || shadow.CanWin;
            if (!ok)
            {
                world.Rejected(m, TargetReject.TooStrong);
                if (!shadowOk) continue; // kept only for the shadow pick
            }
            if (!forced && world.PullsLosingGroup(m)) { if (ok) world.Rejected(m, TargetReject.Swarm); continue; }
            candidates.Add((Score(o, f), m, f, ok, Score(o, shadow), shadowOk));
        }

        double Score(TargetObservation o, TargetForecast<TF> f)
        {
            var self = world.Self;
            if (self.Solo) return -ExpPerSecond(o.Exp, f.SecondsToKill, o.Distance, self.SecondsPerCell, RestSeconds(self.MaxHp, self.Vit, f.DamageTaken)); // negated: ascending
            return o.Level > 0 && o.Level <= self.Level - 15 ? o.Distance + 100 : o.Distance; // grey mob: poor exp, deprioritize
        }

        // Stable: equal scores keep the order the monsters were seen in.
        var reachable = new Dictionary<T, bool>(ReferenceEqualityComparer.Instance);
        bool Reachable(T m) => reachable.TryGetValue(m, out var r) ? r : reachable[m] = world.Reachable(m);
        ranking?.AddRange(candidates.OrderBy(c => c.Score).Select(c => (c.M, c.Score, c.Ok)));
        var shadowPick = wantShadow ? candidates.Where(c => c.ShadowOk).OrderBy(c => c.ShadowScore).FirstOrDefault(c => Reachable(c.M)).M : null;
        foreach (var c in candidates.OrderBy(c => c.Score))
        {
            if (!c.Ok) continue;
            if (Reachable(c.M)) return new(c.M, c.F, shadowPick);
            world.Rejected(c.M, TargetReject.Unreachable);
        }
        return new(null, default, shadowPick);
    }

    /// <summary>The map estimate's search model: walking to the next target takes about the spacing between huntable
    /// monsters spread evenly over the area, at the base player walk.</summary>
    public static double MapSearchSecondsPerKill(int areaCells, double huntableCount) => 0.15 * Math.Sqrt(areaCells / huntableCount);
}

public enum TargetReject { NotHuntable, AvoidedClass, Blacklisted, Hazard, NearPortal, Unknown, NoSafeCell, TooStrong, Swarm, Unreachable }

/// <summary>What the target rules read about one visible monster.</summary>
public readonly record struct TargetObservation(int ClassId, int Exp, int Level, int Hp, int MaxHp, int X, int Y, int Distance);

/// <summary>A forecast as the target rules read it; &lt;see cref="Value"/&gt; is the environment's own forecast, handed back.</summary>
public readonly record struct TargetForecast<TF>(TF Value, bool CanWin, double SecondsToKill, double DamageTaken);

/// <summary>The picking character: solo ranks by exp/s, a party member by distance.</summary>
public readonly record struct TargetSelf(bool Solo, int Level, int MaxHp, int Vit, double SecondsPerCell);

public readonly record struct TargetPick<T, TF>(T? Target, TargetForecast<TF> Forecast, T? ShadowTarget) where T : class;

/// <summary>Where the target rules get what differs between live and offline: observations, the environment's own
/// exclusions (live: death-avoided classes, the unreachable blacklist, hazards), forecasts (live: hand formulas or the
/// server's; offline: the server-code duel), fixed-monster firing spots, group pulls, and A* reachability.</summary>
public interface ITargetWorld<T, TF>
{
    TargetSelf Self { get; }
    TargetObservation Observe(T m);
    TargetReject? Excluded(T m);
    /// <summary>The deciding forecast and the shadow one (live ServerForecast On: the old engine's), or null when unknown.</summary>
    (TargetForecast<TF> Decision, TargetForecast<TF> Shadow)? Forecast(T m);
    bool NoSafeCell(T m);
    bool PullsLosingGroup(T m);
    bool Reachable(T m);
    void Rejected(T m, TargetReject why);
}
