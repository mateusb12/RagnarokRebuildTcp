using RebuildSharedData.ClientTypes;
using RebuildSharedData.Data;
using RebuildSharedData.Enum;
using RoBotClient.Bot.GameData;
using RoBotClient.Bot.Session;
using RoBotClient.Bot.State;

namespace RoBotClient.Bot.Behavior;

public sealed partial class BotBehavior
{
    private DateTime _nextWander = DateTime.MinValue;
    private (int dx, int dy) _heading;

    /// <summary>Build a per-cell HAZARD-ONLY cost closure — for path searches where the goal itself is
    /// a portal cell (Shopping.cs reachable-portal checks). Including portal avoidance would inflate
    /// every portal's path cost since the destination IS a portal. Hazards still apply because we still
    /// want to walk around aggressive mobs on the way to the portal.</summary>
    private Func<int, int, float>? MakeHazardOnlyCost() => HuntPolicy.PathCost(_data, "", HazardZones(), portalBuffer: 0);

    /// <summary>Build a per-cell extra-cost closure for WalkMap.FindPath. Combines the visible hazard
    /// list (mob aggro cones) and the static portal layout (so the bot doesn't path through warp cells
    /// while traveling to a non-portal goal). Returns null when there's nothing to bias — that lets the
    /// callee skip the danger pass entirely.</summary>
    private Func<int, int, float>? MakePathCost(string map)
    {
        if (_data == null) return null;
        // Snapshot inputs into locals so the closure doesn't keep re-evaluating volatile fields.
        var hzList = HazardZones();
        // Immobile-ranged-mob attack zones: Mandragora et al. don't chase, but if the bot walks into
        // their attack radius they shoot. Treat their fixed attack circles as soft hazards so paths
        // naturally bend around them when possible. (For approaching to engage, the kite FSM picks a
        // cell on the OUTER edge; this overlay only nudges; it doesn't make engagement impossible.)
        AppendImmobileMobZones(hzList);
        // The REAL warp footprints (rectangles, not points): the legacy distance-to-center version silently approved
        // cells on the long edge of warps like pay_fild01's 5×13 pay_fild07 portal.
        return HuntPolicy.PathCost(_data, map, hzList, _config.PortalSafeDistance);
    }

    /// <summary>Add every visible immobile-ranged mob's attack circle to <paramref name="hzList"/> as a
    /// soft hazard. Cost-overlay readers (<see cref="MakePathCost"/>) treat the circle's interior as
    /// "inside aggro" (high cost) and the 3-tile fringe as a warning band — so passive travel routes
    /// around the area when there's room, but engagement A* can still find a path to the outer edge.
    /// Only fires when the mob's data confirms it's immobile (Mandragora MoveSpeed -0.001, Geographer
    /// AiAggressiveImmobile, etc.); mobile rangers are already covered by the aggro hazard tracker.</summary>
    private void AppendImmobileMobZones(List<(int x, int y, int radius)> hzList)
    {
        if (_data == null) return;
        var snap = _bot.WithState(Snapshot.From);
        // Remembered for the whole visit: they can't move, and pricing only the ones in view made the A* route flip each
        // time one came into or left view. Bot2 and Bot4 walked back and forth on beach_dun3 for hours (westward the
        // Hydras came into view and the route turned east, eastward they left view and it turned back west).
        if (_immobileMap != snap.Map) { _immobileMap = snap.Map; _immobileSeen.Clear(); }
        foreach (var m in snap.Monsters)
            if (HuntPolicy.ImmobileZone(_data.Monster(m.ClassId), m.Pos) is { } zone) // melee plants have no projectile zone worth detouring around
                _immobileSeen[m.Id] = zone;
        // Gone from a cell we can still see = killed.
        foreach (var (id, z) in _immobileSeen)
            if (Dist(snap.SelfPos, new Position(z.x, z.y)) <= ImmobileForgetDist && !snap.Monsters.Any(m => m.Id == id)) _immobileSeen.Remove(id);
        hzList.AddRange(_immobileSeen.Values);
    }

    private string? _immobileMap;
    private readonly Dictionary<int, (int x, int y, int radius)> _immobileSeen = new();
    private const int ImmobileForgetDist = 10; // well inside view range

