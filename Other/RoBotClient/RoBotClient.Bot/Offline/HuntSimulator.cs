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
    string SkillScript = "", bool NoAutoAttack = false, BotBehaviorConfig? Config = null, string? ConfigSource = null,
    (int X, int Y)? StartAt = null, (int X, int Y)? CrossTo = null, int? StartHp = null, int? StartSp = null);

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
/// Monsters near the bot run the game server's own AI (ServerWorld.ActivateMonster: target search, chase, attack, assist,
/// monster skills); distant ones wander by a copy of its idle AI (Wander), handed over at the view boundary (Boundary).
/// The bot reacts by the live rules (HuntPolicy: hazards, flee, threatened rest, group pulls), checked every step.
/// Items: drops and looting, and potions/buffs by the live rules (UseItems) through the server's item handler.
/// Not modelled (each is listed in the report): Butterfly Wings, zeny and weight, other players. A death ends the run.
/// </summary>
public sealed class HuntSimulator
{
    public const int ViewDistance = 21;             // ServerConfig.MaxViewDistance, Chebyshev (Position.InRange)
    public const double SecondsPerCell = 0.15;      // the server's base player MoveSpeed
    public const double DiagonalFactor = 1.4142;    // WorldObject: a diagonal step takes MoveSpeed * 1.4142
    // Where the approach stops: the server's CharacterStat.Range (bow range + Vulture Eye), not melee. A fixed 1 walked
    // archers up to every target, eating hits a live archer never takes.
    private int Reach => _world.AttackRange;
    // The server also refuses a ranged hit without line of sight (Player.PerformAttack), so the approach stops at the first
    // cell that has both, as its attack move does.
    // Range is the server's DistanceCache.IntDistance (rounded Euclidean), not the square Dist: 12 cells diagonal is 17.
    private bool CanHit(int x, int y, Mob m) => (int)Math.Round(Math.Sqrt((x - m.X) * (x - m.X) + (y - m.Y) * (y - m.Y))) <= Reach
                                                && _walk.HasLineOfSight(x, y, m.X, m.Y);
    private const int ReachExpansions = 6000;       // IsReachable's A* budget
    private const int MaxChaseRepaths = 30;         // ponytail: then give the target up; live has no such count

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
        public bool Locked;                         // wanders only inside its spawn area (LockMovementToSpawn)
        public double Speed = -1;                   // MonsterBase.MoveSpeed, s/cell; negative: never moves
        public double NextMoveAt;                   // idle until then (the AI's nextMoveUpdate)
        public List<(int x, int y)>? Path;          // the random move under way, Path[PathIdx] the next cell
        public int PathIdx;
        public double StepAt;                       // when the next cell is reached
        public int SyncedX = -1, SyncedY = -1;      // where the server map last had it (synced lazily: SyncServer)
        public bool Active;                         // its own server AI runs it (ServerWorld.ActivateMonster); else Wander does
        public object[]? Move;                      // its open HuntReplay.MobMoves entry (a wander, or server steps being appended)
        public double MoveEnd;                      // when that entry's last step arrives
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
    private readonly Random _mobRng;                // monster wandering: its own stream, so it doesn't shift the bot's draws
    private readonly Random _lootRng;
    private readonly Dictionary<int, int> _bag = new(); // item id -> count: the build's inventory, then what was picked up
    private readonly List<(int DropId, int ItemId, int Count, int X, int Y, double Expires)> _ground = new(); // drops not picked up yet
    private int _pickedUp; // items picked up this run
    private readonly List<BotBehavior.ItemRule> _itemRules; // the config's ItemScript, parsed by the live parser
    private readonly Dictionary<int, double> _itemRuleCooldown = new();
    private readonly Dictionary<int, int> _used = new(); // item id -> uses the server took
    private double _nextHeal, _nextBuff;
    private double _end;                            // the run's end: the requested duration, or the moment the player died
    private double _nextEscape;                     // BotBehavior.EscapeWingReady: every wing escape shares one throttle
    private bool _winged;                           // a Fly Wing teleported the player during this fight decision
    private readonly HashSet<int> _skipDrops = new(); // no path: BotBehavior's _skipDrops
    private int _nextDropId;
    private double _nextLoot;
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
    public int Teleports;
    public int Kills, Deaths, PickDecisions, Picks, WanderDestinations, ViewEntries;
    public long Exp;
    public double WanderSeconds, ApproachSeconds, FightSeconds, IdleSeconds, PostKillSeconds, NoHuntableAliveSeconds, DamageTaken;
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

    // Replay (HuntReplay): what a viewer needs to watch the run, recorded as it happens; nothing is recomputed later.
    private readonly List<ReplayEvent> _events = new();
    private readonly List<ReplayStep> _track = new();
    private readonly List<ReplayVitals> _vitals = new();
    private readonly List<object[]> _moves = new();     // HuntReplay.MobMoves
    /// <summary>A wander step's direction, by its digit in HuntReplay.MobMoves.</summary>
    public static readonly (int dx, int dy)[] StepDirs = { (0, 1), (1, 1), (1, 0), (1, -1), (0, -1), (-1, -1), (-1, 0), (-1, 1) };
    private Mob? _fightTarget;                      // the target of the fight under way
    // The live bot's hazard state (BotBehavior._hazards, _seenHazards, _fleeThreats, Mode == Fleeing), on the virtual clock.
    // Ids are server entity ids, as in the BotBehavior.MonsterInfo the shared rules read.
    private readonly List<(Position pos, int radius, string name, string engine, int id)> _hazards = new();
    private readonly Dictionary<int, (Position pos, int radius, double at)> _seenHazards = new();
    private readonly Dictionary<int, (int x, int y, int radius)> _immobileSeen = new();
    private readonly HashSet<int> _fleeThreats = new();
    private bool _fleeing;
    private string _lastFleeLog = "";
    private const double HazardMemorySeconds = 30;  // BotBehavior.HazardMemory
    private readonly Queue<(double At, int Hp)> _hpHistory = new(); // BotBehavior.NoteHp: the last 5 s
    private readonly HashSet<int> _hitMe = new();   // server ids that damaged the player (BotSession.HasHitMe)
    private Mob? _lastAttacker;                     // BotSession.LastAttackerName: who a death is blamed on
    public int Flees, FleeTicks, FightsLeft, Aggros, WalkInterruptions, Ambushes;
    public double FleeSeconds;
    private int _peakActive;
    private object? _pickDetail;                    // the last target decision's candidates, for its event

    public HuntSimulator(HuntSimOptions options, GameDatabase data)
    {
        _o = options;
        _end = options.DurationSeconds;
        _data = data;
        _cfg = options.Config ?? new BotBehaviorConfig();
        _walk = data.GetWalkMap(options.Map) ?? throw new ArgumentException($"No walk map for '{options.Map}'.");
        _rng = new Random(options.Seed);
        _mobRng = new Random(unchecked(options.Seed * 7919 + 17));
        _lootRng = new Random(unchecked(options.Seed * 104729 + 31)); // drops: own stream, the bot's and the world's draws stay as before
        foreach (var (id, n) in options.Build.Inventory ?? new Dictionary<int, int>()) _bag[id] = n;
        _visits = new int[(_walk.Width + 7) / 8, (_walk.Height + 7) / 8];
        var errors = new List<string>();
        _rules = BotBehavior.ParseSkillScript(options.SkillScript ?? "", errors);
        if (errors.Count > 0) throw new ArgumentException("skill script: " + string.Join("; ", errors));
        _itemRules = BotBehavior.ParseItemScript(_cfg.ItemScript ?? "", out _); // live skips bad lines too
        _inputs = new BotBehavior.SkillChoiceInputs(data, options.NoAutoAttack, (m, _) => ForecastOf(m.ClassId).Swing,
            m => ForecastOf(m.ClassId) is { P: { } p } f ? f.Swing / (1 + p.DoubleAttackRate) : ForecastOf(m.ClassId).Swing); // a skill hit never doubles
        for (var y = 0; y < _walk.Height; y++)
            for (var x = 0; x < _walk.Width; x++)
                if (_walk.IsWalkable(x, y)) WalkableCells++;
    }

