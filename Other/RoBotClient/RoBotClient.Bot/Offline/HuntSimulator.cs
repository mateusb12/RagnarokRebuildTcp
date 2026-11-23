using System.Security.Cryptography;
using System.Text;
using RebuildSharedData.ClientTypes;
using RoBotClient.Bot.Behavior;
using RoBotClient.Bot.GameData;
using RoBotClient.Bot.Session;
using RebuildSharedData.Data;
using RebuildSharedData.Enum;
using RoBotClient.Simulation;

namespace RoBotClient.Bot.Offline;

/// <summary>What to simulate: one map, one character build, a virtual duration and an RNG seed.</summary>
/// <param name="SkillScript">The bot's SkillScript (BotBehaviorConfig.SkillScript): the rules the live bot casts by.</param>
/// <param name="NoAutoAttack">BotBehaviorConfig.NoAutoAttack: a pure caster, the script is the whole fight.</param>
/// <param name="Config">The bot's saved BotBehaviorConfig (bot-configs.json); null runs the in-code defaults, and the report says so.</param>
/// <param name="ConfigSource">Where Config came from, for the report.</param>
public sealed record HuntSimOptions(string Map, PlayerBuild Build, double DurationSeconds, int Seed, int ForecastTrials = 300,
    string SkillScript = "", bool NoAutoAttack = false, BotBehaviorConfig? Config = null, string? ConfigSource = null);

/// <summary>
/// Offline hunting on one map: an isolated virtual world (the map's walk grid, its monster population with the server's
/// spawn areas and respawn timers, a virtual clock) and a solo melee bot that wanders, picks targets it can see with the
/// live bot's rules (HuntPolicy), walks to them over the real A* and fights them in the server-code duel simulator, and
/// sits to recover by the live rest rules (HuntPolicy.ShouldSit/ShouldStand) while the server's own regen runs.
/// Time jumps from event to event (a step taken, a monster entering view, a pick, a fight's end, a respawn); nothing
/// ticks at a fixed rate and nothing is rendered.
///
/// Modelled: walk grid and A* (WalkMap), view range, spawn counts, pinned spawn areas, respawn delays, target choice
/// (score, win rule, detour rule, portal buffer). Fights run in a ServerWorld: the player and every spawned monster are
/// server entities on a real server Map, the bot's skill rules are chosen by the live rule engine
/// (BotBehavior.ChooseSkillRule) and sent as the live packets to the server's own skill handler, so any skill executes
/// through the server's generic path; HP, SP, statuses and cooldowns carry from fight to fight and through rests.
/// Not modelled (each is listed in the report): monsters stand still between spawn and death (no wandering, chasing,
/// aggression or assisting), looting, potions and inventory, other players, hazards and fleeing, deaths' return trip.
/// </summary>
public sealed class HuntSimulator
{
    public const int ViewDistance = 21;             // ServerConfig.MaxViewDistance, Chebyshev (Position.InRange)
    public const double SecondsPerCell = 0.15;      // the server's base player MoveSpeed
    public const double DiagonalFactor = 1.4142;    // WorldObject: a diagonal step takes MoveSpeed * 1.4142
    private const int MeleeRange = 1;
    private const int ReachExpansions = 6000;       // IsReachable's A* budget

    private sealed class Mob
    {
        public int Id;
        public required MonsterDbEntry Db;
        public int MinX, MinY, MaxX, MaxY;          // where it (re)spawns
        public bool Pinned;
        public double RespawnMin, RespawnMax;       // seconds
        public int X, Y;
        public bool Alive;
        public int Life;                            // spawns so far; one encounter per life
        public int ServerId;                        // its server entity in the ServerWorld (0: unknown to the server data)
        public double DiedAt, RespawnAt;
    }

    /// <summary>CanWin: at full HP (the map-level "huntable" view); target picks re-check P at the current HP, as live.</summary>
    private sealed record Forecast(bool Known, bool CanWin, double Seconds, double DamageTaken, double WinRate, double Attacks, string Why, double Swing = 0,
        DuelPrediction? P = null);

    public sealed class TypeStats
    {
        public int Population, Seen, Picked, Killed, KilledByArea, Deaths;
        public long Exp;
        public double FightSeconds, DamageTaken;
        public readonly Dictionary<string, int> Rejected = new();
    }

    private readonly HuntSimOptions _o;
    private readonly GameDatabase _data;
    private readonly WalkMap _walk;
    private readonly BotBehaviorConfig _cfg;
    private readonly Random _rng;
    private readonly List<Mob> _mobs = new();
    private readonly Dictionary<int, Forecast> _forecasts = new();
    private CombatSheet? _sheet;
    private ServerWorld _world = null!;
    private readonly Dictionary<int, Mob> _byServerId = new();
    private readonly List<BotBehavior.SkillRule> _rules;
    private readonly BotBehavior.SkillChoiceInputs _inputs;
    private readonly Dictionary<CharacterSkill, double> _skillThrottle = new(); // the live _skillCooldown, on the virtual clock
    private double _nextSkillAt;                                                 // the live 0.25 s global skill throttle

    // State
    private double _t;
    private int _x, _y;
    private (int X, int Y)? _wanderDest;
    private readonly HashSet<int> _inView = new();
    private double _lastFightEnd;

    // Results
    public readonly List<string> Trace = new();
    public readonly Dictionary<string, TypeStats> Types = new();
    public readonly Dictionary<string, int> Rejections = new();
    public readonly List<double> SearchPerKill = new();
    public readonly Dictionary<CharacterSkill, int> RuleFired = new();
    public readonly Dictionary<string, int> RuleSkips = new();    // why a rule didn't fire, per decision (values stripped as live dedupes them)
    public readonly List<(double Hp, double Sp, string Statuses)> FightStarts = new(); // carried state at each engagement
    public int Rests, RestsToWin, SitRefusals, FightsEndedUnderHealHp;
    public double RestSeconds;
    private bool _restToWin;                        // the live _restToWin: the last pick found fights winnable at full HP only
    public int Kills, Deaths, PickDecisions, Picks, WanderDestinations, ViewEntries;
    public long Exp;
    public double WanderSeconds, ApproachSeconds, FightSeconds, IdleSeconds, NoHuntableAliveSeconds, DamageTaken;
    public int Steps, DiagonalSteps;
    public int WalkableCells;
    private readonly int[,] _visits;
    private readonly List<(int X, int Y)> _killSpots = new();
    public readonly List<string> Examples = new();  // verification evidence, first occurrence of each kind
    private readonly HashSet<string> _exampleKinds = new();
    private int _bestDetour;
    private double _longestRespawn;
    public string? RespawnExample;                  // the longest respawn seen
    public string? ObstacleExample;                 // the walk with the longest detour around blocked cells