    private async Task TravelAsync(Snapshot snap, CancellationToken ct)
    {
        if (DateTime.UtcNow < _nextWander) return; // reuse the move throttle
        _nextWander = DateTime.UtcNow.AddSeconds(1.0); // pick a new wander target faster — wander cells are cheap
        if (_data == null || (snap.SelfPos.X == 0 && snap.SelfPos.Y == 0)) return;
        // Map choice priced this trip with a Butterfly Wing to the save town first (one try; if it fails we just walk).
        if (_mapWingTo is { } wingTo)
        {
            _mapWingTo = null;
            if (!string.Equals(snap.Map, wingTo, StringComparison.OrdinalIgnoreCase) && HasUsableItem(_config.ButterflyWingItemId))
            {
                OnLog?.Invoke($"Travel: Butterfly Wing to {_data.MapName(wingTo)} ({wingTo}), quicker to {_config.HomeMap} from there.");
                _nextWander = DateTime.UtcNow.AddSeconds(3);
                await _bot.UseInventoryItemAsync(_config.ButterflyWingItemId, -1, ct);
                return;
            }
        }
        await StepTowardMapAsync(snap, _config.HomeMap, ct);
    }

    // Roam in a persistent direction (turning when blocked or arrived) so we cover ground and find
    // monsters, avoiding portals so we don't accidentally leave the map.
    // Wander uses a "sticky far destination + small server-friendly hops" model. Pick a target ~35 cells
    // out, hold it for ~15 s, and each wander tick walk 12 cells further along the A* path toward it. That
    // gives wide coverage (the bot doesn't pace in a 16-cell box) while respecting the server's per-WalkTo
    // distance cap. The target re-uses across ticks so headings stay coherent — the bot commits to going
    // somewhere instead of jittering.
    private Position? _wanderTarget;
    private DateTime _wanderTargetUntil = DateTime.MinValue;
    /// <summary>The active wander A* destination, for the dashboard minimap.</summary>
    public Position? WanderTarget => DateTime.UtcNow <= _wanderTargetUntil ? _wanderTarget : null;
    private DateTime _nextWanderWedgeLog = DateTime.MinValue;
    private const int WanderHopCells = 12;   // per-WalkTo waypoint distance — safely under the server cap

