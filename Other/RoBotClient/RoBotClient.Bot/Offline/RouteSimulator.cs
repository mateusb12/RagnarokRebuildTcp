using System.Text.Json;
using System.Text.Json.Serialization;
using RoBotClient.Bot.Behavior;
using RoBotClient.Bot.GameData;
using RoBotClient.Simulation;

namespace RoBotClient.Bot.Offline;

/// <summary>What the live bot asks before moving (BotBehavior.RouteCheck): would its current build survive the route?
/// Safe also when the check couldn't run (Text says why): a missing checker never blocks a move.</summary>
public sealed record RouteVerdict(string From, string To, int Level, bool Safe, string? FailsAt, string Text, DateTime AtUtc)
{
    public static RouteVerdict Unchecked(string from, string to, string why) => new(from, to, 0, true, null, why, DateTime.UtcNow);
}

/// <summary>One map of a route: walk from where the bot lands (From) to the portal or Kafra NPC that leaves it (To).</summary>
public sealed record RouteHop(string Map, int FromX, int FromY, int ToX, int ToY, string Leaves);

/// <summary>How one seed's bot did on one hop.</summary>
public sealed record HopOutcome(string Map, bool Arrived, bool Died, double Seconds, int HpLeft, int MaxHp, int FlyWings, int Potions, string? Killer, string? Replay, bool Skipped = false, int SpLeft = 0);

/// <summary>One candidate hunting map of a level scan: a hunt sim on it (exp/h, did it die) and the route there from
/// where the bot stood (null when it is already there).</summary>
public sealed record MapScan(string Map, double ExpPerHour, double Minutes, int Kills, bool Died, string? Killer, RouteVerdict? Route)
{
    /// <summary>The hunt's replay in sim-replays (pruned to the last few scans), or null.</summary>
    public string? Replay { get; init; }
    /// <summary>Seconds per kill in the sim, by activity (0 in scans saved before these were recorded).</summary>
    public double FightPerKill { get; init; }
    public double SearchPerKill { get; init; }
    public double RecoverPerKill { get; init; }
    /// <summary>Per monster name: kills, deaths to it and average HP lost per fight in the sim.</summary>
    public Dictionary<string, MonsterScan>? Monsters { get; init; }
}

public sealed record MonsterScan(int Kills, int Deaths, double HpLossPerFight);

/// <summary>What the offline simulator found for one character at one level: run once per level (RouteSimulator.Scan).</summary>
public sealed record LevelScan(string Character, int Level, string From, List<MapScan> Maps, DateTime AtUtc, double WallSeconds)
{
    /// <summary>Maps the scan's routes got across alive in every seed (RouteResult.Crossed).</summary>
    public List<string> Crossed { get; init; } = new();
    public static readonly JsonSerializerOptions Json = RouteResult.Json;
}

/// <summary>Per map of the route over all seeds: how many got there, died there, got stuck there.</summary>
public sealed record HopSummary(string Map, string Leaves, int Entered, int Arrived, int Died, int Stuck, double MeanSeconds, double MeanHpLeftPct, double MeanFlyWings, List<string> Killers);

/// <summary>"Would this build survive walking from A to B?": the route the live bot plans, each map crossed in a fresh
/// HuntSimulator in crossing mode (the live travel tick: hazards, flee, Fly Wings, potions, defend, rest), HP/SP and the
/// bag carried map to map, over several seeds. A seed's run stops at its first death (or a map it can't cross in
/// HopSeconds).</summary>
public sealed record RouteResult(string Character, int Level, string From, string To, List<RouteHop> Route, int Seeds, int Survived,
    List<HopSummary> Hops, List<List<HopOutcome>> Runs, string Verdict, double WallSeconds)
{
    /// <summary>The first map where at least half the runs that entered it died or got stuck; null when the route holds.</summary>
    public string? FailsAt { get; init; }

    /// <summary>Maps every seed that entered got across alive (skipped ones included): safe from here on, at this level or higher.</summary>
    public List<string> Crossed => Hops.Where(h => h.Entered > 0 && h.Arrived == h.Entered).Select(h => h.Map).ToList();

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
}

public static class RouteSimulator
{
    public const double HopSeconds = 300; // a big dungeon is ~2 min across; past 5 min it is blocked (fleeing back and forth)