    private string Name(Mob m) => m.Db.Name;
    /// <summary>A trace line, and the same line as a replay event of &lt;paramref name="kind"/&gt; with what the decision saw.</summary>
    private void Log(string s, string kind = "note", int mob = 0, object? detail = null, int x = -1, int y = -1)
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("sim: trace/log events");
        var t = Math.Max(_t, _world?.Clock ?? 0);
        Trace.Add($"[{t,8:F2}s] ({_x},{_y}) {s}");
        _events.Add(new ReplayEvent(t, kind, s, mob, x, y, Detail: detail));
    }

    /// <summary>A replay event only (no trace line, so the trace and its fingerprint stay as they were).</summary>
    private void Note(string kind, string text, int mob = 0, object? detail = null, int x = -1, int y = -1)
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("sim: trace/log events");
        _events.Add(new ReplayEvent(Math.Max(_t, _world?.Clock ?? 0), kind, text, mob, x, y, Detail: detail));
    }

    /// <summary>The server world's events: traced ones as before, and every one (hits, regen ticks, ground effects) for
    /// the replay at its own server time, with the player's HP/SP right after it.</summary>
    private void OnWorld(WorldEvent e)
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("sim: trace/log events");
        var mob = _byServerId.TryGetValue(e.Entity, out var m) ? m : null;
        // The server names monsters by entity id; the replay by its own ids.
        if (mob != null && e.Text != null && e.Kind is "ai" or "monster-cast") e = e with { Text = e.Text.Replace($"#{e.Entity}", $"#{mob.Id}") };
        if (e.Text != null) Trace.Add($"[{Math.Max(_t, _world.Clock),8:F2}s] ({_x},{_y}) {e.Text}");
        var onPlayer = e.Entity == _world.PlayerId;
        var kind = e.Kind == "hit" && onPlayer ? "hurt" : e.Kind == "ai" && e.Source == _world.PlayerId ? "aggro" : e.Kind;
        var skill = e.Skill != CharacterSkill.None ? e.Skill.ToString() : null;
        var attacker = kind == "hurt" && _byServerId.TryGetValue(e.Source, out var src) ? src : null;
        if (kind == "hurt")
        {
            if (attacker != null && e.Amount > 0) { _hitMe.Add(e.Source); _lastAttacker = attacker; }
            NoteHp(e.At);
        }
        if (kind == "aggro") Aggros++;
        if (kind == "hurt" && attacker != null && e.Amount > 0) _hurts++;
        var text = e.Text ?? kind switch
        {
            "hurt" => (attacker != null ? $"{Name(attacker)} #{attacker.Id}" + (attacker == _fightTarget ? "" : attacker.Db.Ai is { } ai ? $" ({ai}, not the target)" : " (not the target)")
                          : "a ground effect")
                      + (skill != null ? $" {skill}" : "")
                      + (e.Amount == 0 ? " missed the player" : $" hit the player for {e.Amount} -> HP {e.HpLeft}/{_world.MaxHp}"),
            "hit" => $"{skill ?? "auto-attack"} on {(mob != null ? $"{Name(mob)} #{mob.Id}" : $"entity {e.Entity}")}: "
                     + (e.Amount == 0 ? "miss" : $"{e.Amount} damage") + $" -> HP {e.HpLeft}",
            "regen" => $"regen tick ({(_world.Sitting ? "sitting" : "standing")}): +{e.Amount} HP, +{e.Amount2} SP",
            "queued" => $"{skill}: the server queued it behind the after-cast delay",
            "no-effect" => $"{skill}: the server took the packet without an error, but nothing observable happened (no SP spent, no cast bar, no status, no attack, no damage)",
            _ => e.Kind,
        };
        if (kind is "aggro" or "ai" && mob != null) CloseMove(mob); // what follows is a new stretch of its walk
        _events.Add(new ReplayEvent(e.At, kind, text, kind == "hurt" ? attacker?.Id ?? 0 : mob?.Id ?? 0, e.X, e.Y, e.Amount, e.HpLeft, skill,
            kind.StartsWith("area") ? new { entity = e.Entity } : kind is "aggro" or "ai" && mob != null ? new { ai = mob.Db.Ai, scanDist = mob.Db.ScanDist, aggressive = HuntPolicy.IsAggressive(mob.Db), serverAi = true }
            : kind == "hurt" && attacker != null ? new { attacker = attacker.Id, target = attacker == _fightTarget, motion = Math.Round(e.Motion, 3) }
            : kind == "no-effect" ? new { serverTarget = ServerWorld.DeclaredTarget(e.Skill) }
            : e.Hits > 1 ? new { motion = Math.Round(e.Motion, 3), hits = e.Hits } : e.Motion > 0 ? new { motion = Math.Round(e.Motion, 3) } : null)); // motion: the swing began that long before; hits: Double Attack (sprites only)
        Vitals(e.At);
    }

    private void Vitals(double t)
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("sim: trace/log events");
        if (_vitals.Count == 0 || _vitals[^1].Hp != _world.Hp || _vitals[^1].Sp != _world.Sp) _vitals.Add(new ReplayVitals(t, _world.Hp, _world.Sp));
    }
    private void Example(string kind, string text) { if (_exampleKinds.Add(kind)) Examples.Add($"{kind}: {text}"); }
    private TypeStats Stats(Mob m) => Types.TryGetValue(m.Db.Name, out var s) ? s : Types[m.Db.Name] = new TypeStats();

    // ---- world ----

    /// <summary>The map's spawn rules: counts and respawn delays from the monster database, pinned areas from the server's
    /// spawn scripts; the rest roam the map bounds shrunk by 14 (World.RespawnMonster with no spawn area).</summary>
    private void Populate()
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("sim: spawns/respawns");
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
                        (mob.MinX, mob.MinY, mob.MaxX, mob.MaxY, mob.Pinned, mob.Locked) = (a.MinX, a.MinY, a.MaxX, a.MaxY, true, a.Locked);
                    }
                    if (DuelSimulator.IsKnownMonster(db.Id)) mob.Speed = DuelSimulator.MonsterMoveSpeed(db.Id);
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
        (m.Path, m.NextMoveAt) = (null, _t); // a fresh monster's nextMoveUpdate has passed: it moves on its first AI update
        m.Life++;
        Note("spawn", $"{Name(m)} #{m.Id} {(m.Life == 1 ? "spawned" : "respawned")} at ({x},{y})", m.Id, x: x, y: y);
        if (!DuelSimulator.IsKnownMonster(m.Db.Id)) return;
        m.ServerId = _world.AddMonster(m.Db.Id, x, y, m.Locked ? (m.MinX, m.MinY, m.MaxX, m.MaxY) : null); // a fresh server entity per life: full HP, no statuses
        (m.SyncedX, m.SyncedY, m.Active, m.Move) = (x, y, false, null);
        _byServerId[m.ServerId] = m;
    }

    private void Respawns()
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("sim: spawns/respawns");
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
        Wander();
    }

    /// <summary>The server's idle AI for every monster nobody fights, up to now: wait 4-6 s (OutWaitStart), then walk to a
    /// random cell (OutRandomMoveStart), one cell per MoveSpeed (x1.4142 diagonal), and wait again. Monsters walk through
    /// each other: only the destination avoids occupied cells, as FindRandomMoveTargetInArea does.</summary>
    private void Wander()
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("sim: out-of-view monster wander");
        foreach (var m in _mobs)
        {
            if (!m.Alive || m.Speed <= 0 || m.Active) continue; // under its own server AI: the server walks it
            while (true)
            {
                if (m.Path != null)
                {
                    if (m.StepAt > _t) break;
                    (m.X, m.Y) = m.Path[m.PathIdx++];
                    if (m.PathIdx < m.Path.Count) { m.StepAt += StepSeconds(m, m.Path[m.PathIdx]); continue; }
                    m.Path = null;
                    m.NextMoveAt = m.StepAt + 4 + _mobRng.NextDouble() * 2;
                    continue;
                }
                if (m.NextMoveAt > _t) break;
                var at = m.NextMoveAt;
                var path = (m.Locked ? RandomMove(m, m.MinX, m.MinY, m.MaxX, m.MaxY, 10) : null) ?? RandomMove(m, 0, 0, _walk.Width - 1, _walk.Height - 1, 20);
                if (path == null) { m.NextMoveAt = at + 1; continue; } // ponytail: the AI retries on its next update; 1 s stands in for its tick
                (m.Path, m.PathIdx, m.StepAt) = (path, 1, at + StepSeconds(m, path[1]));
                _moves.Add(m.Move = new object[] { m.Id, Math.Round(at, 3), Math.Round(m.Speed, 3), path[0].x, path[0].y,
                    string.Concat(path.Skip(1).Select((c, i) => (char)('0' + Array.IndexOf(StepDirs, (c.x - path[i].x, c.y - path[i].y))))) });
            }
        }
    }

    /// <summary>Puts wandered monsters where they are on the server map. Only when the server is about to act on them (a
    /// fight, a live ground effect): moving it every step costs more than the rest of the run (Map visibility updates).</summary>
    private void SyncServer()
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("sim: view boundary/server sync");
        foreach (var m in _mobs)
            if (m.Alive && m.ServerId != 0 && (m.X, m.Y) != (m.SyncedX, m.SyncedY))
            {
                _world.MoveMonster(m.ServerId, m.X, m.Y);
                (m.SyncedX, m.SyncedY) = (m.X, m.Y);
            }
    }

    /// <summary>The boundary between the two monster modes. Within the server's view distance of the player (21, where
    /// Monster.Update wakes a monster's AI: IsAiActive once it has a visible player) a monster is handed to its own server
    /// AI, with its cell, its idle timer and the random move it was on. It is handed back to Wander only once it is out of
    /// view again AND its AI is at rest (idle, no target, not walking, not attacked): a chase or a fight never ends at the
    /// boundary. One authority at a time: Wander skips active monsters, and the server only updates active ones.</summary>
    private void Boundary()
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("sim: view boundary/server sync");
        _peakActive = Math.Max(_peakActive, _world.ActiveMonsters);
        foreach (var m in _mobs)
        {
            if (!m.Alive || m.ServerId == 0) continue;
            var d = Dist(_x, _y, m.X, m.Y);
            if (!m.Active && d <= ViewDistance)
            {
                var walkingTo = m.Path != null ? m.Path[^1] : ((int, int)?)null;
                if (!_world.ActivateMonster(m.ServerId, m.X, m.Y, m.Path != null ? _world.Clock : m.NextMoveAt, walkingTo)) continue;
                if (m.Path != null && m.Move != null) m.Move[5] = ((string)m.Move[5])[..(m.PathIdx - 1)]; // the steps it took; the server walks the rest
                (m.Active, m.Path, m.Move, m.SyncedX, m.SyncedY) = (true, null, null, m.X, m.Y);
            }
            else if (m.Active && d > ViewDistance && _world.TryDeactivateMonster(m.ServerId, out var nextMoveAt))
                (m.Active, m.NextMoveAt, m.Path, m.Move) = (false, nextMoveAt, null, null);
        }
    }

    /// <summary>A cell step the server AI made (ServerWorld.OnMonsterStep): the monster's position, and the replay's walk
    /// (consecutive steps at its speed append to one MobMoves entry, flagged as steps the server walked).</summary>
    private void OnMonsterStep(int serverId, int x, int y, double at, double speed)
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("sim: replay recording (monster steps)");
        if (!_byServerId.TryGetValue(serverId, out var m)) return;
        var (dx, dy) = (x - m.X, y - m.Y);
        (m.X, m.Y, m.SyncedX, m.SyncedY) = (x, y, x, y);
        var dir = Array.IndexOf(StepDirs, (dx, dy));
        if (dir < 0) { m.Move = null; return; } // not one cell (pushed, teleported): the next step starts afresh
        var step = speed * (dx != 0 && dy != 0 ? DiagonalFactor : 1);
        if (m.Move != null && m.Move.Length == 7 && Math.Abs(m.MoveEnd - (at - step)) <= 0.03 && (double)m.Move[2] == Math.Round(speed, 3))
            m.Move[5] = (string)m.Move[5] + (char)('0' + dir);
        else
        {
            _moves.Add(m.Move = new object[] { m.Id, Math.Round(at - step, 3), Math.Round(speed, 3), x - dx, y - dy, ((char)('0' + dir)).ToString(), 1 });
            m.MoveEnd = at - step;
        }
        m.MoveEnd += step;
    }

    private static void CloseMove(Mob m) => m.Move = null;

    private void NoteHp(double at)
    {
        _hpHistory.Enqueue((at, _world.Hp));
        while (_hpHistory.Count > 0 && at - _hpHistory.Peek().At > 5) _hpHistory.Dequeue();
    }

    private static double StepSeconds(Mob m, (int x, int y) next) => m.Speed * (next.x != m.X && next.y != m.Y ? DiagonalFactor : 1);

    /// <summary>FindRandomMoveTargetInArea: a cell within 9 of the monster, clipped to the area; the first quarter of the
    /// attempts refuse one 2 or fewer cells away or occupied (and, as the server, retry that same cell); TryMove needs a
    /// walkable cell and a path of at most PathFinder.MaxDistance (20) steps.</summary>
    private List<(int x, int y)>? RandomMove(Mob m, int minX, int minY, int maxX, int maxY, int attempts)
    {
        int x0 = Math.Max(minX, m.X - 9), y0 = Math.Max(minY, m.Y - 9), x1 = Math.Min(maxX, m.X + 9), y1 = Math.Min(maxY, m.Y + 9);
        if (x0 > x1 || y0 > y1) return null;
        int x = _mobRng.Next(x0, x1 + 1), y = _mobRng.Next(y0, y1 + 1);
        for (var i = 0; i < attempts; i++)
        {
            if (i < attempts / 4f && (Dist(m.X, m.Y, x, y) <= 2 || (x, y) == (_x, _y) || _mobs.Any(o => o.Alive && o.X == x && o.Y == y))) continue;
            if ((x, y) != (m.X, m.Y) && _walk.IsWalkable(x, y) && _walk.FindPath(m.X, m.Y, x, y, 2000) is { Count: > 1 and <= 21 } path) return path;
            (x, y) = (_mobRng.Next(x0, x1 + 1), _mobRng.Next(y0, y1 + 1));
        }
        return null;
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


    // ---- threats: the live hazard, flee, rest and pull rules (HuntPolicy), on this world's server-code forecasts ----

    private Position Self => new(_x, _y);

    /// <summary>The monsters in view as the live Snapshot holds them (server ids, HP from the server entities).</summary>
    private List<BotBehavior.MonsterInfo> ViewMonsters()
    {
        var list = new List<BotBehavior.MonsterInfo>();
        foreach (var m in _mobs)
            if (m.Alive && m.ServerId != 0 && Dist(_x, _y, m.X, m.Y) <= ViewDistance && !_world.HiddenFromPlayer(m.ServerId))
            {
                var (hp, maxHp) = _world.MonsterHp(m.ServerId);
                list.Add(new BotBehavior.MonsterInfo(m.ServerId, m.Db.Id, m.Db.Name, new Position(m.X, m.Y), hp, maxHp));
            }
        return list;
    }

    /// <summary>BotBehavior.ComputeHazards: every aggressive monster in view (HuntPolicy.HazardCandidate) the forecast at the
    /// current HP loses to, or the one being fled, is a hazard of HuntPolicy.HazardRadius; remembered for 30 s.</summary>
    private void ComputeHazards()
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("sim: hazards");
        _hazards.Clear();
        foreach (var m in _mobs)
        {
            if (!m.Alive || m.ServerId == 0 || Dist(_x, _y, m.X, m.Y) > ViewDistance) continue;
            if (!HuntPolicy.HazardCandidate(_cfg, m.Db.Id, m.Db)) continue;
            var f = ForecastOf(m);
            var loses = !f.Known || !HuntPolicy.ServerCanWin(f.P!, _world.Hp, PotionBuffer(), _cfg.WinMargin, _cfg.MaxRoundsToKill);
            var fleeingIt = _fleeing && _fleeThreats.Contains(m.ServerId);
            if (!HuntPolicy.IsHazard(loses, fleeingIt)) continue;
            var radius = HuntPolicy.HazardRadius(m.Db);
            _hazards.Add((new Position(m.X, m.Y), radius, m.Db.Name, loses ? "server" : "earlier (still fleeing it)", m.ServerId));
            _seenHazards[m.ServerId] = (new Position(m.X, m.Y), radius, _t);
        }
        foreach (var (id, h) in _seenHazards.ToList())
            if (_t - h.at > HazardMemorySeconds) _seenHazards.Remove(id);
        foreach (var m in ViewMonsters())
            if (HuntPolicy.ImmobileZone(_data.Monster(m.ClassId), m.Pos) is { } zone) _immobileSeen[m.Id] = zone;
    }

    /// <summary>BotBehavior.HazardZones: in view and remembered.</summary>
    private List<(int x, int y, int radius)> HazardZones() =>
        _hazards.Select(h => (h.pos.X, h.pos.Y, h.radius)).Concat(_seenHazards.Values.Select(h => (h.pos.X, h.pos.Y, h.radius))).ToList();

    /// <summary>BotBehavior.MakePathCost: hazard zones, fixed monsters' reach and the portal buffer priced on A*.</summary>
    private Func<int, int, float>? PathCost()
    {
        var zones = HazardZones();
        zones.AddRange(_immobileSeen.Values);
        return HuntPolicy.PathCost(_data, _o.Map, zones, _cfg.PortalSafeDistance);
    }

    private bool ApproachNearHazard(Mob m, out int hazard, out string where)
    {
        var zones = _hazards.Select(h => (h.id, h.pos, h.radius)).Concat(_seenHazards
            .Where(s => _hazards.All(h => h.id != s.Key) && Dist(_x, _y, s.Value.pos.X, s.Value.pos.Y) > s.Value.radius)
            .Select(s => (id: s.Key, s.Value.pos, s.Value.radius))).ToList();
        return HuntPolicy.ApproachNearHazard(Self, new Position(m.X, m.Y), zones, out hazard, out where);
    }

    /// <summary>BotBehavior.Ambushed: HP dropped from its peak in the last 5 s by 15%+ of max, or at all under 35%.</summary>
    private bool Ambushed(out string why)
    {
        why = "";
        if (_hpHistory.Count == 0) return false;
        var lost = _hpHistory.Max(h => h.Hp) - _world.Hp;
        if (lost <= 0) return false;
        why = $"lost {lost} HP in 5s, at {_world.Hp}/{_world.MaxHp}";
        return BotBehavior.IsAmbushed(_world.Hp + lost, _world.Hp, _world.MaxHp);
    }

    /// <summary>The flee the live AvoidHazardsAsync would make now (HuntPolicy.NearestHazardInReach, then ChooseFlee), or
    /// null when no hazard covers the bot. No walk goal (WalkPathTowardAsync isn't used for hunting on the home map, so
    /// live never sidesteps there either). A Fly Wing flee is offered when one is held and the escape throttle is off.</summary>
    private (int Index, int Distance, FleeAction Action)? FleeDecision()
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("sim: flee decision");
        ComputeHazards();
        var (idx, d) = HuntPolicy.NearestHazardInReach(_hazards, Self, _fleeing);
        if (idx < 0) return null;
        var hz = _hazards[idx];
        var action = HuntPolicy.ChooseFlee(_data, _o.Map, _walk, Self, _hazards, idx, d, walkGoal: null, HazardZones(), PathCost(), ViewMonsters(),
            hurt: () => (Ambushed(out var why), why), // ponytail: KillsMeFast (the hand-formula hit) isn't ported; Ambushed alone decides "hurt"
            canFlyWing: CanFlyWing);
        return (idx, d, action);
    }

    /// <summary>One live tick of AvoidHazardsAsync when a hazard covers the bot: the flee starts (or goes on) and its move is
    /// carried out for one tick: a walk of at least a step toward its waypoint, a fight back when cornered, or holding
    /// position. False when no hazard is in reach (an ongoing flee ends).</summary>
    private bool FleeIfThreatened()
    {
        if (FleeDecision() is not var (idx, d, action))
        {
            if (_fleeing)
            {
                Log("Hazard no longer in range — resuming normal behavior.", "flee-end");
                _fleeing = false;
            }
            _fleeThreats.Clear();
            _lastFleeLog = "";
            return false;
        }
        var hz = _hazards[idx];
        var threat = _byServerId.GetValueOrDefault(hz.id);
        if (!_fleeing)
        {
            Flees++;
            Log($"Avoiding '{hz.name}' (aggressive, {hz.engine} forecast says I'd lose) — fleeing. It's {d} cells away "
                + $"(danger radius {hz.radius}), I'm at ({_x},{_y}), it's at ({hz.pos.X},{hz.pos.Y}).", "flee", threat?.Id ?? 0, FleeDetail(hz, d, action));
        }
        _fleeing = true;
        _fleeThreats.Add(hz.id);
        _wanderDest = null;
        FleeTicks++;
        if (action.LogKind != _lastFleeLog && action.Message.Length > 0)
        {
            _lastFleeLog = action.LogKind;
            Log(action.Message, "flee-move", threat?.Id ?? 0, FleeDetail(hz, d, action));
        }
        switch (action.Kind)
        {
            case FleeKind.FightBack when _byServerId.TryGetValue(action.MonsterId, out var cornering):
                Fight(cornering, why: "cornered");
                break;
            case FleeKind.FlyWing:
                if (!FlyWing(action.Message)) Wait(LiveTickSeconds, flee: true);
                break;
            case FleeKind.Hold or FleeKind.FightBack:
                Wait(LiveTickSeconds, flee: true);
                break;
            default:
                var path = _walk.FindPath(_x, _y, action.X, action.Y, ReachExpansions); // the server walks the waypoint its own way
                if (path is not { Count: > 1 }) { Wait(LiveTickSeconds, flee: true); break; }
                Walk(path, WalkKind.Flee, stopOnSighting: false, maxSeconds: LiveTickSeconds);
                break;
        }
        return true;
    }

    private object FleeDetail((Position pos, int radius, string name, string engine, int id) hz, int d, FleeAction a)
    {
        var f = _byServerId.TryGetValue(hz.id, out var m) ? ForecastOf(m) : null;
        return new
        {
            hazard = m?.Id, name = hz.name, distance = d, radius = hz.radius, buffer = HuntPolicy.HazardBuffer, engine = hz.engine,
            ai = m?.Db.Ai, scanDist = m?.Db.ScanDist, hp = _world.Hp, maxHp = _world.MaxHp,
            veto = f?.P != null ? HuntPolicy.ServerWinVeto(f.P, _world.Hp, PotionBuffer(), _cfg.WinMargin, _cfg.MaxRoundsToKill) : "unknown to the server data",
            action = a.Kind.ToString(), x = a.X, y = a.Y, message = a.Message,
            rule = "HuntPolicy.ChooseFlee (the live AvoidHazardsAsync's move)",
        };
    }

    /// <summary>Time passes standing (a held flee, a wedged wander): the world runs on.</summary>
    private void Wait(double seconds, bool flee, bool postKill = false)
    {
        var dt = Math.Min(seconds, _end - _t);
        if (dt <= 0) return;
        _t += dt;
        if (flee) FleeSeconds += dt; else if (postKill) PostKillSeconds += dt; else IdleSeconds += dt;
        if (!flee && !HuntableAliveAnywhere()) NoHuntableAliveSeconds += dt;
        Respawns();
        AdvanceWorld(walking: false);
    }

    /// <summary>The pack this target would bring as the live EngagementGroup sees it, forecast as one fight on this world's
    /// engine: each member's server-code duel (kill time, damage taken), killed quickest first while the rest keep hitting
    /// (BattleSimulator.ForecastGroup's sequence). Wins when every duel is winnable in MaxRoundsToKill and the total damage
    /// stays within WinMargin of the current HP.</summary>
    private (bool CanWin, double Damage, string Members) GroupForecast(List<BotBehavior.MonsterInfo> group)
    {
        var parts = group.Select(g => (g, f: ForecastOf(g.ClassId))).ToList();
        var feasible = parts.All(p => p.f.Known && p.f.WinRate > 0 && p.f.Attacks <= _cfg.MaxRoundsToKill);
        var order = parts.OrderBy(p => p.f.Seconds).ToList();
        var dps = order.Select(p => p.f.Seconds > 0 ? p.f.DamageTaken / p.f.Seconds : 0).ToList();
        double remaining = dps.Sum(), damage = 0;
        for (var i = 0; i < order.Count; i++) { damage += remaining * order[i].f.Seconds; remaining -= dps[i]; }
        return (feasible && damage <= _cfg.WinMargin * _world.Hp, damage, string.Join(", ", group.Select(g => g.Name)));
    }

    /// <summary>The player's HP ran out (in a fight, walking or resting): the run ends here. EXP/h is over the time lived,
    /// so a map that kills the build early scores what it earned before dying.</summary>
    private void PlayerDeath(Mob? target, EncounterResult? fight)
    {
        Deaths++;
        var killer = _lastAttacker ?? target;
        Killer = killer != null ? Name(killer) : null;
        if (killer != null) Stats(killer).Deaths++;
        _end = Math.Max(_t, _world.Clock);
        _t = _end;
        Log($"DIED to {(killer != null ? $"{Name(killer)} #{killer.Id}" : "unknown")}" + (target != null && killer != target ? $" while fighting {Name(target)} #{target.Id}" : "")
            + $" at {_end:F1}s of {_o.DurationSeconds:F0}s: the run ends here", "death", killer?.Id ?? 0,
            new { fightSeconds = fight?.Seconds, damageTaken = fight?.DamageTaken, killer = killer?.Id, target = target?.Id, attackersOnMe = _hitMe.Count,
                forecastWinRate = target != null ? ForecastOf(target).WinRate : (double?)null });
        Vitals(_end);
    }

    // ---- the bot ----

    private readonly HashSet<(int Id, int Life)> _seenLives = new();

    private List<Mob> Visible()
    {
        var visible = _mobs.Where(m => m.Alive && Dist(_x, _y, m.X, m.Y) <= ViewDistance && !(m.ServerId != 0 && _world.HiddenFromPlayer(m.ServerId))).ToList();
        foreach (var m in visible)
            if (_seenLives.Add((m.Id, m.Life))) Stats(m).Seen++;
        return visible;
    }

    /// <summary>The live target rules (HuntPolicy.PickTarget, the same code BotBehavior.PickTarget runs) on what the bot can
    /// see, solo, with this world's forecasts and A*; returns the walk up to the first cell in attack range.</summary>
    private (Mob Mob, List<(int x, int y)> Path)? Pick(List<Mob> visible)
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("sim: target pick");
        PickDecisions++;
        var world = new SimTargets(this);
        var ranking = new List<(Mob M, double Score, bool Ok)>();
        var chosen = HuntPolicy.PickTarget(visible, world, _cfg, _data, _o.Map, ranking: ranking).Target;
        _pickDetail = PickDetail(visible, world, ranking, chosen);
        if (chosen is not { } m)
        {
            _restToWin = world.WinnableRested > 0; // as live PickTarget
            return null;
        }
        _restToWin = false;
        var path = world.Paths[m];
        // Stop at the first cell in attack range, as the server's attack move does.
        var stop = path.FindIndex(c => CanHit(c.x, c.y, m));
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

    /// <summary>What one target decision saw, per visible monster: where it was, its forecast, its score (the exp/s with
    /// walk and rest that HuntPolicy.PickTarget ranked by, handed back through its ranking) and the verdict the shared rules
    /// gave it. Read from the decision itself; nothing is re-decided.</summary>
    private object PickDetail(List<Mob> visible, SimTargets world, List<(Mob M, double Score, bool Ok)> ranking, Mob? chosen)
    {
        var rank = new Dictionary<Mob, (double Score, int Rank)>();
        for (var i = 0; i < ranking.Count; i++) rank[ranking[i].M] = (ranking[i].Score, i + 1);
        var rows = visible.Select(m =>
        {
            var f = ForecastOf(m);
            var why = world.Rejects.Where(r => r.M == m).Select(r => r.Why).ToList();
            var ranked = rank.TryGetValue(m, out var r);
            return new
            {
                mob = m.Id, name = Name(m), x = m.X, y = m.Y, distance = Dist(_x, _y, m.X, m.Y), exp = m.Db.Exp,
                forecastKnown = f.Known, winRate = f.WinRate, killSeconds = f.Seconds, damageTaken = f.DamageTaken,
                restSeconds = f.Known ? HuntPolicy.RestSeconds(_world.MaxHp, _o.Build.Vit, f.DamageTaken) : 0,
                expPerSecond = ranked ? -r.Score : (double?)null, // solo: the score is -exp/s
                rank = ranked ? r.Rank : (int?)null,
                forced = _cfg.ForceHuntClassIds.Contains(m.Db.Id),
                verdict = m == chosen ? (_cfg.ForceHuntClassIds.Contains(m.Db.Id) ? "chosen (in ForceHuntClassIds: the win rule is skipped for it)" : "chosen")
                    : why.Count > 0 ? string.Join("; ", why)
                    : ranked ? "ranked below the chosen target" : "not scored",
            };
        }).OrderBy(c => c.rank ?? int.MaxValue).ToList();
        return new { hp = _world.Hp, maxHp = _world.MaxHp, winMargin = _cfg.WinMargin, maxRoundsToKill = _cfg.MaxRoundsToKill,
            restToWin = chosen == null && world.WinnableRested > 0, candidates = rows };
    }

    /// <summary>What HuntPolicy.PickTarget reads here: the virtual world's monsters, the server-code duel forecast judged
    /// at the player's current HP plus the potion buffer (as live; no shadow engine), plain A* to the monster's cell. Nothing is excluded beyond the shared rules: no deaths-avoid list,
    /// hazards, fixed-monster sniping or group pulls (monsters don't aggress or assist).</summary>
    private sealed class SimTargets(HuntSimulator s) : ITargetWorld<Mob, Forecast>
    {
        public readonly Dictionary<Mob, List<(int x, int y)>> Paths = new();
        public readonly List<(Mob M, string Why)> Rejects = new(); // this decision's verdicts, for its replay event
        public int WinnableRested;                  // too strong now, winnable at full HP: the live rest-to-win count

        public TargetSelf Self => new(true, 0, s._sheet!.MaxHp, s._o.Build.Vit, SecondsPerCell); // _sheet: set by the first forecast

        public TargetObservation Observe(Mob m)
        {
            var d = Dist(s._x, s._y, m.X, m.Y);
            if (d > ViewDistance) throw new InvalidOperationException("a candidate out of view");
            return new(m.Db.Id, m.Db.Exp, m.Db.Level, m.Db.HP, m.Db.HP, m.X, m.Y, d); // alive: full HP until engaged
        }

        public TargetReject? Excluded(Mob m)
        {
            // BotBehavior.LiveTargets.Excluded: hazards (in or near a hazard's zone, or the straight walk crosses one). The
            // killer-class avoidance never applies (a death ends the run). No unreachable blacklist: nothing here gets stuck.
            if (s.ApproachNearHazard(m, out var hz, out var where))
            {
                Notes[m] = $"{where} the zone of {(s._byServerId.TryGetValue(hz, out var h) ? $"{s.Name(h)} #{h.Id}" : "a monster out of view")}";
                return TargetReject.Hazard;
            }
            return null;
        }

        public readonly Dictionary<Mob, string> Notes = new(); // why an exclusion or a pull veto hit, for the replay

        public (TargetForecast<Forecast> Decision, TargetForecast<Forecast> Shadow)? Forecast(Mob m)
        {
            var f = s.ForecastOf(m);
            if (!f.Known) return null;
            var canWin = HuntPolicy.ServerCanWin(f.P!, s._world.Hp, s.PotionBuffer(), s._cfg.WinMargin, s._cfg.MaxRoundsToKill);
            var view = new TargetForecast<Forecast>(f, canWin, f.Seconds, f.DamageTaken);
            return (view, view);
        }

        public bool NoSafeCell(Mob m) => false;
        // BotBehavior.LiveTargets.PullsLosingGroup: one already hitting us isn't a pull (idling leaves it on us).
        public bool PullsLosingGroup(Mob m)
        {
            var info = new BotBehavior.MonsterInfo(m.ServerId, m.Db.Id, m.Db.Name, new Position(m.X, m.Y), m.Db.HP, m.Db.HP);
            var snipes = HuntPolicy.IsImmobile(m.Db) && s._world.AttackRange > m.Db.Range + 1;
            var group = HuntPolicy.EngagementGroup(s._data, s.ViewMonsters(), s.Self, info, standsAtTarget: !snipes);
            if (group.Count <= 1 || s._hitMe.Contains(m.ServerId)) return false;
            var g = s.GroupForecast(group);
            if (g.CanWin) return false;
            Notes[m] = $"would pull {group.Count - 1} more ({g.Members}): ~{g.Damage:F0} damage vs WinMargin {s._cfg.WinMargin} x HP {s._world.Hp}";
            return true;
        }

        public bool Reachable(Mob m)
        {
            // As live IsReachable: A* with the hazard/portal cost (a target only reachable through a kill-you zone isn't);
            // the walk itself is the server's attack-walk, which takes the plain shortest way.
            var costed = s._walk.FindPath(s._x, s._y, m.X, m.Y, ReachExpansions, s.PathCost());
            if (costed == null || !HuntPolicy.DetourAcceptable(costed.Count - 1, Dist(s._x, s._y, m.X, m.Y))) return false;
            Paths[m] = s._walk.FindPath(s._x, s._y, m.X, m.Y, ReachExpansions) ?? costed;
            return true;
        }

        public void Rejected(Mob m, TargetReject why)
        {
            var f = why == TargetReject.Unknown || why == TargetReject.TooStrong ? s.ForecastOf(m) : null;
            var restable = why == TargetReject.TooStrong && f!.CanWin; // ServerCanWin at full HP, as live re-forecasts at SelfMaxHp
            if (restable) WinnableRested++;
            var label = why switch
            {
                TargetReject.NotHuntable => "not huntable",
                TargetReject.NearPortal => "near a portal",
                TargetReject.Unknown => f!.Why,
                TargetReject.TooStrong => restable ? "too strong at current HP" : "too strong",
                TargetReject.Unreachable => "unreachable or long detour",
                TargetReject.AvoidedClass => "avoided: it killed the bot",
                TargetReject.Hazard => "near a dangerous monster",
                TargetReject.Swarm => "would pull a group it loses to",
                _ => why.ToString(),
            };
            var veto = why == TargetReject.TooStrong ? HuntPolicy.ServerWinVeto(f!.P!, s._world.Hp, s.PotionBuffer(), s._cfg.WinMargin, s._cfg.MaxRoundsToKill) : null;
            Rejects.Add((m, veto != null ? $"{label}: {veto}" + (restable ? " (winnable at full HP: rest to win)" : "")
                : Notes.TryGetValue(m, out var note) ? $"{label}: {note}" : label));
            s.Reject(m, label);
            if (why == TargetReject.TooStrong && !restable) s.Example("too strong", $"{s.Name(m)}: {f!.Why}");
        }
    }

    private void Reject(Mob m, string why)
    {
        Rejections[why] = Rejections.GetValueOrDefault(why) + 1;
        Stats(m).Rejected[why] = Stats(m).Rejected.GetValueOrDefault(why) + 1;
    }

    /// <summary>A far random reachable cell, as WanderAsync picks one (no camp zone, no hazards: neither exists here).
    /// &lt;paramref name="attempts"/&gt; receives each random draw and why it was refused, for the replay.</summary>
    private (int X, int Y)? WanderDestination(List<string> attempts)
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("sim: wander pick");
        for (var attempt = 0; attempt < 6; attempt++)
        {
            int tx = _rng.Next(1, _walk.Width - 1), ty = _rng.Next(1, _walk.Height - 1);
            if (Dist(_x, _y, tx, ty) < Math.Min(_walk.Width, _walk.Height) / 3 && attempt < 5)
            { attempts.Add($"({tx},{ty}): closer than a third of the map ({Dist(_x, _y, tx, ty)} < {Math.Min(_walk.Width, _walk.Height) / 3})"); continue; }
            if (!_walk.TryFindWalkableNear(tx, ty, 8, out var fx, out var fy) || (fx == _x && fy == _y))
            { attempts.Add($"({tx},{ty}): no walkable cell within 8"); continue; }
            if (_data.World.IsInPortalFootprint(_o.Map, fx, fy, margin: 3)) { attempts.Add($"({fx},{fy}): next to a portal"); continue; }
            if (_walk.FindPath(_x, _y, fx, fy) == null) { attempts.Add($"({fx},{fy}): no path"); continue; }
            attempts.Add($"({tx},{ty}) -> walkable ({fx},{fy}): accepted");
            return (fx, fy);
        }
        return null;
    }

    private enum WalkKind { Wander, Approach, Flee, Loot }
    private enum WalkStop { Arrived, Sighting, TargetMoved, Threat, Hit, Died, Time }
    private int _hurts; // monster hits that landed on the player (OnWorld), to tell a step on which the bot was hit

    /// <summary>Walks the path one cell at a time. Every step the server world runs to the new moment with the player on
    /// its new cell (monsters under AI chase and attack meanwhile) and the boundary is re-drawn. Like the live tick, a
    /// hazard covering the bot stops any walk but a flee (AvoidHazardsAsync runs first, every tick); a hit stops a walk
    /// with no target (the live PickTarget runs every tick off-target: what's hitting it is a candidate now); a new
    /// monster in view stops a wander (stopOnSighting); the chased target moving stops an approach (re-path).</summary>
    private WalkStop Walk(List<(int x, int y)> path, WalkKind kind, bool stopOnSighting, Mob? chase = null, double maxSeconds = double.PositiveInfinity)
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("sim: walking (step loop)");
        var approach = kind == WalkKind.Approach;
        var goal = chase != null ? (chase.X, chase.Y) : default;
        var detour = path.Count - 1 - Dist(path[0].x, path[0].y, path[^1].x, path[^1].y);
        if (detour > _bestDetour && path.Count > 2 && StraightLineBlocked(path[0].x, path[0].y, path[^1].x, path[^1].y)
            && Crop(path) is var crop && !crop.Contains("too long"))
            (_bestDetour, ObstacleExample) = (detour, $"from ({path[0].x},{path[0].y}) to ({path[^1].x},{path[^1].y}) the straight line crosses blocked cells; "
                + $"A* walks {path.Count - 1} steps (straight distance {Dist(path[0].x, path[0].y, path[^1].x, path[^1].y)}), every step walkable:\n" + crop);
        Note("route", $"{(approach ? "walk to the target" : kind == WalkKind.Flee ? "flee" : kind == WalkKind.Loot ? "walk to a drop" : "wander")}: A* route of {path.Count - 1} steps to ({path[^1].x},{path[^1].y})"
            + (stopOnSighting ? ", stops when a monster comes into view" : ""), detail: new { approach, flee = kind == WalkKind.Flee, loot = kind == WalkKind.Loot, path = path.SelectMany(c => new[] { c.x, c.y }).ToArray() });
        var start = _t;
        for (var i = 1; i < path.Count && _t < _end; i++)
        {
            if (_t - start >= maxSeconds) return WalkStop.Time;
            var (nx, ny) = path[i];
            var diagonal = nx != _x && ny != _y;
            if (!_walk.IsWalkable(nx, ny) || Dist(_x, _y, nx, ny) != 1
                || (diagonal && (!_walk.IsWalkable(nx, _y) || !_walk.IsWalkable(_x, ny))))
                throw new InvalidOperationException($"illegal step ({_x},{_y}) -> ({nx},{ny})");
            var dt = SecondsPerCell * (diagonal ? DiagonalFactor : 1);
            if (kind == WalkKind.Wander && !HuntableAliveAnywhere()) NoHuntableAliveSeconds += dt;
            _t += dt;
            if (approach) ApproachSeconds += dt; else if (kind == WalkKind.Flee) FleeSeconds += dt; else if (kind == WalkKind.Loot) PostKillSeconds += dt; else WanderSeconds += dt;
            (_x, _y) = (nx, ny);
            _track.Add(new ReplayStep(_t, _x, _y, dt));
            Steps++;
            if (diagonal) DiagonalSteps++;
            _visits[_x / 8, _y / 8]++;
            _world.MovePlayer(_x, _y);
            Respawns();
            var hurts = _hurts;
            if (AdvanceWorld(walking: true)) return WalkStop.Died;
            if (kind == WalkKind.Flee) continue;
            if (FleeDecision() is var (hz, hd, _))
            {
                WalkInterruptions++;
                Log($"{(approach ? "approach" : "wander")} interrupted: {_hazards[hz].name} is {hd} cells away (danger radius {_hazards[hz].radius}) and the forecast loses", "threat",
                    _byServerId.GetValueOrDefault(_hazards[hz].id)?.Id ?? 0);
                return WalkStop.Threat;
            }
            if (chase != null && (chase.X, chase.Y) != goal) return WalkStop.TargetMoved; // it moved: re-path
            if (!approach && _hurts > hurts)
            {
                WalkInterruptions++;
                Log($"wander interrupted: hit by {(_lastAttacker != null ? $"{Name(_lastAttacker)} #{_lastAttacker.Id}" : "something")}", "threat", _lastAttacker?.Id ?? 0);
                return WalkStop.Hit;
            }
            if (!stopOnSighting) continue;
            var now = Visible();
            var entered = now.Where(m => !_inView.Contains(m.Id)).ToList();
            _inView.Clear();
            foreach (var m in now) _inView.Add(m.Id);
            if (entered.Count == 0) continue;
            ViewEntries += entered.Count;
            Log($"in view: {string.Join(", ", entered.Select(m => $"{Name(m)} #{m.Id} at ({m.X},{m.Y})"))}", "sighting", entered[0].Id,
                new { entered = entered.Select(m => m.Id).ToArray(), inView = now.Count, viewDistance = ViewDistance });
            return WalkStop.Sighting;
        }
        return WalkStop.Arrived;
    }

    /// <summary>One encounter in the ServerWorld, from the HP/SP/statuses the last one left: the live rule engine picks
    /// each action, the server executes it. Returns false when the target wasn't killed.</summary>
    private bool Fight(Mob m, string? why = null)
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("sim: fight bookkeeping");
        Stats(m).Picked++;
        if (AdvanceWorld(walking: true)) return false; // statuses, world events and regen ran on while walking
        if (!m.Alive) return false; // a ground effect left behind finished it on the way
        _world.MovePlayer(_x, _y);
        var statuses = string.Join(",", _world.StatusesOf(0));
        FightStarts.Add((100.0 * _world.Hp / _world.MaxHp, 100.0 * _world.Sp / Math.Max(1, _world.MaxSp), statuses));
        var fc = ForecastOf(m);
        var onMe = _mobs.Where(o => o != m && o.Alive && o.Active && _world.MonsterAi(o.ServerId).Target == _world.PlayerId).Select(o => o.Id).ToArray();
        Log($"engage {Name(m)} #{m.Id}" + (why != null ? $" ({why})" : "") + $": HP {_world.Hp}/{_world.MaxHp} SP {_world.Sp}/{_world.MaxSp}" + (statuses.Length > 0 ? $" [{statuses}]" : "")
            + (onMe.Length > 0 ? $"; {onMe.Length} other monster(s) already targeting the player" : ""), "engage", m.Id,
            new { forecastWinRate = fc.WinRate, forecastSeconds = fc.Seconds, forecastDamageTaken = fc.DamageTaken, forecastAttacks = fc.Attacks,
                statuses, noAutoAttack = _o.NoAutoAttack, rules = _rules.Count, why, targetingPlayer = onMe, targetAi = _world.MonsterAi(m.ServerId).State });
        SyncServer();
        _fightTarget = m;
        m.Path = null; // attacked: it stops wandering and fights
        var fight = _world.Fight(m.ServerId, () => Decide(m), Math.Min(300, _end - _t));
        _fightTarget = null;
        _t = _world.Clock;
        FightSeconds += fight.Seconds;
        Stats(m).FightSeconds += fight.Seconds;
        DamageTaken += fight.DamageTaken;
        Stats(m).DamageTaken += fight.DamageTaken;
        foreach (var id in fight.Killed)
            if (_byServerId.Remove(id, out var dead)) Died(dead, fight, byArea: dead != m);
        foreach (var (id, mob) in _byServerId) (mob.X, mob.Y, mob.SyncedX, mob.SyncedY) = _world.MonsterPosition(id) is var p ? (p.X, p.Y, p.X, p.Y) : default; // knockback moved them
        if (_world.PlayerPosition != (_x, _y)) _track.Add(new ReplayStep(_t, _world.PlayerPosition.X, _world.PlayerPosition.Y, 0));
        (_x, _y) = _world.PlayerPosition; // the player's own skills or walking into range moved it
        if (fight.Outcome == -1)
        {
            PlayerDeath(m, fight);
            Boundary();
            return false;
        }
        Boundary();
        if (fight.Outcome == 3)
        {
            FightsLeft++;
            Log($"fight with {Name(m)} #{m.Id} broken off after {fight.Seconds:F1}s: " + (_winged ? "a Fly Wing teleported the bot away" : "a hazard covers the bot (the live flee drops the target)"), "fight-end", m.Id);
            return false;
        }
        if (fight.Outcome == 2)
        {
            Log($"fight with {Name(m)} #{m.Id} abandoned after {fight.Seconds:F1}s: out of straight-line reach or hidden (see the abandon event)", "fight-end", m.Id);
            return false;
        }
        if (fight.Outcome == 0)
        {
            Log($"fight with {Name(m)} #{m.Id} cut off after {fight.Seconds:F1}s ({(_t >= _end ? "run end" : "300 s limit")})", "fight-end", m.Id);
            return false;
        }
        if (_cfg.HealHpPercent > 0 && _world.Hp < _cfg.HealHpPercent * _world.MaxHp) FightsEndedUnderHealHp++;
        // The live bot only sees the kill on its next 0.25 s tick, and a walk it sends meanwhile waits out the killing
        // swing's move lock (melee: AttackMotionTime 1.0 s vs damage at 0.6 s; a bow's are equal). Measured live (Bot5,
        // 503 kills): kill -> next action median 0.25 s, mean 0.62 s.
        var gap = Math.Max(LiveTickSeconds, _world.MoveLockUntil - _t);
        Wait(gap, flee: false, postKill: true);
        return true;
    }

    /// <summary>The server world catches up with the bot's clock; monsters a lingering ground effect killed meanwhile die.
    /// Monsters under their own AI act meanwhile. True when the player died on the way (handled by PlayerDeath: the world
    /// then runs on to the bot's clock with the player back at the start cell).</summary>
    private bool AdvanceWorld(bool walking)
    {
        var died = false;
        while (true)
        {
            if (_world.LiveEvents > 0) SyncServer(); // a ground effect may hit them where they walked
            foreach (var id in _world.Advance(_t, walking))
                if (_byServerId.Remove(id, out var dead)) Died(dead, null, byArea: true);
            if (!_world.PlayerDied) break;
            PlayerDeath(_fightTarget, null);
            died = true;
            break;
        }
        Boundary();
        return died;
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
            + $" +{m.Db.Exp} exp; respawns at {m.RespawnAt:F1}s", "kill", m.Id,
            new { exp = m.Db.Exp, byArea, respawnAt = m.RespawnAt, fightSeconds = fight?.Seconds, actions = fight?.Actions, skillActions = fight?.SkillActions,
                damageTaken = fight?.DamageTaken });
        DropItems(m);
    }

    /// <summary>Monster.DoMonsterDrops: the server's drop table, each drop on its own cell around the corpse (GetNextTileForDrop:
    /// the corpse's cell, then SE, W, N, ...), on the ground for 60 s (GroundItem.Expiration).</summary>
    private void DropItems(Mob m)
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("sim: items (drops/loot)");
        var i = 0;
        foreach (var (id, n) in ServerWorld.RollDrops(m.Db.Id, _lootRng))
        {
            var (dx, dy) = i == 0 ? (0, 0) : (i % 3) switch { 0 => (0, 1), 1 => (1, -1), _ => (-1, 0) };
            var (x, y) = _walk.IsWalkable(m.X + dx, m.Y + dy) ? (m.X + dx, m.Y + dy) : (m.X, m.Y);
            var drop = ++_nextDropId;
            _ground.Add((drop, id, n, x, y, _t + GroundItemSeconds));
            Log($"{Name(m)} #{m.Id} dropped {_data.ItemName(id)} x{n} at ({x},{y})", "drop", m.Id,
                new { dropId = drop, itemId = id, name = _data.ItemName(id), code = _data.Item(id)?.Code, count = n }, x, y);
            i++;
        }
    }

    /// <summary>BotBehavior.TickLootAsync, one tick: the nearest drop within LootRange (not blacklisted, not by a hazard) is
    /// sent a pickup at most every LootCooldownSeconds; the server walks the player next to it (PacketPickUpItem: within
    /// 1 cell) and picks it up on arrival. True when it looted (or tried), so the tick ends there as live.</summary>
    private bool LootTick()
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("sim: items (drops/loot)");
        _ground.RemoveAll(g => g.Expires <= _t);
        if (!_cfg.AutoLoot) return false;
        var near = _ground.Where(g => !_cfg.LootBlacklist.Contains(g.ItemId) && !_skipDrops.Contains(g.DropId)
                                     && !HuntPolicy.CellNearHazard(_hazards, g.X, g.Y) && Dist(g.X, g.Y, _x, _y) <= _cfg.LootRange)
            .OrderBy(g => Dist(g.X, g.Y, _x, _y)).ToList();
        if (near.Count == 0) return false;
        var g = near[0];
        if (_t < _nextLoot) Wait(_nextLoot - _t, flee: false, postKill: true); // the live _nextLoot throttle
        _nextLoot = _t + _cfg.LootCooldownSeconds;
        if (Dist(g.X, g.Y, _x, _y) > 1)
        {
            var path = _walk.FindPath(_x, _y, g.X, g.Y, ReachExpansions);
            if (path == null) { _skipDrops.Add(g.DropId); Log($"can't reach {_data.ItemName(g.ItemId)} at ({g.X},{g.Y}): skipped", "loot-skip", x: g.X, y: g.Y); return true; }
            var cut = path.FindIndex(c => Dist(c.x, c.y, g.X, g.Y) <= 1);
            if (Walk(path.Take(cut + 1).ToList(), WalkKind.Loot, stopOnSighting: false) != WalkStop.Arrived) return true;
        }
        // PacketPickUpItem / Player.Update: the server holds the pickup until the attack delay is over (the killing swing's)
        if (_t < _world.AttackCooldownUntil) Wait(_world.AttackCooldownUntil - _t, flee: false, postKill: true);
        if (!_ground.Remove(g)) return true; // expired on the way
        _world.PickedUp(_t); // Player.TryPickup: no attacking for 0.3 s after
        _pickedUp += g.Count;
        _world.AddItem(g.ItemId, g.Count);
        var total = _bag[g.ItemId] = _bag.GetValueOrDefault(g.ItemId) + g.Count;
        Log($"pickup {_data.ItemName(g.ItemId)} +{g.Count} ({total} in the bag)", "loot", 0,
            new { dropId = g.DropId, itemId = g.ItemId, name = _data.ItemName(g.ItemId), code = _data.Item(g.ItemId)?.Code, count = g.Count, total }, g.X, g.Y);
        Wait(LiveTickSeconds, flee: false, postKill: true); // the next tick sees the next drop
        return true;
    }

    private const double LiveTickSeconds = 0.25; // BotBehavior's tick loop delay
    private const double GroundItemSeconds = 60;   // GroundItem.Expiration
    private const double ResitSeconds = 1.5;     // TickRestAsync's _nextSit throttle

    private (double Hp, double Sp) RestFractions() =>
        HuntPolicy.RestFractions(_cfg, _o.Build.JobId, _world.Hp, _world.MaxHp, _world.Sp, _world.MaxSp);

    private bool Threatened() => HuntPolicy.Threatened(_data, ViewMonsters(), Self, _hitMe.Contains, _hazards.Count);

    /// <summary>What made Threatened true, in words (for the replay; the decision is HuntPolicy.Threatened's).</summary>
    private string ThreatText()
    {
        if (_hazards.Count > 0) return $"{_hazards[0].name} in view is a hazard (the forecast loses; danger radius {_hazards[0].radius})";
        var near = ViewMonsters().FirstOrDefault(m => Dist(_x, _y, m.Pos.X, m.Pos.Y) <= HuntPolicy.ThreatReach(_data.Monster(m.ClassId)) && (HuntPolicy.IsAggressive(_data.Monster(m.ClassId)) || _hitMe.Contains(m.Id)));
        return near == null ? "" : $"{near.Name} {Dist(_x, _y, near.Pos.X, near.Pos.Y)} cells away ({(_hitMe.Contains(near.Id) ? "it has hit the bot" : "aggressive")})";
    }

    private bool SendSit(bool sit)
    {
        var p = BotSession.SitStandPacket(sit);
        return _world.SitStand(p.Message, p.Length);
    }

    /// <summary>The live TickRestAsync on the virtual clock: HuntPolicy.ShouldSit on the player's HP/SP and HuntPolicy.Threatened
    /// (an aggressive monster or one that hit it within its reach, HuntPolicy.ThreatReach, or a hazard in view, forbids sitting), the client's sit
    /// packet to the server, then every live tick the world runs on (monsters may come for it), the server's regen runs
    /// (sitting rate) and HuntPolicy.ShouldStand decides (rested, hit, threatened); the server refusing a sit is re-sent
    /// every 1.5 s as live. Returns whether the bot rested.</summary>
    private bool RestIfDue()
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("sim: rest");
        var canSit = HuntPolicy.CanSit(_o.Build.JobId, _o.Build.LearnedSkills.GetValueOrDefault(CharacterSkill.BasicMastery));
        if (AdvanceWorld(walking: true)) return false; // the walk up to here
        ComputeHazards();
        var threatened = Threatened();
        if (!HuntPolicy.ShouldSit(_cfg, RestFractions(), _restToWin, canSit, threatened))
        {
            if (threatened && (HuntPolicy.LowOnHpSp(_cfg, RestFractions()) || _restToWin) && canSit)
                Note("sit-refused", $"wants to rest but won't sit: {ThreatText()}", detail: new { threatened = true, why = ThreatText() });
            return false;
        }
        var low = HuntPolicy.LowOnHpSp(_cfg, RestFractions());
        var (start, hp0, sp0, regenHp0, regenSp0) = (_t, _world.Hp, _world.Sp, _world.RegenHp, _world.RegenSp);
        Rests++;
        if (!low) RestsToWin++;
        var f0 = RestFractions();
        Log($"sit to rest: HP {_world.Hp}/{_world.MaxHp} SP {_world.Sp}/{_world.MaxSp} ("
            + (low ? $"under {_cfg.RestBelowPercent:P0}" : "rest to win: fights in view are winnable at full HP only") + ")", "sit", detail: new
            {
                hpFraction = f0.Hp, spFraction = f0.Sp, spCounts = HuntPolicy.KillsWithSpells(_cfg, _o.Build.JobId), restBelow = _cfg.RestBelowPercent,
                restWhenNoPotions = _cfg.RestWhenNoPotions, lowOnHpSp = low, restToWin = _restToWin, canSit, threatened,
                standAt = HuntPolicy.RestedFraction,
            });
        var nextSit = _t + ResitSeconds;
        if (!SendSit(true)) { SitRefusals++; Log("the server refused to sit", "sit-refused"); }
        var lastHp = _world.Hp;
        var standReason = "the run ended";
        while (_t < _end)
        {
            _t = Math.Min(_t + LiveTickSeconds, _end);
            Respawns();
            if (AdvanceWorld(walking: false)) { standReason = "died while sitting"; break; }
            var hit = _world.Hp < lastHp;
            lastHp = _world.Hp;
            ComputeHazards();
            threatened = Threatened();
            if (HuntPolicy.ShouldStand(_cfg, RestFractions(), _restToWin, hit, threatened))
            {
                var f = RestFractions(); // which of ShouldStand's conditions held, for the replay
                standReason = hit ? $"hit while sitting{(_lastAttacker != null ? $" by {Name(_lastAttacker)} #{_lastAttacker.Id}" : "")}"
                    : threatened ? $"threatened: {ThreatText()}"
                    : f.Hp >= HuntPolicy.RestedFraction && f.Sp >= HuntPolicy.RestedFraction
                    ? $"rested: HP {f.Hp:P0} and {(HuntPolicy.KillsWithSpells(_cfg, _o.Build.JobId) ? $"SP {f.Sp:P0}" : "SP (not counted)")} at or above {HuntPolicy.RestedFraction:P0}"
                    : "not resting by config (RestWhenNoPotions off, no rest-to-win)";
                if (!_world.Sitting || !SendSit(false)) break;
                Log("the server refused to stand; re-sent next tick", "stand-refused"); // ponytail: live sends once and moves on
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
            + $"(server regen +{_world.RegenHp - regenHp0} HP, +{_world.RegenSp - regenSp0} SP)", "stand",
            detail: new { reason = standReason, seconds = _t - start, regenHp = _world.RegenHp - regenHp0, regenSp = _world.RegenSp - regenSp0 });
        return true;
    }

    /// <summary>The live bot's skill tick (TickSkillsAsync) at an action slot: the 0.25 s global throttle, then
    /// BotBehavior.ChooseSkillRule on a snapshot of what the bot would know, then the rule's packet.</summary>
    private PlayerChoice Decide(Mob target)
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("sim: fight decisions (skill rules)");
        // The world keeps going around the fight: respawns, the replica's wander, the boundary (a monster walking into
        // view mid-fight comes under its AI and can join). Then the live tick order: AvoidHazardsAsync before the hunt.
        _t = Math.Max(_t, _world.Clock);
        if (_world.PlayerPosition != (_x, _y))
        {
            (_x, _y) = _world.PlayerPosition;
            _track.Add(new ReplayStep(_t, _x, _y, 0));
        }
        Respawns();
        Boundary();
        if (FleeDecision() is var (hz, hd, flee) && !(flee.Kind == FleeKind.FightBack && flee.MonsterId == target.ServerId))
        {
            Note("attack-decision", $"leave the fight: {_hazards[hz].name} is {hd} cells away (danger radius {_hazards[hz].radius}) and the forecast at HP {_world.Hp} loses (the live flee drops the target)",
                target.Id, FleeDetail(_hazards[hz], hd, flee));
            return PlayerChoice.Leave;
        }
        var noRule = _o.NoAutoAttack ? PlayerChoice.Hold : PlayerChoice.AutoAttack;
        var fallback = _o.NoAutoAttack ? "hold (NoAutoAttack: no auto-attack)" : "auto-attack";
        _winged = false;
        var used = UseItems(target);
        if (!_winged && EmergencyWing()) used = "emergency Fly Wing";
        if (_winged)
        {
            Note("attack-decision", $"leave the fight: {used} teleported the player", target.Id, new { reason = used });
            return PlayerChoice.Leave;
        }
        if (used != null)
        {
            Note("attack-decision", $"{fallback}: {used}", target.Id, new { reason = used });
            return noRule;
        }
        if (_rules.Count == 0) { Note("attack-decision", $"{fallback}: no skill script", target.Id, new { reason = "no skill script" }); return noRule; }
        var now = _world.Clock;
        if (now < _nextSkillAt)
        {
            Note("attack-decision", $"{fallback}: the live 0.25 s global skill throttle", target.Id, new { reason = $"global skill throttle until {_nextSkillAt:F2}s" });
            return noRule;
        }
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
        if (choice is not var (rule, level))
        {
            Note("attack-decision", $"{fallback}: no rule fired" + (skips.Count > 0 ? $" ({string.Join("; ", skips)})" : ""), target.Id,
                new { reason = "no rule could fire", skipped = skips, ownHp = _world.Hp, ownSp = _world.Sp, targetHp = hp, targetMaxHp = maxHp });
            return noRule;
        }
        _nextSkillAt = now + 0.25;
        _skillThrottle[rule.Skill] = now + (rule.EveryCooldown > 0 ? rule.EveryCooldown : 1.5);
        RuleFired[rule.Skill] = RuleFired.GetValueOrDefault(rule.Skill) + 1;
        var packet = rule.Target switch
        {
            BotBehavior.SkillCastTarget.SelfCast => BotSession.SkillSelfPacket(rule.Skill, level),
            BotBehavior.SkillCastTarget.Ground => BotSession.SkillGroundPacket(rule.Skill, level, target.X, target.Y),
            _ => BotSession.SkillOnTargetPacket(rule.Skill, level, target.ServerId),
        };
        Log($"rule fires: {rule.Skill} L{level} ({rule.Target}) on {Name(target)} #{target.Id} (HP {hp}/{maxHp}, own HP {_world.Hp} SP {_world.Sp})", "cast-decision", target.Id,
            new { rule = Describe(rule), ruleIndex = _rules.IndexOf(rule) + 1, ruleTarget = rule.Target.ToString(), serverTarget = ServerWorld.DeclaredTarget(rule.Skill), level, skippedBefore = skips, ownHp = _world.Hp, ownSp = _world.Sp, targetHp = hp, targetMaxHp = maxHp });
        return new PlayerChoice(rule.Skill, packet.Message, packet.Length, false);
    }

    /// <summary>BotBehavior's tick before the hunt, at an action slot in a fight: TickItemRulesAsync (a rule that fires
    /// ends the tick), then MaybeHealAsync and MaybeDrinkBuffAsync (both act while a target is set). What was used, or null;
    /// after a use the slot auto-attacks, as a skill packet inside the server's 0.2 s use-item input delay is dropped.
    /// ponytail: only at action slots in a fight; live also drinks while a monster hits it on the walk.</summary>
    private string? UseItems(Mob target)
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("sim: items (use)");
        var now = _world.Clock;
        if (_itemRules.Count > 0)
        {
            var snap = Snapshot(target);
            var rule = BotBehavior.ChooseItemRule(_itemRules, lhs => BotBehavior.ItemLhs(snap, lhs, 0, () => snap.Monsters.Count),
                id => _itemRuleCooldown.GetValueOrDefault(id) <= now, id => _bag.GetValueOrDefault(id) > 0 && _data.IsUsableItem(id));
            if (rule != null)
            {
                _itemRuleCooldown[rule.ItemId] = now + BotBehavior.ItemRuleCooldown(rule);
                var text = $"item rule \"use {rule.ItemId}{(rule.Conditions.Count > 0 ? " if " + string.Join(" and ", rule.Conditions.Select(c => $"{c.Lhs} {c.Op} {c.Value}")) : "")}\"";
                if (rule.ItemId == _cfg.ButterflyWingItemId) // ponytail: back to the save point = off this map; the run would be over
                    Log($"{text} fires: a Butterfly Wing leaves the hunting map, not simulated", "use-skip", target.Id, new { itemId = rule.ItemId });
                else if (rule.ItemId == _cfg.FlyWingItemId)
                    return FlyWing(text) ? $"Fly Wing ({text})" : $"{text} fired, the server refused it";
                else return Use(rule.ItemId, text) ? $"used {_data.ItemName(rule.ItemId)} ({text})" : $"{text} fired, the server refused it";
            }
        }
        var used = new List<string>();
        if (_cfg.HealHpPercent > 0 && _world.Hp <= _cfg.HealHpPercent * _world.MaxHp && now >= _nextHeal && HuntPolicy.HealItem(_data, _cfg, _bag) is var heal and > 0)
        {
            _nextHeal = now + _cfg.HealCooldownSeconds;
            if (Use(heal, $"HP {_world.Hp}/{_world.MaxHp} at or under HealHpPercent {_cfg.HealHpPercent:P0}")) used.Add(_data.ItemName(heal));
        }
        if (now >= _nextBuff && HuntPolicy.BuffToDrink(_data, _cfg, _o.Build.Level, _o.Build.JobId, _bag, _world.StatusesOf(0).ToList()) is { } buff)
        {
            _nextBuff = now + 5; // MaybeDrinkBuffAsync: the status packet lands after the use
            if (Use(buff.Id, $"buff {buff.Status} is not up")) used.Add(_data.ItemName(buff.Id));
        }
        return used.Count > 0 ? "used " + string.Join(" and ", used) : null;
    }

    /// <summary>The live UseInventoryItem packet through the server's PacketUseInventoryItem; one fewer in the bag when taken.</summary>
    private bool Use(int itemId, string why)
    {
        var (hp, sp, before) = (_world.Hp, _world.Sp, _world.StatusesOf(0).ToHashSet());
        var packet = BotSession.UseItemPacket(itemId);
        var name = _data.ItemName(itemId);
        if (!_world.UseItem(packet.Message, packet.Length))
        {
            var refusal = _world.UseItemRefusal(itemId) ?? "no reason found";
            Log($"use {name} ({why}): refused by the server ({refusal})", "use-refused", 0, new { itemId, name, refusal });
            return false;
        }
        var left = _bag[itemId] -= 1;
        if (left <= 0) _bag.Remove(itemId);
        _used[itemId] = _used.GetValueOrDefault(itemId) + 1;
        var gained = _world.StatusesOf(0).Except(before).Select(s => s.ToString()).ToArray();
        Log($"use {name} ({why}): HP {hp} -> {_world.Hp}, SP {sp} -> {_world.Sp}{(gained.Length > 0 ? ", gained " + string.Join(", ", gained) : "")}; {left} left", "use", 0,
            new { itemId, name, code = _data.Item(itemId)?.Code, count = 1, total = left, hp = _world.Hp - hp, sp = _world.Sp - sp, statuses = gained });
        Vitals(_world.Clock);
        return true;
    }

    private bool CanFlyWing() => _t >= _nextEscape && _bag.GetValueOrDefault(_cfg.FlyWingItemId) > 0 && _data.IsUsableItem(_cfg.FlyWingItemId);

    /// <summary>BotBehavior.TryEmergencyFlyWingAsync: under 25% HP and still bleeding (Ambushed), wing out.</summary>
    private bool EmergencyWing() =>
        _world.Hp < _world.MaxHp * 0.25 && Ambushed(out var hurt) && CanFlyWing() && FlyWing($"Emergency: {hurt}");

    /// <summary>A Fly Wing through the server (its OnUse is CombatEntity.RandomTeleport: Map.FindRandomPositionOnMap); the bot
    /// follows the server to the new cell, drops its target and wander goal, and the escape throttle starts (3 s, as live).</summary>
    private bool FlyWing(string why)
    {
        _nextEscape = Math.Max(_t, _world.Clock) + 3;
        var from = (_x, _y);
        if (!Use(_cfg.FlyWingItemId, why)) return false;
        _t = Math.Max(_t, _world.Clock);
        (_x, _y) = _world.PlayerPosition;
        _track.Add(new ReplayStep(_t, _x, _y, 0));
        _visits[_x / 8, _y / 8]++;
        (_wanderDest, _winged) = (null, true);
        Teleports++;
        Log($"Fly Wing: ({from.Item1},{from.Item2}) -> ({_x},{_y})", "teleport", 0, new { fromX = from.Item1, fromY = from.Item2, x = _x, y = _y, why });
        Boundary();
        return true;
    }

    /// <summary>The potion buffer the live forecast adds to HP (HuntPolicy.PotionHpBuffer), from what is left in the bag.</summary>
    private double PotionBuffer() => HuntPolicy.PotionHpBuffer(_data, _cfg, _itemRules, _bag, _world.MaxHp);

    private static string Describe(BotBehavior.SkillRule r) =>
        $"use {r.Skill} {(r.Level > 0 ? r.Level.ToString() : "max")} on {r.Target}" + (r.EveryCooldown > 0 ? $" every {r.EveryCooldown}s" : "")
        + (r.Conditions.Count > 0 ? " if " + string.Join(" and ", r.Conditions.Select(c => $"{c.Lhs} {c.Op} {c.Value}")) : "");

    /// <summary>What the live bot's Snapshot would hold: own HP/SP/stats/statuses from the server entity, the monsters in
    /// view with their HP, the target's statuses.</summary>
    private BotBehavior.Snapshot Snapshot(Mob target)
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("sim: snapshot");
        var b = _o.Build;
        var s = new BotBehavior.Snapshot
        {
            SelfId = _world.PlayerId, SelfPos = new Position(_world.PlayerPosition.X, _world.PlayerPosition.Y), SelfHp = _world.Hp, SelfMaxHp = _world.MaxHp,
            SelfSp = _world.Sp, SelfMaxSp = _world.MaxSp, SelfLevel = b.Level, SelfJobId = b.JobId, SelfJobLevel = b.JobLevel,
            SelfVit = b.Vit, SelfInt = b.Int, SelfDex = b.Dex, SelfAgi = b.Agi, SelfLuk = b.Luk,
            SelfAttackInterval = _world.AttackDelay, Map = _o.Map,
            SelfWeaponItemId = b.Equipment.FirstOrDefault(e => e.Slot == EquipSlot.RightHand).ItemId, SelfAmmoId = b.AmmoItemId,
        };
        foreach (var m in _mobs)
            if (m.Alive && m.ServerId != 0 && Dist(_x, _y, m.X, m.Y) <= ViewDistance && !_world.HiddenFromPlayer(m.ServerId))
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

    /// <summary>Share of the virtual duration simulated (0..1), after each step of the main loop.</summary>
    public Action<double>? OnProgress;

    public void Run()
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("sim: main loop");
        var (sx, sy) = _o.StartAt ?? (_walk.Width / 2, _walk.Height / 2);
        if (!_walk.TryFindWalkableNear(sx, sy, 60, out _x, out _y))
            throw new InvalidOperationException("no walkable start cell");
        _world = new ServerWorld(_o.Map, _o.Build, _x, _y, _rng.Next()) { OnEvent = OnWorld, OnMonsterStep = OnMonsterStep };
        if (_o.StartHp is { } hp0) _world.SetVitals(hp0, _o.StartSp ?? _world.MaxSp);
        _track.Add(new ReplayStep(0, _x, _y, 0));
        Vitals(0);
        if (_world.MapWidth != _walk.Width || _world.MapHeight != _walk.Height)
            throw new InvalidOperationException($"server walk data {_world.MapWidth}x{_world.MapHeight} differs from the client's {_walk.Width}x{_walk.Height}");
        Populate();
        Log($"start on {_o.Map} ({_walk.Width}x{_walk.Height}, {WalkableCells} walkable cells), {_mobs.Count} monsters spawned", "start");
        Boundary();
        var lookNow = true;
        var (stuckAt, stuckTicks) = (-1.0, 0);
        if (_o.CrossTo is { } exit) RunCross(exit);
        else while (_t < _end)
        {
            OnProgress?.Invoke(_t / _end);
            // A tick that never moves the clock repeats forever: fail with what it keeps doing instead of hanging.
            if (_t != stuckAt) (stuckAt, stuckTicks) = (_t, 0);
            else if (++stuckTicks > 10_000)
                throw new InvalidOperationException($"simulation stuck at {_t:0.##}s (clock not advancing); last events:\n  " + string.Join("\n  ", Trace.TakeLast(12)));
            Respawns();
            // The live tick order: AvoidHazardsAsync before resting and hunting; a flee tick ends the tick.
            if (FleeIfThreatened()) { lookNow = true; continue; }
            if (EmergencyWing()) { lookNow = true; continue; } // TryEmergencyFlyWingAsync, after AvoidHazardsAsync
            if (lookNow)
            {
                lookNow = false;
                if (RestIfDue()) { lookNow = true; continue; } // TickRestAsync runs before the target pick
                if (LootTick()) { lookNow = true; continue; } // TickLootAsync: drops before a new target
                var visible = Visible();
                _inView.Clear();
                foreach (var m in visible) _inView.Add(m.Id);
                if (visible.Count == 0) _restToWin = false; // live PickTarget over nothing finds nothing winnable rested
                var pick = visible.Count > 0 ? Pick(visible) : null;
                if (pick is { } p)
                {
                    var (mob, path) = p;
                    Log($"target {Name(mob)} #{mob.Id} at ({mob.X},{mob.Y}), {path.Count - 1} steps (of {visible.Count} in view)", "pick", mob.Id, _pickDetail);
                    // The target keeps moving until hit (its own AI: wandering, or coming for the bot): follow it, re-pathing
                    // each time it moves, as the server's attack move does.
                    var stop = path.Count > 1 ? Walk(path, WalkKind.Approach, stopOnSighting: false, chase: mob) : WalkStop.Arrived;
                    for (var repaths = 0; stop is not (WalkStop.Threat or WalkStop.Died) && mob.Alive && _t < _end
                                          && !CanHit(_x, _y, mob) && repaths < MaxChaseRepaths; repaths++)
                    {
                        var again = _walk.FindPath(_x, _y, mob.X, mob.Y, ReachExpansions);
                        if (again == null) break;
                        stop = Walk(again.Take(again.FindIndex(c => CanHit(c.x, c.y, mob)) + 1).ToList(), WalkKind.Approach, stopOnSighting: false, chase: mob);
                    }
                    if (_t >= _end) break;
                    if (stop is WalkStop.Threat or WalkStop.Died || !mob.Alive) { _wanderDest = null; lookNow = true; continue; }
                    if (!CanHit(_x, _y, mob))
                    {
                        Log($"lost {Name(mob)} #{mob.Id}: it kept wandering out of reach", "abandon", mob.Id);
                        _wanderDest = null;
                        lookNow = true;
                        continue;
                    }
                    if (!CanHit(_x, _y, mob)) throw new InvalidOperationException("fight started out of range");
                    var searched = _t - _lastFightEnd;
                    if (Fight(mob)) SearchPerKill.Add(searched);
                    _lastFightEnd = _t;
                    _wanderDest = null; // WanderAsync picks afresh after a fight (its target expired meanwhile)
                    lookNow = true;
                    continue;
                }
                if (visible.Count > 0) Log($"no target among {visible.Count} in view", "nopick", detail: _pickDetail);
                if (_restToWin && RestIfDue()) { lookNow = true; continue; }
            }
            if (_wanderDest is not { } dest || Dist(_x, _y, dest.X, dest.Y) <= 3)
            {
                var attempts = new List<string>();
                _wanderDest = WanderDestination(attempts);
                if (_wanderDest == null)
                {
                    // Wedged (WanderAsync's "no reachable destination in 6 attempts"): it retries on its 0.8 s throttle.
                    if (IdleSeconds == 0 || !Trace[^1].Contains("wedged")) Log("wedged: no reachable wander destination in 6 attempts", "idle", detail: new { attempts });
                    Wait(0.8, flee: false);
                    lookNow = true;
                    continue;
                }
                WanderDestinations++;
                Log($"wander to ({_wanderDest.Value.X},{_wanderDest.Value.Y})", "wander", detail: new
                {
                    why = _inView.Count == 0 ? "no monster in view" : "no target among the monsters in view",
                    rule = "WanderAsync: a random walkable cell at least a third of the map away (the 6th draw may be near), not by a portal, with a path",
                    attempts,
                });
            }
            var route = _walk.FindPath(_x, _y, _wanderDest.Value.X, _wanderDest.Value.Y, 0, PathCost()); // as WanderAsync: around hazards
            if (route == null) { _wanderDest = null; continue; }
            Walk(route, WalkKind.Wander, stopOnSighting: true);
            lookNow = true; // stopped for a sighting, or arrived: look either way
        }
        var accounted = WanderSeconds + ApproachSeconds + FightSeconds + IdleSeconds + PostKillSeconds + RestSeconds + FleeSeconds;
        if (Math.Abs(accounted - _t) > 1e-6)
            throw new InvalidOperationException($"clock {_t:F6}s but walking + fighting + idle + resting + fleeing = {accounted:F6}s");
        Log($"end: {Kills} kills, {Exp} exp", "end");
    }

    // ---- crossing (HuntSimOptions.CrossTo): the live travel tick ----

    /// <summary>Crossing mode reached its exit cell.</summary>
    public bool Arrived { get; private set; }
    public double Elapsed => _t;
    public int Hp => _world.Hp;
    public int Sp => _world.Sp;
    public int MaxHp => _world.MaxHp;
    public IReadOnlyDictionary<int, int> Bag => _bag;
    public string? Killer { get; private set; }
    private const int CrossArriveCells = 2; // a portal's footprint warps on any cell of it; the NPC of a Kafra is clicked from 2 cells

    /// <summary>The live travel tick (BotBehavior.Tick while off the hunting map) toward one exit cell: AvoidHazardsAsync,
    /// TryEmergencyFlyWingAsync, DefendAsync (turn on what's chasing when the pack is too costly to drag), TickRestAsync,
    /// then TravelAsync's walk. Ends on arrival, death or the duration cap (stuck).</summary>
    private void RunCross((int X, int Y) exit)
    {
        if (!_walk.TryFindWalkableNear(exit.X, exit.Y, 6, out var gx, out var gy)) (gx, gy) = exit;
        Log($"cross {_o.Map}: from ({_x},{_y}) to the exit at ({gx},{gy})", "start");
        while (_t < _end)
        {
            OnProgress?.Invoke(_t / _end);
            if (Dist(_x, _y, gx, gy) <= CrossArriveCells) { Arrived = true; _end = _t; break; } // the replay ends at the portal, not at the 5 min cap
            Respawns();
            if (FleeIfThreatened()) continue;
            if (EmergencyWing()) continue;
            if (Defender() is { } m)
            {
                var stop = WalkStop.Arrived;
                for (var repaths = 0; stop is not (WalkStop.Threat or WalkStop.Died) && m.Alive && _t < _end && !CanHit(_x, _y, m) && repaths < MaxChaseRepaths; repaths++)
                {
                    var path = _walk.FindPath(_x, _y, m.X, m.Y, ReachExpansions);
                    if (path == null) break;
                    stop = Walk(path.Take(path.FindIndex(c => CanHit(c.x, c.y, m)) + 1).ToList(), WalkKind.Approach, stopOnSighting: false, chase: m);
                }
                if (m.Alive && _t < _end && stop is not (WalkStop.Threat or WalkStop.Died) && CanHit(_x, _y, m)) Fight(m, "defend");
                else Wait(LiveTickSeconds, flee: false); // couldn't close in: the next tick decides again
                continue;
            }
            if (RestIfDue()) continue;
            var route = _walk.FindPath(_x, _y, gx, gy, 0, PathCost()); // WalkPathTowardAsync: A* around hazard zones
            if (route is not { Count: > 1 }) { Log($"no path to the exit at ({gx},{gy})", "idle"); Wait(0.8, flee: false); continue; }
            Walk(route, WalkKind.Wander, stopOnSighting: false, maxSeconds: 10); // a hit (DefendAsync's turn) or a hazard stops it sooner
        }
        Log(Arrived ? $"arrived at the exit after {_t:F0}s, HP {_world.Hp}/{_world.MaxHp}" : Deaths > 0 ? $"died after {_t:F0}s" : $"stuck: no arrival in {_end:F0}s", "end");
    }

    /// <summary>BotBehavior.DefendAsync: the winnable monsters on the bot (hit it, or aggressive within 2 cells; fixed ones
    /// don't follow), within DefendRadius. None when dragging the pack costs under DefendDangerShare of the HP; else the
    /// quickest winnable kill. ponytail: IsHuntable is the forecast win only (no ignore/force lists).</summary>
    private Mob? Defender()
    {
        var chasers = Visible().Where(m => ForecastOf(m).CanWin && Dist(_x, _y, m.X, m.Y) <= BotBehavior.DefendRadius && !HuntPolicy.IsImmobile(_data.Monster(m.Db.Id))
            && (_hitMe.Contains(m.ServerId) || HuntPolicy.IsAggressive(_data.Monster(m.Db.Id)) && Dist(_x, _y, m.X, m.Y) <= 2)).ToList();
        if (chasers.Count == 0) return null;
        var pack = GroupForecast(chasers.Select(m => { var (hp, maxHp) = _world.MonsterHp(m.ServerId); return new BotBehavior.MonsterInfo(m.ServerId, m.Db.Id, m.Db.Name, new Position(m.X, m.Y), hp, maxHp); }).ToList());
        if (pack.CanWin && pack.Damage < _world.Hp * BotBehavior.DefendDangerShare) return null;
        return chasers.MinBy(m => ForecastOf(m).Seconds);
    }

    // ---- report ----

    /// <summary>SHA-256 of the full event trace: equal for equal inputs and seed.</summary>
    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", Trace))))[..16];

    public string Report()
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("output: report");
        var sb = new StringBuilder();
        void L(string s = "") => sb.AppendLine(s);
        var hours = Math.Max(1e-9, _end / 3600);
        var b = _o.Build;
        L($"== Offline hunt: {_o.Map}, {_o.DurationSeconds / 60:F0} min virtual, seed {_o.Seed}" + (Deaths > 0 ? $"; ENDED BY DEATH at {_end / 60:F1} min" : "") + " ==");
        L($"Build: Lv {b.Level}/{b.JobLevel} job {b.JobId}, STR {b.Str} AGI {b.Agi} VIT {b.Vit} INT {b.Int} DEX {b.Dex} LUK {b.Luk}; "
          + $"gear {string.Join(", ", b.Equipment.Select(e => _data.ItemName(e.ItemId)))}; MaxHP {_sheet?.MaxHp}, attack delay {_sheet?.AttackDelay:F2}s");
        L($"Bag at start: {(b.Inventory is { Count: > 0 } inv ? string.Join(", ", inv.Select(i => $"{_data.ItemName(i.Key)} x{i.Value}")) : "empty (or a build recorded before inventories were)")}");
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
        var huntHours = Math.Max(1e-9, (_end - RestSeconds) / 3600);
        L($"Kills/h {Kills / hours:F0}, EXP/h {Exp / hours:F0} sustained; {Exp / huntHours:F0} EXP/h over the {_end - RestSeconds:F0}s not spent sitting");
        double Pct(double s) => 100 * s / _end;
        L($"Time: wandering without a target {WanderSeconds:F0}s ({Pct(WanderSeconds):F1}%), walking to targets {ApproachSeconds:F0}s ({Pct(ApproachSeconds):F1}%), "
          + $"fighting {FightSeconds:F0}s ({Pct(FightSeconds):F1}%), resting {RestSeconds:F0}s ({Pct(RestSeconds):F1}%), fleeing {FleeSeconds:F0}s ({Pct(FleeSeconds):F1}%), wedged/idle {IdleSeconds:F0}s ({Pct(IdleSeconds):F1}%), after kills (tick, move lock, loot) {PostKillSeconds:F0}s ({Pct(PostKillSeconds):F1}%)");
        L($"Threats: monster AIs targeted the bot {Aggros} times; {Flees} flees, {FightsLeft} fights broken off to flee, {WalkInterruptions} walks interrupted (hazard or hit), "
          + $"{Deaths} deaths; at most {_peakActive} monsters under their server AI at once");
        L($"Search (wander + walk-up) per kill: {Summary(SearchPerKill)}");
        L($"  histogram: {Histogram(SearchPerKill, new[] { 2.0, 5, 10, 20, 40, 80 })}");
        L($"Fight per kill: {(Kills > 0 ? FightSeconds / Math.Max(1, Kills + Deaths) : 0):F2}s mean");
        L($"Respawn: {NoHuntableAliveSeconds:F0}s of searching with no huntable monster alive anywhere on the map (waiting on respawns)");
        L($"Recovery: {DamageTaken:F0} HP lost in fights; sat {Rests} times ({Rests - RestsToWin} under {_cfg.RestBelowPercent:P0} HP"
          + $"{(HuntPolicy.KillsWithSpells(_cfg, b.JobId) ? "/SP" : "")}, {RestsToWin} to win), {RestSeconds:F0}s sitting{(SitRefusals > 0 ? $", {SitRefusals} sits refused by the server" : "")}; "
          + $"the server's regen gave +{_world.RegenHp} HP, +{_world.RegenSp} SP over the run");
        if (!_cfg.RestWhenNoPotions)
            L("!! RestWhenNoPotions is off: the live bot sits only to win a fight in view and otherwise relies on potions");
        L($"Items used: {(_used.Count > 0 ? string.Join(", ", _used.Select(u => $"{_data.ItemName(u.Key)} x{u.Value}")) : "none")}"
          + $"{(_itemRules.Count > 0 ? $"; item script: {_itemRules.Count} rule(s)" : "")}{(Teleports > 0 ? $"; {Teleports} Fly Wing teleport(s)" : "")}; "
          + $"{FightsEndedUnderHealHp} fights ended under HealHpPercent {_cfg.HealHpPercent:P0}");
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
        L("-- Approximated or not modelled (stated, not hidden) --");
        foreach (var c in Capabilities().Where(c => c.Level is "approximated" or "absent"))
            L($"* {c.Area} ({c.Level}): {c.What} {c.Implication}");
        L($"Trace fingerprint {Fingerprint()} ({Trace.Count} events)");
        return sb.ToString();
    }

    /// <summary>What this simulator runs with the server's code, with the live bot's own decision code, approximately,
    /// or not at all, with what each means for the numbers; plus what this run's inputs make of it. The report's
    /// "approximated or not modelled" list and the dashboard read this one list.</summary>
    public List<ReplayCapability> Capabilities()
    {
        var c = new List<ReplayCapability>();
        void Add(string area, string level, string what, string implication) => c.Add(new ReplayCapability(area, level, what, implication));
        Add("Combat", "server", "Auto-attacks, skills, damage, hit/miss, statuses, cast bars and after-cast delays run the game server's own code (skill packet -> PacketSkill -> SkillHandler, CombatEntity).",
            "Damage, kill times and skill effects are the server's numbers.");
        Add("Recovery", "server", "HP/SP regen is the server's Player.UpdateRegenTick (sitting, standing and walking rates); sitting goes through the server's sit/stand packet handler.",
            "Resting time and what it recovers are the server's.");
        Add("Ground effects", "server", "World events (ground effects) run on 10 ms server frames: creation, timers, area ticks, expiry and removal are the server's; their packets to clients are dropped.",
            "Lingering area damage and its kills are real; nothing is drawn client-side.");
        Add("Knockback", "server", "Knockback moves monsters over the server's walk data.", "A knocked monster stays where it lands: it never walks back.");
        Add("Map", "server", "The server's walk data (checked equal to the client's grid) and the database's spawn counts, pinned spawn areas and respawn delays.", "Geometry and population are the real map's.");
        Add("Target choice", "shared", "The live bot's HuntPolicy.PickTarget: hunt filters, portal buffer, win rule at the current HP, exp/s score with walk and rest, detour rule.",
            "Picks and rejections are what the live rules decide on this world's inputs.");
        Add("Skill choice", "shared", "The live rule engine (BotBehavior.ChooseSkillRule) on the bot's skill script, with its 0.25 s global throttle and per-rule cooldown.", "Why a skill fired or was skipped is the live engine's reason.");
        Add("Resting", "shared", "The live sit/stand rules (HuntPolicy.ShouldSit/ShouldStand), checked every 0.25 s while sitting.", "When the bot sits and stands is the live decision.");
        Add("Spawning", "approximated", "A (re)spawn lands on a uniformly random walkable cell of its area; the server's 9-slice spawn-zone formula is not reproduced.", "Monster density across a big area can differ a little from live.");
        Add("Walking", "approximated", "A* on the walk grid at the base 0.15 s per cell (diagonal x1.414); the bot moves cell to cell, time jumps per step. Inside a fight the player walks into range in a straight line.",
            "No server path smoothing or move lag; a target behind a wall mid-fight is abandoned rather than walked around.");
        Add("Re-targeting", "approximated", "Targets are re-picked when a monster enters view, when a monster hits the bot off-target, after a flee or a fight, not every 0.25 s tick as live; once picked, a target is walked to and fought. Threats are checked every step (0.15 s) and every fight action rather than every 0.25 s tick.",
            "The live bot can switch to a better target mid-walk; this one cannot.");
        Add("Skill timing", "approximated", "Skill rules are evaluated at action slots in a fight, not while walking up to the target as the live tick also does.", "Opening buffs cast on the approach live happen at the first swing here.");
        Add("Target forecast", "approximated", "The pick's forecast is a server-code duel from full HP, judged against the current HP; the live server-forecast mode re-runs it from the current HP. No potion buffer.",
            "Borderline targets at low HP can be judged slightly differently than live.");
        Add("Rest estimate in the score", "approximated", "The target score's rest-time term is HuntPolicy.RestSeconds (live's formula, without recovery skills), not the server regen.", "Scores of damaging targets carry live's rest estimate.");
        Add("Regen timing", "approximated", "The server regen runs in 0.25 s slices of virtual time.", "A regen tick can land up to 0.25 s late.");
        Add("Monster AI", "server", "Every monster within the server's view distance (21) of the bot runs the game server's own AI (WorldObject.Update -> Monster.Update, MonsterAI.csv, its skill handler) on 10 ms server frames: target search, chasing, giving up, attacking, assisting its kind, retaliating, monster skills, random moves.",
            $"Aggression, chases, packs and monster skills are the server's. This run: {Aggros} times a monster's AI targeted the bot.");
        Add("Distant monsters", "approximated", "Out of view, a monster's idle wander is a re-implementation of the server's (wait 4-6 s, a random cell within 9, at most 20 steps, locked spawn areas kept); handed to its server AI (cell, idle timer, the move under way) on entering view, and back only once out of view with its AI at rest.",
            "Nothing out of the bot's view can notice it, which the server agrees with (it wakes a monster's AI only for players in view). The live server keeps an awake monster's AI running out of view; here it falls back to the copy.");
        Add("Monster fights", "server", "Every monster whose AI targets the bot swings or casts on its own cooldown; damage lands through the server's damage queue, attributed to its attacker.", "Several attackers at once, and the ones that aren't the target, hit as on the server.");
        Add("Threat reactions", "shared", "The live hazard rules (HuntPolicy: HazardCandidate/IsHazard, HazardRadius, ApproachNearHazard, EngagementGroup, Threatened, ChooseFlee, PathCost) on this world's server-code forecasts, checked every step and every fight action: flee, refuse targets near hazards or pulling a losing pack, refuse to sit or stand up when threatened, fight back when cornered, avoid a killer's class.",
            $"This run: {Flees} flees ({FleeTicks} flee ticks, {FleeSeconds:F0}s), {FightsLeft} fights broken off, {WalkInterruptions} walks interrupted.");
        Add("Threat forecasts", "approximated", "Hazards use the server-code duel at the current HP (the live server-forecast engine, not its hand-formula 'old' one); a pack is its members' duels killed quickest first while the rest keep hitting, not BattleSimulator.ForecastGroup's hand formula. KillsMeFast (the hand-formula hit) isn't ported: a Fly Wing flee needs Ambushed (HP falling).",
            "Borderline hazards and packs can be judged slightly differently than live; a live bot can wing out of a hazard before its first hit lands, this one waits to be hurt.");
        Add("Items", "approximated", "Kills roll the server's drop table onto the ground around the corpse; with AutoLoot the bot walks to each (nearest first, within LootRange, one pickup per LootCooldownSeconds) and picks it up (see the Inventory tab). No zeny or weight; picked-up gemstones and arrows don't feed skills.", $"Skills needing ammo or catalysts are refused by the server. This run picked up {_pickedUp} items; the bag started with the build's {_o.Build.Inventory?.Values.Sum() ?? 0}.");
        Add("Potions", "server", "Healing and buff items are chosen by the live rules (the item script, MaybeHealAsync, MaybeDrinkBuffAsync, shared through HuntPolicy) at action slots in a fight and run the server's item scripts (PacketUseInventoryItem); the target forecast counts the potions left (PotionHpBuffer). Not between fights, where live also drinks if a monster hits it on the walk.",
            $"Used {_used.Values.Sum()}: {(_used.Count > 0 ? string.Join(", ", _used.Select(u => $"{_data.ItemName(u.Key)} x{u.Value}")) : "none")}.");
        Add("Wings", "server", $"Fly Wings go through the server (CombatEntity.RandomTeleport) from the item script, the hazard flee (HuntPolicy.ChooseFlee) and the emergency wing (under 25% HP and Ambushed), sharing live's 3 s escape throttle. {Teleports} used this run. Butterfly Wings and the stuck/confined wings are not simulated.", "Stuck wings never fire here (the sim doesn't get stuck).");
        Add("Other players", "absent", "No party (ally rules never fire), no other players competing for monsters.", "Kill-stealing and party support don't exist here.");
        Add("Death", "server", "A death (in a fight, walking or sitting) ends the run there; the rates are over the time lived.", "A map that kills the build early scores what it earned before dying, over that shorter time.");
        Add("Progression", "absent", "The build is fixed for the whole run: no level-ups, stat or skill points.", "A long run keeps the recorded build's stats, HP and kill speed throughout.");
        // What this run's inputs make of it.
        if (_o.Config == null) Add("Config", "approximated", "No saved config for this bot: the in-code BotBehaviorConfig defaults ran.", "Rest, win and hunt rules may differ from the live bot's.");
        if (!_cfg.RestWhenNoPotions) Add("Config", "absent", "RestWhenNoPotions is off: the live bot sits only to win a fight in view and otherwise relies on potions, which don't exist here.", "HP only comes back by standing regen.");
        var unknown = _mobs.Where(m => !DuelSimulator.IsKnownMonster(m.Db.Id)).Select(m => m.Db.Name).Distinct().ToList();
        if (unknown.Count > 0) Add("Monsters", "absent", $"Unknown to the server data, never targeted: {string.Join(", ", unknown)}.", "They are drawn but can't be fought.");
        var generic = _rules.Select(r => r.Skill).Distinct().Where(ServerWorld.IsGenericHandler).ToList();
        if (generic.Count > 0) Add("Skills", "absent", $"No server handler for {string.Join(", ", generic)}: the server runs its generic cast (cooldown and packet only).", "These casts do nothing but cost time.");
        if (_rules.Any(r => r.Target == BotBehavior.SkillCastTarget.Ally)) Add("Skills", "absent", "Ally rules in the script never fire (no party).", "Support casts are missing.");
        return c;
    }

    /// <summary>The finished run as a HuntReplay (call after Run): what was recorded while it ran, the report's numbers and
    /// the capability list. &lt;paramref name="buildSource"/&gt;: where the build came from, for the viewer.</summary>
    public HuntReplay Replay(string? buildSource = null)
    {
        using var _profile = RoBotClient.Simulation.SimProfile.Of("output: replay build");
        var walk = new StringBuilder(_walk.Width * _walk.Height);
        for (var y = 0; y < _walk.Height; y++)
            for (var x = 0; x < _walk.Width; x++)
                walk.Append(_walk.IsWalkable(x, y) ? '1' : '0');
        var b = _o.Build;
        var hours = Math.Max(1e-9, _end / 3600);
        var skills = _world.Skills.Keys.Union(RuleFired.Keys).Union(_rules.Select(r => r.Skill)).Distinct().OrderBy(s => s.ToString()).Select(s =>
        {
            var l = _world.Skills.GetValueOrDefault(s) ?? new SkillLedger();
            return new ReplaySkill(s.ToString(), b.LearnedSkills.GetValueOrDefault(s), RuleFired.GetValueOrDefault(s), l.Requested, l.CastBars, l.Queued, l.Executed,
                l.SpSpent, l.Damage, l.Hits, l.KillingBlows, l.Healed, new(l.Rejected), new(l.StatusesApplied), l.Failures, l.Blocked, ServerWorld.IsGenericHandler(s));
        }).ToList();
        var types = Types.OrderByDescending(t => t.Value.Killed).ThenByDescending(t => t.Value.Population).Select(t =>
        {
            var f = ForecastOf(_mobs.First(m => m.Db.Name == t.Key));
            return new ReplayType(t.Key, t.Value.Population, t.Value.Seen, t.Value.Picked, t.Value.Killed, t.Value.Exp,
                t.Value.Killed > 0 ? t.Value.FightSeconds / t.Value.Killed : 0, ForecastText(f), new(t.Value.Rejected));
        }).ToList();
        var summary = new ReplaySummary(Kills, Deaths, Exp, Exp / hours, Exp / Math.Max(1e-9, (_end - RestSeconds) / 3600), Kills / hours,
            WanderSeconds, ApproachSeconds, FightSeconds, RestSeconds, IdleSeconds, NoHuntableAliveSeconds, DamageTaken, _world.RegenHp, _world.RegenSp,
            Rests, RestsToWin, SitRefusals, FightsEndedUnderHealHp, Steps, PickDecisions, Picks, new(Rejections),
            SearchPerKill.Count > 0 ? SearchPerKill.Average() : 0, Kills > 0 ? FightSeconds / Math.Max(1, Kills + Deaths) : 0,
            skills, new(RuleSkips), types);
        return new HuntReplay
        {
            Map = _o.Map, Width = _walk.Width, Height = _walk.Height, Walk = walk.ToString(), Duration = _end, Seed = _o.Seed,
            BuildSpec = b,
            StartInventory = (b.Inventory ?? new Dictionary<int, int>()).Select(i => new ReplayItem(i.Key, _data.ItemName(i.Key), _data.Item(i.Key)?.Code, i.Value)).ToList(),
            Build = $"Lv {b.Level}/{b.JobLevel} job {b.JobId}, STR {b.Str} AGI {b.Agi} VIT {b.Vit} INT {b.Int} DEX {b.Dex} LUK {b.Luk}; "
                    + $"gear {string.Join(", ", b.Equipment.Select(e => _data.ItemName(e.ItemId)))}",
            BuildSource = buildSource,
            Config = _o.Config != null ? _o.ConfigSource ?? "given" : "NONE SAVED: the in-code BotBehaviorConfig defaults",
            SkillScript = _o.SkillScript ?? "", MaxHp = _world.MaxHp, MaxSp = _world.MaxSp,
            Mobs = _mobs.Select(m => new ReplayMob(m.Id, m.Db.Id, m.Db.Name, m.Db.Level, m.Db.Exp, m.Db.HP, m.Pinned, new[] { m.MinX, m.MinY, m.MaxX, m.MaxY },
                Respawn(m), ForecastText(ForecastOf(m)))).ToList(),
            Track = _track, Vitals = _vitals, MobMoves = _moves,
            Events = _events.OrderBy(e => e.T).ToList(), // stable: back-dated world events (regen caught up at a fight's start) slot in by time
            Summary = summary, Capabilities = Capabilities(), Failures = _world.Failures.ToList(),
            Report = Report(), Fingerprint = Fingerprint(),
        };
    }

    private static string ForecastText(Forecast f) => !f.Known ? "unknown to the server data" : f.CanWin
        ? $"win {f.WinRate:P0}, {f.Seconds:F1}s, -{f.DamageTaken:F0} HP" : $"too strong at full HP ({f.Why})";

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