    private async Task WanderAsync(Snapshot snap, CancellationToken ct)
    {
        if (DateTime.UtcNow < _nextWander) return;
        _nextWander = DateTime.UtcNow.AddSeconds(0.8);

        var self = snap.SelfPos;
        if (self.X == 0 && self.Y == 0) return;

        var map = _data?.GetWalkMap(snap.Map);
        if (map == null)
        {
            // No walkmap loaded — naive walk in the current heading, let the server pathfind. Just keep us
            // doing SOMETHING so we're not Idle on a never-loaded map.
            if (_heading.dx == 0 && _heading.dy == 0) _heading = RandomHeading();
            await _bot.WalkToAsync(Math.Max(1, self.X + _heading.dx * 12), Math.Max(1, self.Y + _heading.dy * 12), ct);
            return;
        }

        // Need a fresh target if we've never picked one, the previous one expired, or we've arrived.
        var needTarget = _wanderTarget == null
                         || DateTime.UtcNow > _wanderTargetUntil
                         || Dist(self, _wanderTarget.Value) <= 3;

        if (needTarget)
        {
            // Portal-away if we're hugging one; otherwise pick a fresh random heading so successive targets
            // don't all point the same direction.
            if (_data != null && TryNearestPortal(snap.Map, self, 8, out var npx, out var npy))
            {
                var hx = Math.Sign(self.X - npx);
                var hy = Math.Sign(self.Y - npy);
                _heading = (hx == 0 && hy == 0) ? RandomHeading() : (hx, hy);
            }
            else
            {
                _heading = RandomHeading();
            }

            // The map was chosen for a camp (a cluster of pinned spawns): walk there and stay inside it.
            var zone = OnHomeMap(snap.Map)
                       && _mapEstimates.TryGetValue(snap.Map, out var homeEstimate) ? homeEstimate.Zone : null;
            const int zoneMargin = 3;
            for (var attempt = 0; attempt < 6; attempt++)
            {
                // ponytail: a random 35-cell hop per 15 s is a random walk — net drift ~35·√n, so the bot circled one
                // corner. Aim at a random far cell anywhere on the map instead; combat still interrupts on the way.
                var tx = Random.Shared.Next(1, map.Width - 1);
                var ty = Random.Shared.Next(1, map.Height - 1);
                if (zone == null && Dist(self, new Position(tx, ty)) < Math.Min(map.Width, map.Height) / 3 && attempt < 5) continue;
                // Outside the camp every try used to aim at its centre, so one unreachable centre failed all six, forever:
                // Bot2 sat 23 min on mjo_dun01 where a Fly Wing dropped it. Centre once, then camp cells, then plain wander.
                var useZone = zone != null && attempt < 4;
                if (useZone)
                    (tx, ty) = zone!.Contains(self.X, self.Y, zoneMargin) || attempt > 0
                        ? (Random.Shared.Next(zone.MinX - zoneMargin, zone.MaxX + zoneMargin + 1), Random.Shared.Next(zone.MinY - zoneMargin, zone.MaxY + zoneMargin + 1))
                        : ((zone.MinX + zone.MaxX) / 2, (zone.MinY + zone.MaxY) / 2);
                if (!map.TryFindWalkableNear(tx, ty, 8, out var fx, out var fy) || (fx == self.X && fy == self.Y))
                {
                    _heading = RandomHeading();
                    continue;
                }
                var blocked = CellNearHazard(fx, fy)
                              || (_data != null && (_data.World.IsInPortalFootprint(snap.Map, fx, fy, margin: 3) || PathCrossesPortal(snap.Map, self, fx, fy)));
                if (blocked) { _heading = RandomHeading(); continue; }
                // A*-verify reachability with a generous budget — far targets need more nodes than the old
                // 2000 cap allowed, but a 6000 budget still completes in well under a millisecond.
                var pathCost = MakePathCost(snap.Map);
                var p = map.FindPath(self.X, self.Y, fx, fy, 0, pathCost); // far targets / camps may be across a maze
                if (p == null) { _heading = RandomHeading(); continue; }
                _wanderTarget = new Position(fx, fy);
                // Hold the target long enough to actually get there (0.15 s/cell walking, x2 for fights on the way).
                _wanderTargetUntil = DateTime.UtcNow.AddSeconds(useZone ? 15 : 20 + p.Count * 0.3);
                OnLog?.Invoke(!useZone ? $"Wander: heading to ({fx},{fy}) — {p.Count} cells via A*."
                    : zone!.Contains(self.X, self.Y, zoneMargin) ? $"Wander: staying in the camp, heading to ({fx},{fy}) — {p.Count} cells via A*."
                    : $"Wander: heading to the camp at ({fx},{fy}) this map was chosen for ({_mapEstimates.GetValueOrDefault(snap.Map)?.Note}) — {p.Count} cells via A*.");
                break;
            }
            if (_wanderTarget == null)
            {
                if (DateTime.UtcNow >= _nextWanderWedgeLog)
                {
                    _nextWanderWedgeLog = DateTime.UtcNow.AddSeconds(15);
                    OnLog?.Invoke($"Wander: no reachable destination in 6 attempts from ({self.X},{self.Y}) — wedged.");
                }
                return;
            }
        }

        // Step toward the sticky target via a short server-acceptable hop. Re-pathfind every tick so we
        // adapt to a moving fog of new hazards / unwalkable cells without going through stuck recovery.
        // Re-uses the danger overlay so a freshly-spawned aggressive mob in our path triggers a detour
        // mid-walk without needing to enter AvoidHazards / flee mode.
        var path = map.FindPath(self.X, self.Y, _wanderTarget!.Value.X, _wanderTarget!.Value.Y, 0, MakePathCost(snap.Map));
        if (path == null || path.Count <= 1)
        {
            // Mid-route path break — drop the target so next tick picks a fresh one rather than thrashing.
            _wanderTarget = null;
            return;
        }
        var wp = path[Math.Min(WanderHopCells, path.Count - 1)];
        await _bot.WalkToAsync(Math.Max(1, wp.x), Math.Max(1, wp.y), ct);
    }