    public HuntSimulator(HuntSimOptions options, GameDatabase data)
    {
        _o = options;
        _data = data;
        _cfg = options.Config ?? new BotBehaviorConfig();
        _walk = data.GetWalkMap(options.Map) ?? throw new ArgumentException($"No walk map for '{options.Map}'.");
        _rng = new Random(options.Seed);
        _visits = new int[(_walk.Width + 7) / 8, (_walk.Height + 7) / 8];
        var errors = new List<string>();
        _rules = BotBehavior.ParseSkillScript(options.SkillScript ?? "", errors);
        if (errors.Count > 0) throw new ArgumentException("skill script: " + string.Join("; ", errors));
        _inputs = new BotBehavior.SkillChoiceInputs(data, options.NoAutoAttack, (m, _) => ForecastOf(m.ClassId).Swing);
        for (var y = 0; y < _walk.Height; y++)
            for (var x = 0; x < _walk.Width; x++)
                if (_walk.IsWalkable(x, y)) WalkableCells++;
    }

    private string Name(Mob m) => m.Db.Name;
    private static int Dist(int x0, int y0, int x1, int y1) => Math.Max(Math.Abs(x0 - x1), Math.Abs(y0 - y1));
    private void Log(string s) => Trace.Add($"[{Math.Max(_t, _world?.Clock ?? 0),8:F2}s] ({_x},{_y}) {s}");
    private void Example(string kind, string text) { if (_exampleKinds.Add(kind)) Examples.Add($"{kind}: {text}"); }
    private TypeStats Stats(Mob m) => Types.TryGetValue(m.Db.Name, out var s) ? s : Types[m.Db.Name] = new TypeStats();

    // ---- world ----

    /// <summary>The map's spawn rules: counts and respawn delays from the monster database, pinned areas from the server's
    /// spawn scripts; the rest roam the map bounds shrunk by 14 (World.RespawnMonster with no spawn area).</summary>
    private void Populate()
    {
        var pinnedLeft = _data.SpawnAreas(_o.Map).GroupBy(a => a.MonsterCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.SelectMany(a => Enumerable.Repeat(a, a.Count)).ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var db in _data.MonstersById.Values.OrderBy(m => m.Id))
            foreach (var spawn in db.Spawns ?? new())
            {
                if (!string.Equals(spawn.Map, _o.Map, StringComparison.OrdinalIgnoreCase)) continue;
                for (var i = 0; i < Math.Max(1, spawn.Count); i++)
                {
                    var mob = new Mob
                    {
                        Id = _mobs.Count + 1, Db = db,
                        RespawnMin = Math.Max(0.4, Math.Min(spawn.RespawnMin, spawn.RespawnMax) / 1000.0), // Monster.Die: at least 0.4 s
                        RespawnMax = Math.Max(0.4, Math.Max(spawn.RespawnMin, spawn.RespawnMax) / 1000.0),
                        MinX = 14, MinY = 14, MaxX = _walk.Width - 15, MaxY = _walk.Height - 15,
                    };
                    if (pinnedLeft.TryGetValue(db.Code, out var areas) && areas.Count > 0)
                    {
                        var a = areas[0];
                        areas.RemoveAt(0);
                        (mob.MinX, mob.MinY, mob.MaxX, mob.MaxY, mob.Pinned) = (a.MinX, a.MinY, a.MaxX, a.MaxY, true);
                    }
                    _mobs.Add(mob);
                    Stats(mob).Population++;
                    Spawn(mob);
                }
            }
    }

    private void Spawn(Mob m)
    {
        // ponytail: uniform random walkable cell in the area (the server's FindPositionInRange); the 9-slice spawn-zone
        // formula (UseAccurateSpawnZoneFormula) is not reproduced.
        for (var tries = 0; tries < 500; tries++)
        {
            int x = _rng.Next(m.MinX, m.MaxX + 1), y = _rng.Next(m.MinY, m.MaxY + 1);
            if (!_walk.IsWalkable(x, y)) continue;
            Place(m, x, y);
            return;
        }
        if (_walk.TryFindWalkableNear((m.MinX + m.MaxX) / 2, (m.MinY + m.MaxY) / 2, 30, out var fx, out var fy))
            Place(m, fx, fy);
    }

    private void Place(Mob m, int x, int y)
    {
        (m.X, m.Y, m.Alive) = (x, y, true);
        m.Life++;
        if (!DuelSimulator.IsKnownMonster(m.Db.Id)) return;
        m.ServerId = _world.AddMonster(m.Db.Id, x, y); // a fresh server entity per life: full HP, no statuses
        _byServerId[m.ServerId] = m;
    }

    private void Respawns()
    {
        foreach (var m in _mobs)
        {
            if (m.Alive || m.RespawnAt > _t) continue;
            var delay = m.RespawnAt - m.DiedAt;
            if (delay < m.RespawnMin - 1e-9 || delay > m.RespawnMax + 1e-9)
                throw new InvalidOperationException($"{Name(m)} #{m.Id} respawned after {delay:F2}s, outside [{m.RespawnMin},{m.RespawnMax}]");
            Spawn(m);
            if (delay > _longestRespawn) (_longestRespawn, RespawnExample) = (delay, $"{Name(m)} #{m.Id} died at {m.DiedAt:F1}s, invisible to target selection until {m.RespawnAt:F1}s "
                + $"(delay {delay:F1}s within its rule [{m.RespawnMin:F0}s, {m.RespawnMax:F0}s]), back at ({m.X},{m.Y})"
                + (m.Pinned ? $" inside its pinned area ({m.MinX},{m.MinY})-({m.MaxX},{m.MaxY})" : ""));
        }
    }

    private bool HuntableAliveAnywhere() => _mobs.Any(m => m.Alive && ForecastOf(m).CanWin && m.Db.Exp > 0);

    private Forecast ForecastOf(Mob m) => ForecastOf(m.Db.Id);

    /// <summary>The target-selection forecast (win rule, exp/s score, expected swing for SkillPolicy): stateless duels at
    /// full HP with auto-attacks, as the live pick uses. Seeded by class: forecasts don't depend on the run seed.</summary>
    private Forecast ForecastOf(int classId)
    {
        if (_forecasts.TryGetValue(classId, out var f)) return f;
        if (!DuelSimulator.IsKnownMonster(classId)) return _forecasts[classId] = new Forecast(false, false, 0, 0, 0, 0, "unknown to the server data");
        var p = DuelSimulator.Simulate(new DuelRequest(_o.Build, classId, Trials: _o.ForecastTrials, Seed: classId * 7919 + 1));
        _sheet ??= p.Sheet;
        var canWin = HuntPolicy.ServerCanWin(p, p.Sheet.MaxHp, 0, _cfg.WinMargin, _cfg.MaxRoundsToKill);
        return _forecasts[classId] = new Forecast(true, canWin, p.Seconds.Mean, p.DamageTaken.Mean, p.WinRate, p.Attacks.Mean,
            canWin ? "" : $"win {p.WinRate:P0}, dmg taken {p.DamageTaken.Mean:F0}/{p.Sheet.MaxHp}, {p.Attacks.Mean:F0} attacks", p.DamagePerAction.Mean, p);
    }

    // ---- the bot ----

    private readonly HashSet<(int Id, int Life)> _seenLives = new();

    private List<Mob> Visible()
    {
        var visible = _mobs.Where(m => m.Alive && Dist(_x, _y, m.X, m.Y) <= ViewDistance).ToList();
        foreach (var m in visible)
            if (_seenLives.Add((m.Id, m.Life))) Stats(m).Seen++;
        return visible;
    }

