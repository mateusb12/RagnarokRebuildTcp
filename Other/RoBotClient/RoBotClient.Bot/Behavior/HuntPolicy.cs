using RoBotClient.Bot.GameData;
using RebuildSharedData.ClientTypes;
using RebuildSharedData.Data;
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


    // ---- hazards: aggressive monsters the forecast loses to. BotBehavior (ComputeHazards, AvoidHazardsAsync, PickTarget,
    // EngagementGroup, TickRestAsync, WanderAsync) and the offline HuntSimulator react to them by these same rules. ----

    /// <summary>After a death the killer's class is not targeted for this long (minutes); a repeat killer, longer.</summary>
    public const int AvoidClassMinutes = 5, RepeatKillerAvoidMinutes = 30;

    /// <summary>Same 3-cell warning band as the path cost: a flee ends only past it, and a target must sit outside it.</summary>
    public const int HazardBuffer = 3;
    /// <summary>Same-type assist monsters this close to a target join the fight.</summary>
    public const int AssistRadius = 10;

    /// <summary>Will attack on sight: an aggressive AI, an angry one, a boss (plants never, even boss-flagged ones).</summary>
    public static bool IsAggressive(MonsterDbEntry? m)
    {
        if (m == null) return false;
        // Plants never move or attack, even boss-flagged ones (the prt_fild08 Target Dummy is Special=Boss).
        if (string.Equals(m.Ai, "AiPlant", StringComparison.Ordinal)) return false;
        if (string.Equals(m.Special, "Boss", StringComparison.OrdinalIgnoreCase)) return true; // bosses/MVPs: always dangerous
        if (string.IsNullOrEmpty(m.Ai)) return false;
        return m.Ai.StartsWith("AiAggressive", StringComparison.Ordinal)
            || m.Ai.Equals("AiAngry", StringComparison.Ordinal)
            || m.Ai.Equals("AiStandardBoss", StringComparison.Ordinal);
    }

    /// <summary>A fixed-position monster: Mandragora-style plants (negative MoveSpeed) and AiAggressiveImmobile sprites.</summary>
    public static bool IsImmobile(MonsterDbEntry? db)
    {
        if (db == null) return false;
        if (db.MoveSpeed <= 0) return true;
        return !string.IsNullOrEmpty(db.Ai) && db.Ai.IndexOf("Immobile", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static bool IsAssist(MonsterDbEntry? db) => !string.IsNullOrEmpty(db?.Ai) && db.Ai.Contains("Assist", StringComparison.Ordinal);

    // A mob that never moves can only hit us within its attack range; fleeing at its scan distance (12 for Flora, range 3)
    // made the bot flee, walk back toward its exit and flee again every 10 s.
    public static int HazardRadius(MonsterDbEntry? db) => db != null && IsImmobile(db) ? Math.Max(1, db.Range) + 2 : (db?.ScanDist ?? 10) + 2;

    /// <summary>A monster that can be a hazard at all: aggressive, and neither ignored nor force-hunted by config.</summary>
    public static bool HazardCandidate(BotBehaviorConfig cfg, int classId, MonsterDbEntry? db) =>
        !cfg.IgnoreClassIds.Contains(classId) && !cfg.ForceHuntClassIds.Contains(classId) && IsAggressive(db);

    /// <summary>A candidate is a hazard when any forecast engine says we'd lose, or it's the one being fled (the forecast
    /// flips with HP mid-chase; each "win" ended the flee and re-engaged it at 1 cell).</summary>
    public static bool IsHazard(bool aForecastLoses, bool fleeingIt) => aForecastLoses || fleeingIt;

    // The server's attack range test: straight-line distance rounded to whole cells (DistanceCache.IntDistance).
    public static int ServerDistance(int x0, int y0, int x1, int y1) =>
        (int)Math.Round(Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)));

    /// <summary>The hazard to flee: the nearest one whose zone covers us (an ongoing flee lasts until past the warning band
    /// too). (-1, max) when none.</summary>
    internal static (int Index, int Distance) NearestHazardInReach(IReadOnlyList<(Position pos, int radius, string name, string engine, int id)> hazards,
        Position self, bool fleeing)
    {
        var bestIdx = -1;
        var nearestD = int.MaxValue;
        for (var i = 0; i < hazards.Count; i++)
        {
            var d = Dist(self.X, self.Y, hazards[i].pos.X, hazards[i].pos.Y);
            var reach = hazards[i].radius + (fleeing ? HazardBuffer : 0);
            if (d <= reach && d < nearestD) { nearestD = d; bestIdx = i; }
        }
        return (bestIdx, nearestD);
    }

    internal static bool CellNearHazard(IReadOnlyList<(Position pos, int radius, string name, string engine, int id)> hazards, int x, int y, int skip = -1)
    {
        for (var i = 0; i < hazards.Count; i++)
            if (i != skip && Dist(hazards[i].pos.X, hazards[i].pos.Y, x, y) <= hazards[i].radius) return true;
        return false;
    }

    /// <summary>A target in or near a hazard zone, or whose straight walk crosses one, pulls us back to the threat.
    /// <paramref name="zones"/>: the hazards in view plus the remembered ones (see BotBehavior.ApproachNearHazard).</summary>
    public static bool ApproachNearHazard(Position self, Position target, IReadOnlyList<(int id, Position pos, int radius)> zones, out int hazard, out string where)
    {
        var n = Math.Max(1, Dist(self.X, self.Y, target.X, target.Y));
        for (var i = n; i >= 1; i--) // target cell first, so "where" names it when both apply
        {
            int x = self.X + (target.X - self.X) * i / n, y = self.Y + (target.Y - self.Y) * i / n;
            var buffer = i == n ? HazardBuffer : 0;
            foreach (var h in zones)
            {
                hazard = h.id;
                var d = Dist(h.pos.X, h.pos.Y, x, y);
                if (d > h.radius + buffer) continue;
                where = i < n ? $"the walk to it crosses (at ({x},{y}))" : d <= h.radius ? "it stands inside" : $"it stands {d - h.radius} cell(s) outside";
                return true;
            }
        }
        (hazard, where) = (0, "");
        return false;
    }

    /// <summary>An immobile ranged monster's attack circle, priced as a soft hazard on paths (+1: the server's range is
    /// rounded Euclidean). Null for mobile monsters and melee plants.</summary>
    public static (int x, int y, int radius)? ImmobileZone(MonsterDbEntry? db, Position pos)
    {
        if (!IsImmobile(db)) return null;
        var range = db?.Range ?? 0;
        return range < 2 ? null : (pos.X, pos.Y, range + 1);
    }

    /// <summary>The per-cell extra cost for WalkMap.FindPath: hazard zones (8 inside, 2 in the 3-cell band) and, with a
    /// portal buffer, the map's warp footprints (50 inside, 6 adjacent, 1.5 within the buffer). Null: nothing to bias.</summary>
    public static Func<int, int, float>? PathCost(GameDatabase? data, string map, List<(int x, int y, int radius)> hzList, int portalBuffer)
    {
        if (data == null) return null;
        portalBuffer = Math.Max(0, portalBuffer);
        // Snapshot the REAL warp footprints (rectangles, not points) so cost is correct for fat warps.
        List<(int minX, int minY, int maxX, int maxY)>? footprints = null;
        if (portalBuffer > 0)
        {
            footprints = new List<(int, int, int, int)>();
            foreach (var f in data.World.PortalFootprintsOn(map)) footprints.Add(f);
        }
        if (hzList.Count == 0 && (footprints == null || footprints.Count == 0)) return null;
        return (x, y) =>
        {
            var cost = 0f;
            for (var i = 0; i < hzList.Count; i++)
            {
                var h = hzList[i];
                var d = Dist(h.x, h.y, x, y);
                if (d <= h.radius) cost += 8f;          // inside aggro — strong penalty (path detours unless no choice)
                else if (d <= h.radius + 3) cost += 2f; // 3-cell warning buffer
            }
            if (footprints != null)
            {
                for (var i = 0; i < footprints.Count; i++)
                {
                    var f = footprints[i];
                    // Chebyshev distance from (x,y) to the AABB of the footprint. 0 = inside.
                    var dx = Math.Max(Math.Max(f.minX - x, 0), x - f.maxX);
                    var dy = Math.Max(Math.Max(f.minY - y, 0), y - f.maxY);
                    var d = Math.Max(dx, dy);
                    if (d == 0) { cost += 50f; break; }       // inside footprint = guaranteed warp
                    if (d == 1) cost += 6f;                    // adjacent — one step from triggering
                    else if (d <= portalBuffer) cost += 1.5f;  // soft buffer fades smoothly
                }
            }
            return cost;
        };
    }

    /// <summary>True if the server's walk from (x0,y0) to (x1,y1) could step on a warp footprint other than the goal
    /// portal: on open ground every shortest 8-way path stays inside the box between the two points.</summary>
    public static bool WalkMayCrossPortal(GameDatabase? data, string map, int x0, int y0, int x1, int y1, (int x, int y)? allowedPortalCell)
    {
        if (data == null || (x0 == x1 && y0 == y1)) return false;
        int minX = Math.Min(x0, x1), maxX = Math.Max(x0, x1), minY = Math.Min(y0, y1), maxY = Math.Max(y0, y1);
        foreach (var f in data.World.PortalFootprintsOn(map))
        {
            if (f.maxX < minX || f.minX > maxX || f.maxY < minY || f.minY > maxY) continue;
            if (allowedPortalCell is { } a && a.x >= f.minX - 1 && a.x <= f.maxX + 1 && a.y >= f.minY - 1 && a.y <= f.maxY + 1)
                continue; // goal portal: crossing it is the point
            return true;
        }
        return false;
    }

    /// <summary>The largest i &lt;= maxStep such that the straight walk from (fromX,fromY) to path[i] touches no portal
    /// (allowedPortalCell, the goal portal, excepted); 0 when none.</summary>
    public static int FurthestStraightLineSafeStep(GameDatabase? data, string map, int fromX, int fromY, IReadOnlyList<(int x, int y)> path, int maxStep,
        (int x, int y)? allowedPortalCell = null)
    {
        if (data == null) return maxStep;
        for (var i = maxStep; i >= 1; i--)
        {
            var (tx, ty) = path[i];
            if (!WalkMayCrossPortal(data, map, fromX, fromY, tx, ty, allowedPortalCell))
                return i;
        }
        return 0;
    }

    /// <summary>A fixed monster other than the target that would hit someone standing on (x, y). One cell of margin for rounding.</summary>
    internal static bool InOtherFixedReach(GameDatabase? data, IEnumerable<BotBehavior.MonsterInfo> monsters, int x, int y, int targetId)
    {
        foreach (var m in monsters)
        {
            if (m.Id == targetId || (m.MaxHp > 0 && m.Hp <= 0)) continue;
            if (data?.Monster(m.ClassId) is not { } db || !IsImmobile(db)) continue;
            if (ServerDistance(x, y, m.Pos.X, m.Pos.Y) <= db.Range + 1) return true;
        }
        return false;
    }

    /// <summary>The pack that piles on if we engage this target: same-type assist monsters that can see it, any aggressive
    /// monster whose aggro range covers us, and (standing next to the target) any fixed monster that reaches it.</summary>
    internal static List<BotBehavior.MonsterInfo> EngagementGroup(GameDatabase? data, IEnumerable<BotBehavior.MonsterInfo> monsters, Position self,
        BotBehavior.MonsterInfo target, bool standsAtTarget)
    {
        var group = new List<BotBehavior.MonsterInfo> { target };
        var targetAssists = IsAssist(data?.Monster(target.ClassId));
        foreach (var m in monsters)
        {
            if (m.Id == target.Id) continue;
            if (m.MaxHp > 0 && m.Hp <= 0) continue;
            var db = data?.Monster(m.ClassId);
            var joins = (targetAssists && m.ClassId == target.ClassId && Dist(m.Pos.X, m.Pos.Y, target.Pos.X, target.Pos.Y) <= AssistRadius)
                        || (IsAggressive(db) && Dist(self.X, self.Y, m.Pos.X, m.Pos.Y) <= HazardRadius(db))
                        || (standsAtTarget && db is { } fixedDb && IsImmobile(fixedDb)
                            && ServerDistance(m.Pos.X, m.Pos.Y, target.Pos.X, target.Pos.Y) <= fixedDb.Range + 1);
            if (joins) group.Add(m);
        }
        return group;
    }

    /// <summary>Something that will attack is near a resting bot: an aggressive monster, or one that has hit us, within 3
    /// cells; or any hazard in view. Ends a rest and forbids sitting (a passive Horn wandering by no longer stands it up).</summary>
    /// <summary>A monster that will attack (aggressive, or already hitting us) within its reach: 3 cells, or its attack
    /// range + 1 for a ranged one (a Cruiser, range 7, shot Bot2 sitting 7 cells away; it stood, sat again, was shot...).</summary>
    internal static bool Threatened(GameDatabase? data, IEnumerable<BotBehavior.MonsterInfo> monsters, Position self, Func<int, bool> hasHitMe, int hazardCount)
    {
        if (hazardCount > 0) return true;
        foreach (var m in monsters)
        {
            if (m.MaxHp > 0 && m.Hp <= 0) continue;
            var db = data?.Monster(m.ClassId);
            if (!IsAggressive(db) && !hasHitMe(m.Id)) continue;
            if (Dist(self.X, self.Y, m.Pos.X, m.Pos.Y) <= ThreatReach(db)) return true;
        }
        return false;
    }

    internal static int ThreatReach(MonsterDbEntry? db) => Math.Max(3, (db?.Range ?? 0) + 1);

    internal static bool AnyAdjacentMonster(GameDatabase? data, IEnumerable<BotBehavior.MonsterInfo> monsters, Position self, int range, bool aggressiveOnly, Func<int, bool> hasHitMe)
    {
        foreach (var m in monsters)
        {
            if (m.MaxHp > 0 && m.Hp <= 0) continue;
            // "Will attack": aggressive, or already fighting us (Bot3 sat down mid-fight with a Horn and sat/stood under its hits).
            if (aggressiveOnly && !IsAggressive(data?.Monster(m.ClassId)) && !hasHitMe(m.Id)) continue;
            if (Dist(self.X, self.Y, m.Pos.X, m.Pos.Y) <= range) return true;
        }
        return false;
    }

    /// <summary>First waypoint (&lt;= 8 cells) of an A* path to the current walk goal where every hazard zone is steeply
    /// priced (deeper = dearer, so from inside the path leaves by the shortest way out). Null when there's no fresh goal,
    /// no path, or the waypoint wouldn't take us farther from <paramref name="threat"/>.</summary>
    public static (int x, int y)? DodgeTowardGoal(GameDatabase? data, string mapName, WalkMap? map, Position self, Position threat, (int x, int y)? goal,
        IReadOnlyList<(int x, int y, int radius)> zones, out string whyNot)
    {
        whyNot = "";
        if (goal is not { } g) { whyNot = "not walking anywhere"; return null; }
        if (map == null) { whyNot = "no walk map"; return null; }
        var path = map.FindPath(self.X, self.Y, g.x, g.y, 20000, (x, y) =>
        {
            var cost = 0f;
            foreach (var h in zones)
            {
                var d = Dist(h.x, h.y, x, y);
                if (d <= h.radius) cost += 40f * (h.radius - d + 1);
            }
            return cost;
        });
        if (path is not { Count: > 1 }) { whyNot = $"no path to my goal ({g.x},{g.y})"; return null; }
        var step = FurthestStraightLineSafeStep(data, mapName, self.X, self.Y, path, Math.Min(8, path.Count - 1), (g.x, g.y));
        if (step < 1) { whyNot = "a portal is in the way"; return null; }
        var wp = path[step];
        if (Dist(wp.x, wp.y, threat.X, threat.Y) > Dist(self.X, self.Y, threat.X, threat.Y)) return wp;
        whyNot = $"the way to my goal ({g.x},{g.y}) goes toward it";
        return null;
    }

    /// <summary>A way around a portal that sits between us and the flee cell: a walkable cell just outside the footprint,
    /// not farther toward the threat, whose own walk and onward path stay off every portal.</summary>
    internal static (int x, int y)? FindPortalFleeWaypoint(GameDatabase? data, string map, WalkMap walkMap, Position self,
        int goalX, int goalY, Position threat, Func<int, int, float>? pathCost)
    {
        if (data == null || !WalkMayCrossPortal(data, map, self.X, self.Y, goalX, goalY, allowedPortalCell: null)) return null;
        var best = (x: 0, y: 0);
        var bestRemaining = int.MaxValue;
        foreach (var portal in data.World.PortalFootprintsOn(map))
        {
            var minX = Math.Min(self.X, goalX);
            var maxX = Math.Max(self.X, goalX);
            var minY = Math.Min(self.Y, goalY);
            var maxY = Math.Max(self.Y, goalY);
            if (portal.maxX < minX || portal.minX > maxX || portal.maxY < minY || portal.minY > maxY)
                continue;

            var candidates = new HashSet<(int x, int y)>();
            for (var y = portal.minY - 1; y <= portal.maxY + 1; y++)
            {
                candidates.Add((portal.minX - 1, y));
                candidates.Add((portal.maxX + 1, y));
            }
            for (var x = portal.minX - 1; x <= portal.maxX + 1; x++)
            {
                candidates.Add((x, portal.minY - 1));
                candidates.Add((x, portal.maxY + 1));
            }

            foreach (var candidate in candidates)
            {
                if ((candidate.x == self.X && candidate.y == self.Y)
                    || !walkMap.IsWalkable(candidate.x, candidate.y)
                    || WalkMayCrossPortal(data, map, self.X, self.Y, candidate.x, candidate.y, allowedPortalCell: null)
                    || data.World.IsInPortalFootprint(map, candidate.x, candidate.y)
                    || Dist(candidate.x, candidate.y, threat.X, threat.Y) < Dist(self.X, self.Y, threat.X, threat.Y))
                    continue;

                var onward = walkMap.FindPath(candidate.x, candidate.y, goalX, goalY, 4000, pathCost);
                if (onward is not { Count: > 1 }
                    || onward.Any(p => data.World.IsInPortalFootprint(map, p.x, p.y)))
                    continue;
                if (onward.Count >= bestRemaining) continue;
                best = candidate;
                bestRemaining = onward.Count;
            }
        }
        return bestRemaining < int.MaxValue ? best : null;
    }

    /// <summary>What the flee does this tick (AvoidHazardsAsync), from the hazard being fled: sidestep toward the walk goal
    /// when it isn't closing in; a Fly Wing once it is actually hurting us (or two of its hits kill); else walk away 14,
    /// 10 or 6 cells (a walkable cell outside other hazards and fixed reach, farther from it, on a portal-safe path),
    /// around a portal in the way, or, cornered at 2 cells or less, fight it; otherwise hold position.
    /// <paramref name="hurt"/> and <paramref name="canFlyWing"/> are asked only when needed.</summary>
    internal static FleeAction ChooseFlee(GameDatabase? data, string mapName, WalkMap? map, Position self,
        IReadOnlyList<(Position pos, int radius, string name, string engine, int id)> hazards, int index, int nearestD,
        (int x, int y)? walkGoal, IReadOnlyList<(int x, int y, int radius)> zones, Func<int, int, float>? pathCost,
        IReadOnlyCollection<BotBehavior.MonsterInfo> monsters, Func<(bool Hurt, string Why)> hurt, Func<bool> canFlyWing)
    {
        var hz = hazards[index];
        // On the way somewhere: sidestep along an A* path to the goal that bends out of the danger zone, instead
        // of running straight back the way we came. Fly Wing / flee-away only when it's closing in or no such path.
        string noDodge;
        if (nearestD <= hz.radius / 2) noDodge = $"it's {nearestD} cells away, too close to sidestep";
        else if (DodgeTowardGoal(data, mapName, map, self, hz.pos, walkGoal, zones, out noDodge) is { } dodge)
            return new(FleeKind.Dodge, dodge.x, dodge.y, 0, "dodge", $"Flee: sidestepping '{hz.name}' toward my goal ({walkGoal!.Value.x},{walkGoal.Value.y}) via ({dodge.x},{dodge.y}).");

        // Walking away rarely outruns an aggressive mob (Angeling on pay_fild04 killed Bot3 that way).
        // A Fly Wing (random spot on this map) gets out of its sight instantly; travel/hunt just replans.
        // Only once it's actually hurting us: winging at every hazard in view burnt the stack (Bot1: 239 escapes)
        // and the bots then died with "no Fly Wing" when a real ambush came.
        var (isHurt, hurtWhy) = hurt();
        if (isHurt && canFlyWing())
            return new(FleeKind.FlyWing, 0, 0, 0, "flywing", $"Using Fly Wing to escape '{hz.name}' ({hurtWhy}; no sidestep: {noDodge}).");

        var dx = Math.Sign(self.X - hz.pos.X);
        var dy = Math.Sign(self.Y - hz.pos.Y);
        if (dx == 0 && dy == 0) { dx = 1; dy = 1; }
        var why = $"no sidestep: {noDodge}; {(isHurt ? "no Fly Wing" : "not hurt yet, saving Fly Wings")}";
        for (var step = 14; step >= 6; step -= 4)
        {
            var tx = self.X + dx * step;
            var ty = self.Y + dy * step;
            if (map == null) return new(FleeKind.WalkBlind, Math.Max(1, tx), Math.Max(1, ty), 0, "", "");
            // The one we flee only has to end up farther away: a cell 6-14 steps off can't be outside a radius-12 zone
            // when it's 1-2 cells behind us, so checking it here too left only "holding position" while it hit us.
            if (!map.TryFindWalkableNear(tx, ty, 5, out var fx, out var fy)
                || CellNearHazard(hazards, fx, fy, skip: index)
                || InOtherFixedReach(data, monsters, fx, fy, 0) // fleeing a Thief Bug into a Flora's reach killed Bot5
                || Dist(fx, fy, hz.pos.X, hz.pos.Y) <= nearestD)
                continue;

            // A direct flee vector can point straight through a warp: plan around portal footprints, then send only a
            // short waypoint whose server-side walk cannot touch a portal.
            var path = map.FindPath(self.X, self.Y, fx, fy, 6000, pathCost);
            if (path is not { Count: > 1 }) continue;
            var safeStep = FurthestStraightLineSafeStep(data, mapName, self.X, self.Y, path, Math.Min(6, path.Count - 1));
            if (safeStep < 1)
            {
                if (FindPortalFleeWaypoint(data, mapName, map, self, fx, fy, hz.pos, pathCost) is { } aroundPortal)
                    return new(FleeKind.AroundPortal, aroundPortal.x, aroundPortal.y, 0, "portal",
                        $"Flee: stepping around a portal via ({aroundPortal.x},{aroundPortal.y}) to get away from '{hz.name}'.");
                continue;
            }
            while (safeStep > 1 && InOtherFixedReach(data, monsters, path[safeStep].x, path[safeStep].y, 0)) safeStep--;
            var waypoint = path[safeStep];
            return new(FleeKind.Away, waypoint.x, waypoint.y, 0, "away", $"Flee: moving away from '{hz.name}' via ({waypoint.x},{waypoint.y}) toward ({fx},{fy}) ({why}).");
        }
        if (map == null)
            return new(FleeKind.CorneredBlind, Math.Max(1, self.X + dx * 6), Math.Max(1, self.Y + dy * 6), 0, "cornered",
                $"Flee: no safe cell away from '{hz.name}' — stepping blindly 6 cells off ({why}).");
        // Standing still while it hits us always loses (Bot1 vs Argos on mjolnir_02: 10 hits, no swing back). A
        // fight the forecast calls lost still deals damage and sometimes wins.
        if (nearestD <= 2 && monsters.FirstOrDefault(m => m.Pos == hz.pos && m.Name == hz.name) is { } cornering)
            return new(FleeKind.FightBack, cornering.Pos.X, cornering.Pos.Y, cornering.Id, "fightback",
                $"Flee: cornered by '{hz.name}' {nearestD} cell(s) away, no way out ({why}) — fighting back.");
        return new(FleeKind.Hold, self.X, self.Y, 0, "cornered", $"Flee: no portal-safe path away from '{hz.name}' ({why}); holding position.");
    }

    /// <summary>The map estimate's search model: walking to the next target takes about the spacing between huntable
    /// monsters spread evenly over the area, at the base player walk.</summary>
    public static double MapSearchSecondsPerKill(int areaCells, double huntableCount) => 0.15 * Math.Sqrt(areaCells / huntableCount);
}

/// <summary>The flee's move this tick: walk to (X, Y) (Dodge, Away, AroundPortal, WalkBlind, CorneredBlind), use a Fly Wing,
/// attack MonsterId (FightBack), or stand (Hold). LogKind/Message: the live flee log line (LogFlee dedupes by kind).</summary>
internal enum FleeKind { Dodge, FlyWing, WalkBlind, AroundPortal, Away, CorneredBlind, FightBack, Hold }

internal readonly record struct FleeAction(FleeKind Kind, int X, int Y, int MonsterId, string LogKind, string Message);

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