    // Nearest portal on the map within `within` Chebyshev tiles of `self`.
    private bool TryNearestPortal(string map, Position self, int within, out int px, out int py)
    {
        px = 0; py = 0;
        var best = int.MaxValue;
        foreach (var p in _data!.PortalsOn(map))
        {
            var d = Dist(p.X, p.Y, self.X, self.Y);
            if (d <= within && d < best) { best = d; px = p.X; py = p.Y; }
        }
        return best != int.MaxValue;
    }

    // True if the straight line from `from` to (tx,ty) steps onto any real warp footprint.
    private bool PathCrossesPortal(string map, Position from, int tx, int ty)
    {
        var steps = Dist(tx, ty, from.X, from.Y);
        for (var i = 1; i <= steps; i++)
        {
            var x = from.X + (tx - from.X) * i / steps;
            var y = from.Y + (ty - from.Y) * i / steps;
            if (_data!.World.IsInPortalFootprint(map, x, y)) return true;
        }
        return false;
    }

    private (int dx, int dy) RandomHeading()
    {
        while (true)
        {
            var dx = _rng.Next(-1, 2);
            var dy = _rng.Next(-1, 2);
            if (dx != 0 || dy != 0) return (dx, dy);
        }
    }

    // Walk to a random nearby walkable cell to break out of a stuck position.
    private async Task NudgeAsync(Snapshot snap, CancellationToken ct)
    {
        if (await EscapeConfinementAsync(snap, ct)) return;
        var self = snap.SelfPos;
        var map = _data?.GetWalkMap(snap.Map);
        for (var i = 0; i < 10; i++)
        {
            var tx = self.X + _rng.Next(-6, 7);
            var ty = self.Y + _rng.Next(-6, 7);
            if (tx == self.X && ty == self.Y) continue;
            if (_data != null && _data.World.IsInPortalFootprint(snap.Map, tx, ty, margin: 1)) continue; // don't nudge into / next to a warp
            if (map == null || map.IsWalkable(tx, ty))
            {
                OnLog?.Invoke($"Stuck: no progress for {_config.StuckSeconds}s+ at ({self.X},{self.Y}) on {snap.Map} — nudging to ({tx},{ty}).");
                await _bot.WalkToAsync(Math.Max(1, tx), Math.Max(1, ty), ct);
                return;
            }
        }
    }

    // The server's pathfinder gives up past 20 cells (PathFinder.MaxDistance) and skills/attacks need line of sight, so
    // a target behind a wall got "Bash: no line of sight" spam with the bot standing still until it timed out as
    // unreachable. Walk our own uncapped A* until we can see it and the server can path the rest.
    private const int ServerMaxPath = 18; // 20 minus slack for the server stopping one cell short
    private DateTime _nextChaseWalk;
    private int _chaseLoggedFor;

    private async Task<bool> WalkAroundToTargetAsync(Snapshot snap, MonsterInfo target, CancellationToken ct)
    {
        var map = _data?.GetWalkMap(snap.Map);
        if (map == null) return false;
        string why;
        if (!map.HasLineOfSight(snap.SelfPos.X, snap.SelfPos.Y, target.Pos.X, target.Pos.Y)) why = "no line of sight";
        else if (Dist(snap.SelfPos, target.Pos) <= InferAttackRange(snap)) return false;
        else if (map.FindPath(snap.SelfPos.X, snap.SelfPos.Y, target.Pos.X, target.Pos.Y, 4000) is { } p && p.Count - 1 > ServerMaxPath)
            why = $"walk is {p.Count - 1} cells, server only paths {ServerMaxPath}";
        else return false;

        if (_chaseLoggedFor != target.Id)
        {
            _chaseLoggedFor = target.Id;
            OnLog?.Invoke($"Can't hit '{target.Name}' from here ({why}) — walking around to it myself.");
        }
        if (DateTime.UtcNow < _nextChaseWalk) return true;
        _nextChaseWalk = DateTime.UtcNow.AddSeconds(0.5);
        await WalkPathTowardAsync(snap, target.Pos.X, target.Pos.Y, 12, ct);
        return true;
    }

    // Last target WalkPathTowardAsync aimed at (travel, shop, kafra, bard...), so fleeing can keep heading there.
    private (string map, int x, int y, DateTime at)? _walkGoal;
}