    /// <summary>The live target rules (HuntPolicy.PickTarget, the same code BotBehavior.PickTarget runs) on what the bot can
    /// see, solo, with this world's forecasts and A*; returns the walk up to the first cell in attack range.</summary>
    private (Mob Mob, List<(int x, int y)> Path)? Pick(List<Mob> visible)
    {
        PickDecisions++;
        var world = new SimTargets(this);
        if (HuntPolicy.PickTarget(visible, world, _cfg, _data, _o.Map).Target is not { } m)
        {
            _restToWin = world.WinnableRested > 0; // as live PickTarget
            return null;
        }
        _restToWin = false;
        var path = world.Paths[m];
        // Stop at the first cell in attack range, as the server's attack move does.
        var stop = path.FindIndex(c => Dist(c.x, c.y, m.X, m.Y) <= MeleeRange);
        Picks++;
        var hidden = _mobs.Where(o => o.Alive && Dist(_x, _y, o.X, o.Y) > ViewDistance && ForecastOf(o).CanWin).ToList();
        if (hidden.Count > 0)
        {
            var nearest = hidden.MinBy(o => Dist(_x, _y, o.X, o.Y))!;
            Example("hidden monsters ignored", $"at {_t:F1}s the bot picked {Name(m)} {Dist(_x, _y, m.X, m.Y)} cells away from {visible.Count} in view; "
                + $"{hidden.Count} huntable monsters were alive out of view (nearest {Name(nearest)} #{nearest.Id} at {Dist(_x, _y, nearest.X, nearest.Y)} cells) and were not candidates");
        }
        return (m, path.Take(stop + 1).ToList());
    }

    /// <summary>What HuntPolicy.PickTarget reads here: the virtual world's monsters, the server-code duel forecast judged
    /// at the player's current HP (as live; no potion buffer: no inventory here; no shadow engine), plain A* to the monster's cell. Nothing is excluded beyond the shared rules: no deaths-avoid list,
    /// hazards, fixed-monster sniping or group pulls (monsters stand still and don't assist).</summary>
    private sealed class SimTargets(HuntSimulator s) : ITargetWorld<Mob, Forecast>
    {
        public readonly Dictionary<Mob, List<(int x, int y)>> Paths = new();
        public int WinnableRested;                  // too strong now, winnable at full HP: the live rest-to-win count

        public TargetSelf Self => new(true, 0, s._sheet!.MaxHp, s._o.Build.Vit, SecondsPerCell); // _sheet: set by the first forecast

        public TargetObservation Observe(Mob m)
        {
            var d = Dist(s._x, s._y, m.X, m.Y);
            if (d > ViewDistance) throw new InvalidOperationException("a candidate out of view");
            return new(m.Db.Id, m.Db.Exp, m.Db.Level, m.Db.HP, m.Db.HP, m.X, m.Y, d); // alive: full HP until engaged
        }

        public TargetReject? Excluded(Mob m) => null;

        public (TargetForecast<Forecast> Decision, TargetForecast<Forecast> Shadow)? Forecast(Mob m)
        {
            var f = s.ForecastOf(m);
            if (!f.Known) return null;
            var canWin = HuntPolicy.ServerCanWin(f.P!, s._world.Hp, 0, s._cfg.WinMargin, s._cfg.MaxRoundsToKill);
            var view = new TargetForecast<Forecast>(f, canWin, f.Seconds, f.DamageTaken);
            return (view, view);
        }

        public bool NoSafeCell(Mob m) => false;
        public bool PullsLosingGroup(Mob m) => false;

        public bool Reachable(Mob m)
        {
            var path = s._walk.FindPath(s._x, s._y, m.X, m.Y, ReachExpansions);
            if (path == null || !HuntPolicy.DetourAcceptable(path.Count - 1, Dist(s._x, s._y, m.X, m.Y))) return false;
            Paths[m] = path;
            return true;
        }

        public void Rejected(Mob m, TargetReject why)
        {
            var f = why == TargetReject.Unknown || why == TargetReject.TooStrong ? s.ForecastOf(m) : null;
            var restable = why == TargetReject.TooStrong && f!.CanWin; // ServerCanWin at full HP, as live re-forecasts at SelfMaxHp
            if (restable) WinnableRested++;
            s.Reject(m, why switch
            {
                TargetReject.NotHuntable => "not huntable",
                TargetReject.NearPortal => "near a portal",
                TargetReject.Unknown => f!.Why,
                TargetReject.TooStrong => restable ? "too strong at current HP" : "too strong",
                TargetReject.Unreachable => "unreachable or long detour",
                _ => why.ToString(),
            });
            if (why == TargetReject.TooStrong && !restable) s.Example("too strong", $"{s.Name(m)}: {f!.Why}");
        }
    }

    private void Reject(Mob m, string why)
    {
        Rejections[why] = Rejections.GetValueOrDefault(why) + 1;
        Stats(m).Rejected[why] = Stats(m).Rejected.GetValueOrDefault(why) + 1;
    }