    /// <summary>The live route (WorldGraph.Plan from the bot's cell to the destination's entry cell) as hops. Kafra
    /// teleports are allowed unless noKafra (the live bot only takes the ones it can afford). Null when unreachable.</summary>
    public static List<RouteHop>? Plan(GameDatabase data, string fromMap, int x, int y, string toMap, bool noKafra = false)
    {
        if (data.World.EntryCell(toMap) is not { } entry) return null;
        if (x < 0 && (data.World.EntryCell(fromMap) is { } start)) (x, y) = start; // no cell given: where a portal lands
        var plan = data.World.Plan(fromMap, x, y, toMap, entry.X, entry.Y, edgeFilter: noKafra ? e => e.Kafra == null : null);
        if (plan == null) return null;
        var hops = new List<RouteHop>();
        var (map, fx, fy) = (fromMap, x, y);
        foreach (var e in plan.Edges)
        {
            hops.Add(new RouteHop(map, fx, fy, e.Sx, e.Sy, e.Kafra != null ? $"Kafra to {e.DstMap}" : $"portal to {e.DstMap}"));
            (map, fx, fy) = (e.DstMap, e.Dx, e.Dy);
        }
        return hops;
    }

    /// <param name="replay">called with (seed, hop index, finished simulator) for each crossing; returns a replay name to
    /// record on the hop, or null.</param>
    public static RouteResult Run(GameDatabase data, PlayerBuild build, BotBehaviorConfig? config, string character, string toMap,
        List<RouteHop> route, int seeds, int firstSeed = 1, Func<int, int, HuntSimulator, string?>? replay = null, Action<double>? progress = null,
        IReadOnlySet<string>? safeMaps = null)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var runs = new List<List<HopOutcome>>();
        for (var s = 0; s < seeds; s++)
        {
            var seed = firstSeed + s;
            var run = new List<HopOutcome>();
            var bag = build.Inventory != null ? new Dictionary<int, int>(build.Inventory) : new Dictionary<int, int>();
            int? hp = null, sp = null;
            for (var i = 0; i < route.Count; i++)
            {
                var hop = route[i];
                // Nothing there attacks first, or this character already got across it at this level or lower: safe, not simulated.
                if ((Passive(data, hop.Map) || safeMaps?.Contains(hop.Map) == true) && replay == null)
                {
                    run.Add(new HopOutcome(hop.Map, true, false, 0, hp ?? 0, 0, 0, 0, null, null, Skipped: true));
                    continue;
                }
                var options = new HuntSimOptions(hop.Map, build with { Inventory = bag }, HopSeconds, seed * 1009 + i, SkillScript: config?.SkillScript ?? "",
                    NoAutoAttack: config?.NoAutoAttack ?? false, Config: config, StartAt: (hop.FromX, hop.FromY), CrossTo: (hop.ToX, hop.ToY), StartHp: hp, StartSp: sp);
                var sim = new HuntSimulator(options, data);
                sim.Run();
                int Used(int id) => bag.GetValueOrDefault(id) - sim.Bag.GetValueOrDefault(id);
                var wings = Used(config?.FlyWingItemId ?? 601);
                var potions = bag.Keys.Where(id => id != (config?.FlyWingItemId ?? 601)).Sum(Used);
                var died = sim.Deaths > 0;
                run.Add(new HopOutcome(hop.Map, sim.Arrived, died, sim.Elapsed, died ? 0 : sim.Hp, sim.MaxHp, wings, potions, sim.Killer, replay?.Invoke(seed, i, sim), SpLeft: died ? 0 : sim.Sp));
                progress?.Invoke((s + (i + 1.0) / route.Count) / seeds);
                if (!sim.Arrived) break;
                bag = new Dictionary<int, int>(sim.Bag);
                (hp, sp) = (sim.Hp, sim.Sp);
            }
            runs.Add(run);
        }
        var hops = route.Select((hop, i) =>
        {
            var at = runs.Where(r => r.Count > i).Select(r => r[i]).ToList();
            return new HopSummary(hop.Map, hop.Leaves, at.Count, at.Count(o => o.Arrived), at.Count(o => o.Died), at.Count(o => !o.Arrived && !o.Died),
                at.Count > 0 ? at.Average(o => o.Seconds) : 0, at.Where(o => o.Arrived && !o.Skipped).Select(o => 100.0 * o.HpLeft / Math.Max(1, o.MaxHp)).DefaultIfEmpty(0).Average(),
                at.Count > 0 ? at.Average(o => o.FlyWings) : 0, at.Where(o => o.Killer != null).Select(o => o.Killer!).Distinct().ToList());
        }).ToList();
        var survived = runs.Count(r => r.Count == route.Count && r.All(o => o.Arrived));
        var fails = hops.FirstOrDefault(h => h.Entered > 0 && h.Died + h.Stuck >= Math.Max(1, h.Entered / 2.0));
        var from = route.Count > 0 ? route[0].Map : toMap;
        var verdict = route.Count == 0 ? $"already on {toMap}"
            : fails == null ? $"survives {from} -> {toMap} in {survived}/{seeds} runs"
            : $"would not reach {toMap}: fails on {fails.Map} in {fails.Died + fails.Stuck}/{fails.Entered} runs ("
              + string.Join(", ", new[] { fails.Died > 0 ? $"died {fails.Died}" + (fails.Killers.Count > 0 ? $", to {string.Join("/", fails.Killers)}" : "") : null,
                  fails.Stuck > 0 ? $"blocked {fails.Stuck}: not across in {HopSeconds / 60:F0} min" : null }.Where(s => s != null)) + ")";
        return new RouteResult(character, build.Level, from, toMap, route, seeds, survived, hops, runs, verdict, started.Elapsed.TotalSeconds) { FailsAt = fails?.Map };
    }

    /// <summary>No regular (non-boss) spawn on the map attacks first (HuntPolicy.IsAggressive).</summary>
    public static bool Passive(GameDatabase data, string map) => !data.MonstersById.Values.Any(m =>
        HuntPolicy.IsAggressive(m) && (m.Spawns ?? new()).Any(s => !s.IsBoss && !s.IsMvp && string.Equals(s.Map, map, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The once-per-level scan: each candidate map hunted for <paramref name="minutes"/> (seed 1), exp/h only.
    /// Survival of the way there is asked later, one map at a time, only for the map the bot picks (BotBehavior.DecideMap -> routesim).</summary>
    public static LevelScan Scan(GameDatabase data, PlayerBuild build, BotBehaviorConfig? config, string character, string from, int x, int y,
        IReadOnlyList<string> maps, double minutes, Action<double>? progress = null, Func<string, HuntSimulator, string?>? replay = null)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var scans = new List<MapScan>();
        for (var i = 0; i < maps.Count; i++)
        {
            var map = maps[i];
            var sim = new HuntSimulator(new HuntSimOptions(map, build, minutes * 60, 1, SkillScript: config?.SkillScript ?? "",
                NoAutoAttack: config?.NoAutoAttack ?? false, Config: config), data);
            sim.Run();
            var died = sim.Deaths > 0;
            scans.Add(new MapScan(map, sim.Exp / Math.Max(1e-9, sim.Elapsed / 3600), sim.Elapsed / 60, sim.Kills, died, sim.Killer, null)
            {
                Replay = replay?.Invoke(map, sim),
                FightPerKill = sim.Kills > 0 ? sim.FightSeconds / sim.Kills : 0,
                SearchPerKill = sim.Kills > 0 ? (sim.WanderSeconds + sim.ApproachSeconds) / sim.Kills : 0,
                RecoverPerKill = sim.Kills > 0 ? sim.RestSeconds / sim.Kills : 0,
                Monsters = sim.Types.Where(t => t.Value.Picked > 0 || t.Value.Deaths > 0)
                    .ToDictionary(t => t.Key, t => new MonsterScan(t.Value.Killed, t.Value.Deaths, t.Value.Picked > 0 ? t.Value.DamageTaken / t.Value.Picked : 0)),
            });
            progress?.Invoke((i + 1.0) / maps.Count);
        }
        return new LevelScan(character, build.Level, from, scans, DateTime.UtcNow, started.Elapsed.TotalSeconds);
    }

    public static string Report(LevelScan s)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"== Level scan: {s.Character} Lv {s.Level} from {s.From}, {s.Maps.Count} maps, {s.WallSeconds:F1}s wall ==");
        foreach (var m in s.Maps)
            sb.AppendLine($"{m.Map,-14} {m.ExpPerHour,10:N0} exp/h  {m.Kills,4} kills in {m.Minutes:F1} min{(m.Died ? $"  DIED to {m.Killer}" : "")}{(m.Route != null ? $"  route: {m.Route.Text}" : "")}");
        return sb.ToString();
    }

    public static string Report(RouteResult r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"== Route: {r.Character} Lv {r.Level}, {r.From} -> {r.To}, {r.Seeds} seeds, {r.WallSeconds:F1}s wall ==");
        sb.AppendLine(r.Verdict);
        sb.AppendLine($"{"map",-14} {"leaves by",-24} {"entered",7} {"crossed",7} {"died",5} {"stuck",5} {"mean s",7} {"HP left",7} {"wings",5}  killers");
        foreach (var h in r.Hops)
            sb.AppendLine($"{h.Map,-14} {h.Leaves,-24} {h.Entered,7} {h.Arrived,7} {h.Died,5} {h.Stuck,5} {h.MeanSeconds,7:F0} {h.MeanHpLeftPct,6:F0}% {h.MeanFlyWings,5:F1}  {string.Join(", ", h.Killers)}");
        return sb.ToString();
    }
}