    /// <summary>A far random reachable cell, as WanderAsync picks one (no camp zone, no hazards: neither exists here).</summary>
    private (int X, int Y)? WanderDestination()
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            int tx = _rng.Next(1, _walk.Width - 1), ty = _rng.Next(1, _walk.Height - 1);
            if (Dist(_x, _y, tx, ty) < Math.Min(_walk.Width, _walk.Height) / 3 && attempt < 5) continue;
            if (!_walk.TryFindWalkableNear(tx, ty, 8, out var fx, out var fy) || (fx == _x && fy == _y)) continue;
            if (_data.World.IsInPortalFootprint(_o.Map, fx, fy, margin: 3)) continue;
            if (_walk.FindPath(_x, _y, fx, fy) == null) continue;
            return (fx, fy);
        }
        return null;
    }

    /// <summary>Walks the path one cell at a time; returns early (false) when a monster enters view and stopOnSighting.</summary>
    private bool Walk(List<(int x, int y)> path, bool approach, bool stopOnSighting)
    {
        var detour = path.Count - 1 - Dist(path[0].x, path[0].y, path[^1].x, path[^1].y);
        if (detour > _bestDetour && path.Count > 2 && StraightLineBlocked(path[0].x, path[0].y, path[^1].x, path[^1].y)
            && Crop(path) is var crop && !crop.Contains("too long"))
            (_bestDetour, ObstacleExample) = (detour, $"from ({path[0].x},{path[0].y}) to ({path[^1].x},{path[^1].y}) the straight line crosses blocked cells; "
                + $"A* walks {path.Count - 1} steps (straight distance {Dist(path[0].x, path[0].y, path[^1].x, path[^1].y)}), every step walkable:\n" + crop);
        for (var i = 1; i < path.Count && _t < _o.DurationSeconds; i++)
        {
            var (nx, ny) = path[i];
            var diagonal = nx != _x && ny != _y;
            if (!_walk.IsWalkable(nx, ny) || Dist(_x, _y, nx, ny) != 1
                || (diagonal && (!_walk.IsWalkable(nx, _y) || !_walk.IsWalkable(_x, ny))))
                throw new InvalidOperationException($"illegal step ({_x},{_y}) -> ({nx},{ny})");
            var dt = SecondsPerCell * (diagonal ? DiagonalFactor : 1);
            if (!approach && !HuntableAliveAnywhere()) NoHuntableAliveSeconds += dt;
            _t += dt;
            if (approach) ApproachSeconds += dt; else WanderSeconds += dt;
            (_x, _y) = (nx, ny);
            Steps++;
            if (diagonal) DiagonalSteps++;
            _visits[_x / 8, _y / 8]++;
            Respawns();
            if (!stopOnSighting) continue;
            var now = Visible();
            var entered = now.Where(m => !_inView.Contains(m.Id)).ToList();
            _inView.Clear();
            foreach (var m in now) _inView.Add(m.Id);
            if (entered.Count == 0) continue;
            ViewEntries += entered.Count;
            Log($"in view: {string.Join(", ", entered.Select(m => $"{Name(m)} #{m.Id} at ({m.X},{m.Y})"))}");
            return false;
        }
        return true;
    }

    /// <summary>One encounter in the ServerWorld, from the HP/SP/statuses the last one left: the live rule engine picks
    /// each action, the server executes it. Returns false when the target wasn't killed.</summary>
    private bool Fight(Mob m)
    {
        Stats(m).Picked++;
        AdvanceWorld(walking: true); // statuses, world events and regen ran on while walking
        if (!m.Alive) return false; // a ground effect left behind finished it on the way
        _world.MovePlayer(_x, _y);
        var statuses = string.Join(",", _world.StatusesOf(0));
        FightStarts.Add((100.0 * _world.Hp / _world.MaxHp, 100.0 * _world.Sp / Math.Max(1, _world.MaxSp), statuses));
        Log($"engage {Name(m)} #{m.Id}: HP {_world.Hp}/{_world.MaxHp} SP {_world.Sp}/{_world.MaxSp}" + (statuses.Length > 0 ? $" [{statuses}]" : ""));
        var fight = _world.Fight(m.ServerId, () => Decide(m), Math.Min(300, _o.DurationSeconds - _t));
        _t = _world.Clock;
        FightSeconds += fight.Seconds;
        Stats(m).FightSeconds += fight.Seconds;
        DamageTaken += fight.DamageTaken;
        Stats(m).DamageTaken += fight.DamageTaken;
        foreach (var id in fight.Killed)
            if (_byServerId.Remove(id, out var dead)) Died(dead, fight, byArea: dead != m);
        foreach (var (id, mob) in _byServerId) (mob.X, mob.Y) = _world.MonsterPosition(id); // knockback moved them
        (_x, _y) = _world.PlayerPosition; // the player's own skills or walking into range moved it
        if (fight.Outcome == -1)
        {
            Deaths++;
            Stats(m).Deaths++;
            Log($"DIED to {Name(m)} #{m.Id} (death penalty, respawn and return trip are not simulated; revived at full HP, statuses cleared)");
            _world.Revive();
            return false;
        }
        if (fight.Outcome == 2)
        {
            Log($"fight with {Name(m)} #{m.Id} abandoned after {fight.Seconds:F1}s: moved out of straight-line reach (see trace)");
            return false;
        }
        if (fight.Outcome == 0)
        {
            Log($"fight with {Name(m)} #{m.Id} cut off after {fight.Seconds:F1}s ({(_t >= _o.DurationSeconds ? "run end" : "300 s limit")})");
            return false;
        }
        if (_cfg.HealHpPercent > 0 && _world.Hp < _cfg.HealHpPercent * _world.MaxHp) FightsEndedUnderHealHp++;
        return true;
    }

    /// <summary>The server world catches up with the bot's clock; monsters a lingering ground effect killed meanwhile die.</summary>
    private void AdvanceWorld(bool walking)
    {
        foreach (var id in _world.Advance(_t, walking))
            if (_byServerId.Remove(id, out var dead)) Died(dead, null, byArea: true);
    }

    private void Died(Mob m, EncounterResult? fight, bool byArea)
    {
        Kills++;
        Exp += m.Db.Exp;
        Stats(m).Killed++;
        if (byArea) Stats(m).KilledByArea++;
        Stats(m).Exp += m.Db.Exp;
        _killSpots.Add((m.X, m.Y));
        m.Alive = false;
        m.DiedAt = _t;
        m.RespawnAt = _t + m.RespawnMin + _rng.NextDouble() * (m.RespawnMax - m.RespawnMin);
        m.ServerId = 0;
        _inView.Remove(m.Id);
        Log($"killed {Name(m)} #{m.Id}" + (byArea ? " by area damage" : $" in {fight!.Seconds:F2}s ({fight.Actions} actions, {fight.SkillActions} skills, took {fight.DamageTaken} dmg)")
            + $" +{m.Db.Exp} exp; respawns at {m.RespawnAt:F1}s");
    }

    private const double LiveTickSeconds = 0.25; // BotBehavior's tick loop delay
    private const double ResitSeconds = 1.5;     // TickRestAsync's _nextSit throttle

    private (double Hp, double Sp) RestFractions() =>
        HuntPolicy.RestFractions(_cfg, _o.Build.JobId, _world.Hp, _world.MaxHp, _world.Sp, _world.MaxSp);

    private bool SendSit(bool sit)
    {
        var p = BotSession.SitStandPacket(sit);
        return _world.SitStand(p.Message, p.Length);
    }

    /// <summary>The live TickRestAsync on the virtual clock: HuntPolicy.ShouldSit on the player's HP/SP, the client's sit
    /// packet to the server, then every live tick the server's own regen runs (sitting rate) and HuntPolicy.ShouldStand
    /// decides; the server refusing a sit is re-sent every 1.5 s as live. Nothing threatens a sitting bot here (monsters
    /// stand still and don't aggress, no hazards), so 'threatened' is false. Returns whether the bot rested.</summary>
    private bool RestIfDue()
    {
        var canSit = HuntPolicy.CanSit(_o.Build.JobId, _o.Build.LearnedSkills.GetValueOrDefault(CharacterSkill.BasicMastery));
        if (!HuntPolicy.ShouldSit(_cfg, RestFractions(), _restToWin, canSit, threatened: false)) return false;
        AdvanceWorld(walking: true); // the walk up to here
        var low = HuntPolicy.LowOnHpSp(_cfg, RestFractions());
        var (start, hp0, sp0, regenHp0, regenSp0) = (_t, _world.Hp, _world.Sp, _world.RegenHp, _world.RegenSp);
        Rests++;
        if (!low) RestsToWin++;
        Log($"sit to rest: HP {_world.Hp}/{_world.MaxHp} SP {_world.Sp}/{_world.MaxSp} ("
            + (low ? $"under {_cfg.RestBelowPercent:P0}" : "rest to win: fights in view are winnable at full HP only") + ")");
        var nextSit = _t + ResitSeconds;
        if (!SendSit(true)) { SitRefusals++; Log("the server refused to sit"); }
        var lastHp = _world.Hp;
        while (_t < _o.DurationSeconds)
        {
            _t = Math.Min(_t + LiveTickSeconds, _o.DurationSeconds);
            AdvanceWorld(walking: false);
            Respawns();
            var hit = _world.Hp < lastHp;
            lastHp = _world.Hp;
            if (HuntPolicy.ShouldStand(_cfg, RestFractions(), _restToWin, hit, threatened: false))
            {
                if (!_world.Sitting || !SendSit(false)) break;
                Log("the server refused to stand; re-sent next tick"); // ponytail: live sends once and moves on
                continue;
            }
            if (!_world.Sitting && _t >= nextSit)
            {
                nextSit = _t + ResitSeconds;
                if (!SendSit(true)) SitRefusals++;
            }
        }
        _restToWin = false;
        RestSeconds += _t - start;
        Log($"stand after {_t - start:F2}s: HP {hp0}->{_world.Hp}/{_world.MaxHp} SP {sp0}->{_world.Sp}/{_world.MaxSp} "
            + $"(server regen +{_world.RegenHp - regenHp0} HP, +{_world.RegenSp - regenSp0} SP)");
        return true;
    }

    /// <summary>The live bot's skill tick (TickSkillsAsync) at an action slot: the 0.25 s global throttle, then
    /// BotBehavior.ChooseSkillRule on a snapshot of what the bot would know, then the rule's packet.</summary>
    private PlayerChoice Decide(Mob target)
    {
        var noRule = _o.NoAutoAttack ? PlayerChoice.Hold : PlayerChoice.AutoAttack;
        if (_rules.Count == 0) return noRule;
        var now = _world.Clock;
        if (now < _nextSkillAt) return noRule;
        var (hp, maxHp) = _world.MonsterHp(target.ServerId);
        var info = new BotBehavior.MonsterInfo(target.ServerId, target.Db.Id, target.Db.Name, new Position(target.X, target.Y), hp, maxHp);
        var skips = new List<string>();
        var usable = _rules.Where(r => !_world.Skills.TryGetValue(r.Skill, out var l) || l.Blocked == null).ToList();
        var snapshot = Snapshot(target);
        Func<CharacterSkill, int> learned = skill => _o.Build.LearnedSkills.TryGetValue(skill, out var lv) ? lv : 0;
        Func<CharacterSkill, bool> throttled = skill => _skillThrottle.TryGetValue(skill, out var until) && now < until;
        var choice = BotBehavior.ChooseSkillRule(usable, snapshot, info, _inputs, learned, throttled,
            (_, _) => null, // zeny: no purse here; the server's own validation rejects what the player can't pay
            skips);
        if (usable.Count < _rules.Count // a failed skill is withheld: count the decisions the live engine would still give it
            && BotBehavior.ChooseSkillRule(_rules, snapshot, info, _inputs, learned, throttled, (_, _) => null, new List<string>()) is var (wanted, _)
            && _world.Skills.TryGetValue(wanted.Skill, out var failed) && failed.Blocked != null)
            failed.SuppressedPicks++;
        foreach (var skip in skips)
        {
            var key = System.Text.RegularExpressions.Regex.Replace(skip, @", is [^;]*", ""); // as TickSkillsAsync dedupes
            RuleSkips[key] = RuleSkips.GetValueOrDefault(key) + 1;
        }
        if (choice is not var (rule, level)) return noRule;
        _nextSkillAt = now + 0.25;
        _skillThrottle[rule.Skill] = now + (rule.EveryCooldown > 0 ? rule.EveryCooldown : 1.5);
        RuleFired[rule.Skill] = RuleFired.GetValueOrDefault(rule.Skill) + 1;
        var packet = rule.Target switch
        {
            BotBehavior.SkillCastTarget.SelfCast => BotSession.SkillSelfPacket(rule.Skill, level),
            BotBehavior.SkillCastTarget.Ground => BotSession.SkillGroundPacket(rule.Skill, level, target.X, target.Y),
            _ => BotSession.SkillOnTargetPacket(rule.Skill, level, target.ServerId),
        };
        Log($"rule fires: {rule.Skill} L{level} ({rule.Target}) on {Name(target)} #{target.Id} (HP {hp}/{maxHp}, own HP {_world.Hp} SP {_world.Sp})");
        return new PlayerChoice(rule.Skill, packet.Message, packet.Length, false);
    }

    /// <summary>What the live bot's Snapshot would hold: own HP/SP/stats/statuses from the server entity, the monsters in
    /// view with their HP, the target's statuses.</summary>
    private BotBehavior.Snapshot Snapshot(Mob target)
    {
        var b = _o.Build;
        var s = new BotBehavior.Snapshot
        {
            SelfId = _world.PlayerId, SelfPos = new Position(_world.PlayerPosition.X, _world.PlayerPosition.Y), SelfHp = _world.Hp, SelfMaxHp = _world.MaxHp,
            SelfSp = _world.Sp, SelfMaxSp = _world.MaxSp, SelfLevel = b.Level, SelfJobId = b.JobId, SelfJobLevel = b.JobLevel,
            SelfVit = b.Vit, SelfInt = b.Int, SelfDex = b.Dex, SelfAgi = b.Agi, SelfLuk = b.Luk,
            SelfAttackInterval = _world.AttackDelay, Map = _o.Map,
        };
        foreach (var m in _mobs)
            if (m.Alive && m.ServerId != 0 && Dist(_x, _y, m.X, m.Y) <= ViewDistance)
            {
                var (hp, maxHp) = _world.MonsterHp(m.ServerId);
                s.Monsters.Add(new BotBehavior.MonsterInfo(m.ServerId, m.Db.Id, m.Db.Name, new Position(m.X, m.Y), hp, maxHp));
            }
        foreach (var (skill, level) in b.LearnedSkills) s.KnownSkills.Add((skill, level));
        foreach (var status in _world.StatusesOf(0)) s.SelfStatuses.Add(status);
        var onTarget = _world.StatusesOf(target.ServerId);
        if (onTarget.Count > 0) s.EntityStatuses[target.ServerId] = onTarget.ToHashSet();
        return s;
    }

    public void Run()
    {
        if (!_walk.TryFindWalkableNear(_walk.Width / 2, _walk.Height / 2, 60, out _x, out _y))
            throw new InvalidOperationException("no walkable start cell");
        _world = new ServerWorld(_o.Map, _o.Build, _x, _y, _rng.Next()) { OnEvent = Log };
        if (_world.MapWidth != _walk.Width || _world.MapHeight != _walk.Height)
            throw new InvalidOperationException($"server walk data {_world.MapWidth}x{_world.MapHeight} differs from the client's {_walk.Width}x{_walk.Height}");
        Populate();
        Log($"start on {_o.Map} ({_walk.Width}x{_walk.Height}, {WalkableCells} walkable cells), {_mobs.Count} monsters spawned");
        var lookNow = true;
        while (_t < _o.DurationSeconds)
        {
            Respawns();
            if (lookNow)
            {
                lookNow = false;
                if (RestIfDue()) { lookNow = true; continue; } // TickRestAsync runs before the target pick
                var visible = Visible();
                _inView.Clear();
                foreach (var m in visible) _inView.Add(m.Id);
                if (visible.Count == 0) _restToWin = false; // live PickTarget over nothing finds nothing winnable rested
                var pick = visible.Count > 0 ? Pick(visible) : null;
                if (pick is { } p)
                {
                    var (mob, path) = p;
                    Log($"target {Name(mob)} #{mob.Id} at ({mob.X},{mob.Y}), {path.Count - 1} steps (of {visible.Count} in view)");
                    Walk(path, approach: true, stopOnSighting: false);
                    if (_t >= _o.DurationSeconds) break;
                    if (Dist(_x, _y, mob.X, mob.Y) > MeleeRange) throw new InvalidOperationException("fight started out of range");
                    var searched = _t - _lastFightEnd;
                    if (Fight(mob)) SearchPerKill.Add(searched);
                    _lastFightEnd = _t;
                    _wanderDest = null; // WanderAsync picks afresh after a fight (its target expired meanwhile)
                    lookNow = true;
                    continue;
                }
                if (visible.Count > 0) Log($"no target among {visible.Count} in view");
                if (_restToWin && RestIfDue()) { lookNow = true; continue; }
            }
            if (_wanderDest is not { } dest || Dist(_x, _y, dest.X, dest.Y) <= 3)
            {
                _wanderDest = WanderDestination();
                if (_wanderDest == null)
                {
                    // Wedged (WanderAsync's "no reachable destination in 6 attempts"): it retries on its 0.8 s throttle.
                    if (IdleSeconds == 0 || !Trace[^1].Contains("wedged")) Log("wedged: no reachable wander destination in 6 attempts");
                    var wait = Math.Min(0.8, _o.DurationSeconds - _t);
                    _t += wait;
                    IdleSeconds += wait;
                    if (!HuntableAliveAnywhere()) NoHuntableAliveSeconds += wait;
                    lookNow = true;
                    continue;
                }
                WanderDestinations++;
                Log($"wander to ({_wanderDest.Value.X},{_wanderDest.Value.Y})");
            }
            var route = _walk.FindPath(_x, _y, _wanderDest.Value.X, _wanderDest.Value.Y);
            if (route == null) { _wanderDest = null; continue; }
            Walk(route, approach: false, stopOnSighting: true);
            lookNow = true; // stopped for a sighting, or arrived: look either way
        }
        var accounted = WanderSeconds + ApproachSeconds + FightSeconds + IdleSeconds + RestSeconds;
        if (Math.Abs(accounted - _t) > 1e-6)
            throw new InvalidOperationException($"clock {_t:F6}s but walking + fighting + idle + resting = {accounted:F6}s");
        Log($"end: {Kills} kills, {Exp} exp");
    }

    // ---- report ----

    /// <summary>SHA-256 of the full event trace: equal for equal inputs and seed.</summary>
    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", Trace))))[..16];

    public string Report()
    {
        var sb = new StringBuilder();
        void L(string s = "") => sb.AppendLine(s);
        var hours = _o.DurationSeconds / 3600;
        var b = _o.Build;
        L($"== Offline hunt: {_o.Map}, {_o.DurationSeconds / 60:F0} min virtual, seed {_o.Seed} ==");
        L($"Build: Lv {b.Level}/{b.JobLevel} job {b.JobId}, STR {b.Str} AGI {b.Agi} VIT {b.Vit} INT {b.Int} DEX {b.Dex} LUK {b.Luk}; "
          + $"gear {string.Join(", ", b.Equipment.Select(e => _data.ItemName(e.ItemId)))}; MaxHP {_sheet?.MaxHp}, attack delay {_sheet?.AttackDelay:F2}s");
        L($"Map: {_walk.Width}x{_walk.Height}, {WalkableCells} walkable cells, {_mobs.Count} monsters ({_mobs.Count(m => m.Pinned)} in pinned areas)");
        L($"Config: {(_o.Config != null ? _o.ConfigSource ?? "given" : "NONE SAVED: the in-code BotBehaviorConfig defaults")}; RestWhenNoPotions {_cfg.RestWhenNoPotions}, "
          + $"RestBelowPercent {_cfg.RestBelowPercent:P0}, WinMargin {_cfg.WinMargin}, MaxRoundsToKill {_cfg.MaxRoundsToKill}, MaxTargetHp {_cfg.MaxTargetHp}, "
          + $"hunt/ignore/force lists {_cfg.HuntClassIds.Count}/{_cfg.IgnoreClassIds.Count}/{_cfg.ForceHuntClassIds.Count}");
        if (_world.Failures.Count > 0)
        {
            // A failed world operation: its effect on the player was undone, the skill was withheld afterwards, so the
            // numbers below are NOT what the live bot would get.
            L();
            L($"!! RESULT INCOMPLETE: server code failed {_world.Failures.Count} time(s); the failing skill was withheld for the rest of the run");
            foreach (var (skill, l) in _world.Skills.Where(s => s.Value.Blocked != null))
                L($"!!   {skill}: first at {l.BlockedAt:F2}s during {l.BlockedStage}; {l.Blocked}; failures {l.Failures}, "
                  + $"later decisions that still picked it {l.SuppressedPicks}" + (l.RolledBack.Count > 0 ? $"; undone: {string.Join(", ", l.RolledBack.Distinct())}" : ""));
        }
        L();
        L("-- Results (sustained: sitting to recover is included in the time) --");
        L($"Kills {Kills}, deaths {Deaths}, base EXP {Exp} (database values, no server rate multiplier)");
        var huntHours = Math.Max(1e-9, (_o.DurationSeconds - RestSeconds) / 3600);
        L($"Kills/h {Kills / hours:F0}, EXP/h {Exp / hours:F0} sustained; {Exp / huntHours:F0} EXP/h over the {_o.DurationSeconds - RestSeconds:F0}s not spent sitting");
        double Pct(double s) => 100 * s / _o.DurationSeconds;
        L($"Time: wandering without a target {WanderSeconds:F0}s ({Pct(WanderSeconds):F1}%), walking to targets {ApproachSeconds:F0}s ({Pct(ApproachSeconds):F1}%), "
          + $"fighting {FightSeconds:F0}s ({Pct(FightSeconds):F1}%), resting {RestSeconds:F0}s ({Pct(RestSeconds):F1}%), wedged/idle {IdleSeconds:F0}s ({Pct(IdleSeconds):F1}%)");
        L($"Search (wander + walk-up) per kill: {Summary(SearchPerKill)}");
        L($"  histogram: {Histogram(SearchPerKill, new[] { 2.0, 5, 10, 20, 40, 80 })}");
        L($"Fight per kill: {(Kills > 0 ? FightSeconds / Math.Max(1, Kills + Deaths) : 0):F2}s mean");
        L($"Respawn: {NoHuntableAliveSeconds:F0}s of searching with no huntable monster alive anywhere on the map (waiting on respawns)");
        L($"Recovery: {DamageTaken:F0} HP lost in fights; sat {Rests} times ({Rests - RestsToWin} under {_cfg.RestBelowPercent:P0} HP"
          + $"{(HuntPolicy.KillsWithSpells(_cfg, b.JobId) ? "/SP" : "")}, {RestsToWin} to win), {RestSeconds:F0}s sitting{(SitRefusals > 0 ? $", {SitRefusals} sits refused by the server" : "")}; "
          + $"the server's regen gave +{_world.RegenHp} HP, +{_world.RegenSp} SP over the run");
        if (!_cfg.RestWhenNoPotions)
            L("!! RestWhenNoPotions is off: the live bot sits only to win a fight in view and otherwise relies on potions, which are not modelled here");
        if (_cfg.HealHpPercent > 0)
            L($"Potions: not modelled (no inventory); {FightsEndedUnderHealHp} fights ended under HealHpPercent {_cfg.HealHpPercent:P0}, where the live bot drinks "
              + $"{(_cfg.HealingItemIds.Count > 0 ? string.Join("/", _cfg.HealingItemIds.Select(_data.ItemName)) : "healing items")} if it carries any");
        if (FightStarts.Count > 0)
            L($"Carried state at engagement ({FightStarts.Count} fights): HP mean {FightStarts.Average(f => f.Hp):F0}% (min {FightStarts.Min(f => f.Hp):F0}%), "
              + $"SP mean {FightStarts.Average(f => f.Sp):F0}% (min {FightStarts.Min(f => f.Sp):F0}%); started with a status up: "
              + (FightStarts.Any(f => f.Statuses.Length > 0)
                  ? string.Join(", ", FightStarts.SelectMany(f => f.Statuses.Split(',', StringSplitOptions.RemoveEmptyEntries)).GroupBy(s => s).Select(g => $"{g.Key} {g.Count()}"))
                  : "none"));
        L($"Movement: {Steps} steps ({DiagonalSteps} diagonal), {WanderDestinations} wander destinations, {ViewEntries} monsters entered view");
        L($"Target selection: {PickDecisions} decisions, {Picks} picks; rejected candidates: "
          + (Rejections.Count == 0 ? "none" : string.Join(", ", Rejections.OrderByDescending(r => r.Value).Select(r => $"{r.Key} {r.Value}"))));
        L();
        SkillsReport(L);
        L();
        L("-- Monster types --");
        L($"{"monster",-16} {"pop",4} {"respawn",11} {"seen",5} {"picked",6} {"killed",6} {"by area",7} {"exp",7} {"fight s/kill",12} {"forecast",-28} rejected");
        foreach (var (name, s) in Types.OrderByDescending(t => t.Value.Killed).ThenByDescending(t => t.Value.Population))
        {
            var mob = _mobs.First(m => m.Db.Name == name);
            var f = ForecastOf(mob);
            L($"{name,-16} {s.Population,4} {Respawn(mob),11} {s.Seen,5} {s.Picked,6} {s.Killed,6} {s.KilledByArea,7} {s.Exp,7} {(s.Killed > 0 ? s.FightSeconds / s.Killed : 0),12:F2} "
              + $"{(!f.Known ? "unknown" : f.CanWin ? $"win {f.Seconds:F1}s, -{f.DamageTaken:F0}HP" : "too strong"),-28} "
              + string.Join(", ", s.Rejected.Select(r => $"{r.Key} {r.Value}")));
        }
        L();
        L("-- Beside it: the map-choice estimate's search model (BotBehavior.EstimateMaps, roaming case) --");
        var huntable = _mobs.Where(m => m.Db.Exp > 0 && ForecastOf(m).CanWin && !string.Equals(m.Db.Special, "Boss", StringComparison.OrdinalIgnoreCase)).ToList();
        if (huntable.Count > 0)
        {
            double n = huntable.Count, fight = huntable.Average(m => ForecastOf(m).Seconds), expPerKill = huntable.Average(m => (double)m.Db.Exp);
            var find = HuntPolicy.MapSearchSecondsPerKill(WalkableCells, n);
            var supply = huntable.Sum(m => m.Db.Exp / ((m.RespawnMin + m.RespawnMax) / 2)); // exp/s the map refills
            var perKill = fight + find;
            var estKills = Math.Min(3600 / perKill, supply * 3600 / expPerKill);
            L($"{"",-26} {"estimate",12} {"spatial sim",12}");
            L($"{"search s/kill",-26} {find,12:F2} {(SearchPerKill.Count > 0 ? SearchPerKill.Average() : 0),12:F2}");
            L($"{"fight s/kill",-26} {fight,12:F2} {(Kills > 0 ? FightSeconds / Kills : 0),12:F2}");
            L($"{"kills/h (no recovery)",-26} {estKills,12:F0} {Kills / hours,12:F0}");
            L($"{"EXP/h (no recovery)",-26} {estKills * expPerKill,12:F0} {Exp / hours,12:F0}");
            L($"Estimate: {n} huntable spread evenly over {WalkableCells} cells, 0.15 s x spacing per kill, capped by refill ({supply * 3600:F0} exp/h). "
              + "Both columns use the same server-code fight forecasts, so the difference is the search model.");
        }
        L();
        L("-- Where the bot went (8x8-cell blocks; ' ' blocked, '.' walkable unvisited, 1-9 time spent (log), '*' a kill) --");
        L(HeatMap());
        L();
        L("-- Not modelled (stated, not hidden) --");
        L("* Monsters stand still between spawn and death: no wandering, chasing, aggression, assist or swarms; live monsters move.");
        L("* Recovery: the live sit/stand rules on the server's sit packet and regen ticks; nothing interrupts a rest (no monster walks up or aggresses), and the target score's rest estimate (HuntPolicy.RestSeconds) stays the live formula.");
        L("* Fights: only the target swings back (once hit); area damage hurts neighbours but they don't react; monsters cast no skills. Knockback is the server's (walk data); a knocked monster doesn't walk back, the player walks to it in a straight line.");
        L("* World events (ground effects) run on 10 ms server frames: CreateEvent, timers, area-of-effect ticks, expiry and removal are the server's; event packets to clients are dropped.");
        L("* Skill rules are evaluated at action slots in a fight, not while walking up to the target as the live tick also does.");
        L("* No potions, no looting, no zeny/inventory (gemstones, arrows to consume), no party (ally rules never fire), no other players, no hazards/fleeing; a death revives at full HP on the spot (no penalty, no return trip).");
        L("* Re-targeting happens when a monster enters view or after a fight, not every tick as live; once picked, a target is walked to and fought.");
        L($"Trace fingerprint {Fingerprint()} ({Trace.Count} events)");
        return sb.ToString();
    }

    private void SkillsReport(Action<string> L)
    {
        L("-- Skills: executed by the server (bot packet -> PacketSkill -> CombatEntity.AttemptStart*Skill -> SkillHandler) --");
        L(_rules.Count == 0 ? "No skill script: auto-attacks only." : "Script: " + string.Join(" | ", (_o.SkillScript ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        var skills = _world.Skills.Keys.Union(RuleFired.Keys).Union(_rules.Select(r => r.Skill)).Distinct().OrderBy(s => s.ToString()).ToList();
        if (skills.Count > 0)
        {
            L($"{"skill",-20} {"lv",3} {"fired",6} {"sent",5} {"bars",5} {"cast s",7} {"queued",6} {"exec",5} {"SP",6} {"damage",8} {"hits",5} {"kills",5} {"healed",7}  statuses applied / server rejections / blocked");
            foreach (var skill in skills)
            {
                var l = _world.Skills.GetValueOrDefault(skill) ?? new SkillLedger();
                var notes = new List<string>();
                if (l.StatusesApplied.Count > 0) notes.Add(string.Join(", ", l.StatusesApplied.Select(s => $"{s.Key} x{s.Value}")));
                if (l.Rejected.Count > 0) notes.Add("rejected: " + string.Join(", ", l.Rejected.Select(s => $"{s.Key} x{s.Value}")));
                if (l.WorldEvents > 0) notes.Add($"world events {l.WorldEvents} ({string.Join(", ", l.EventKinds.Select(k => $"{k.Key} x{k.Value}"))}), "
                                                 + $"{l.EventsEnded} expired/cleaned up, mean life {(l.EventsEnded > 0 ? l.EventSeconds / l.EventsEnded : 0):F2}s");
                if (l.Knockbacks > 0) notes.Add($"knocked monsters back x{l.Knockbacks}");
                if (l.Blocked != null) notes.Add($"FAILED x{l.Failures} (INCOMPLETE, withheld after {l.BlockedAt:F2}s, {l.SuppressedPicks} picks withheld): {l.Blocked}");
                if (ServerWorld.IsGenericHandler(skill)) notes.Add("no server handler (SkillHandlerGenericCast)");
                L($"{skill,-20} {_o.Build.LearnedSkills.GetValueOrDefault(skill),3} {RuleFired.GetValueOrDefault(skill),6} {l.Requested,5} {l.CastBars,5} "
                  + $"{(l.CastBars > 0 ? l.CastSeconds / l.CastBars : 0),7:F2} {l.Queued,6} {l.Executed,5} {l.SpSpent,6} {l.Damage,8} {l.Hits,5} {l.KillingBlows,5} {l.Healed,7}  "
                  + string.Join("; ", notes));
            }
        }
        if (RuleSkips.Count > 0)
            L("Rules skipped (decisions): " + string.Join("; ", RuleSkips.OrderByDescending(s => s.Value).Take(8).Select(s => $"{s.Key} x{s.Value}")));
        var learned = _o.Build.LearnedSkills.Where(s => _rules.All(r => r.Skill != s.Key)).Select(s => s.Key).ToList();
        var passive = learned.Where(ServerWorld.IsPassive).ToList();
        var unscripted = learned.Except(passive).ToList();
        if (passive.Count > 0) L("Passive (the server's stat pipeline applies them at Player.Init): " + string.Join(", ", passive));
        if (unscripted.Count > 0) L("Learned, castable, but no rule in the script, so the live bot never casts them: "
            + string.Join(", ", unscripted.Select(s => ServerWorld.IsGenericHandler(s) ? $"{s} (no server handler)" : s.ToString())));
    }

    private static string Respawn(Mob m) => m.RespawnMin == m.RespawnMax ? $"{m.RespawnMin:F0}s" : $"{m.RespawnMin:F0}-{m.RespawnMax:F0}s";

    private static string Summary(List<double> v)
    {
        if (v.Count == 0) return "no kills";
        var s = v.OrderBy(x => x).ToList();
        double P(double q) => s[(int)Math.Min(s.Count - 1, Math.Floor(q * s.Count))];
        return $"mean {v.Average():F2}s, median {P(0.5):F2}s, p90 {P(0.9):F2}s, max {s[^1]:F2}s (n={v.Count})";
    }

    private static string Histogram(List<double> v, double[] edges)
    {
        var parts = new List<string>();
        var lo = 0.0;
        foreach (var hi in edges.Append(double.PositiveInfinity))
        {
            var n = v.Count(x => x >= lo && x < hi);
            parts.Add($"{(double.IsPositiveInfinity(hi) ? $"{lo:0}s+" : $"{lo:0}-{hi:0}s")}: {n}");
            lo = hi;
        }
        return string.Join(" | ", parts);
    }

    private string HeatMap()
    {
        int bw = _visits.GetLength(0), bh = _visits.GetLength(1);
        var kills = new HashSet<(int, int)>(_killSpots.Select(k => (k.X / 8, k.Y / 8)));
        var max = Math.Log(1 + _visits.Cast<int>().DefaultIfEmpty(0).Max());
        var sb = new StringBuilder();
        for (var by = bh - 1; by >= 0; by--) // north up
        {
            for (var bx = 0; bx < bw; bx++)
            {
                var walkable = 0;
                for (var y = by * 8; y < by * 8 + 8; y++)
                    for (var x = bx * 8; x < bx * 8 + 8; x++)
                        if (_walk.IsWalkable(x, y)) walkable++;
                var v = _visits[bx, by];
                sb.Append(kills.Contains((bx, by)) ? '*' : v > 0 ? (char)('1' + (int)Math.Min(8, 8 * Math.Log(1 + v) / Math.Max(1e-9, max))) : walkable > 16 ? '.' : ' ');
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // ---- verification helpers (the Host's huntsim --check) ----

    /// <summary>A straight line between two cells crosses a blocked cell (Bresenham), i.e. walking it would cross a wall.</summary>
    public bool StraightLineBlocked(int x0, int y0, int x1, int y1)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1, dy = Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1, err = dx - dy;
        while (x0 != x1 || y0 != y1)
        {
            if (!_walk.IsWalkable(x0, y0)) return true;
            var e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
        return false;
    }

    // '#' blocked, '.' walkable, 'o' the walked path, 'X' a blocked cell on the straight line, S/E the ends; north up.
    private string Crop(List<(int x, int y)> path)
    {
        int x0 = path.Min(p => p.x) - 2, x1 = path.Max(p => p.x) + 2, y0 = path.Min(p => p.y) - 2, y1 = path.Max(p => p.y) + 2;
        if (x1 - x0 > 70 || y1 - y0 > 40) return "  (path too long to draw)";
        var on = path.ToHashSet();
        var line = new HashSet<(int, int)>();
        int lx = path[0].x, ly = path[0].y, ex = path[^1].x, ey = path[^1].y;
        int dx = Math.Abs(ex - lx), sx = lx < ex ? 1 : -1, dy = Math.Abs(ey - ly), sy = ly < ey ? 1 : -1, err = dx - dy;
        while (lx != ex || ly != ey) { line.Add((lx, ly)); var e2 = 2 * err; if (e2 > -dy) { err -= dy; lx += sx; } if (e2 < dx) { err += dx; ly += sy; } }
        var sb = new StringBuilder();
        for (var y = y1; y >= y0; y--)
        {
            sb.Append("    ");
            for (var x = x0; x <= x1; x++)
                sb.Append((x, y) == path[0] ? 'S' : (x, y) == path[^1] ? 'E' : on.Contains((x, y)) ? 'o'
                    : !_walk.IsWalkable(x, y) ? (line.Contains((x, y)) ? 'X' : '#') : '.');
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}
