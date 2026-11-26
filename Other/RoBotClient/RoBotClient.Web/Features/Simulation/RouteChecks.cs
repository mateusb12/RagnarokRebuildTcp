using System.Text.Json;
using RoBotClient.Bot.Offline;
using RoBotClient.Bot.Session;
using RoBotClient.Simulation;

namespace RoBotClient.Web.Features.Simulation;

/// <summary>
/// "Would this build survive the way to that map?": runs the CLI's routesim (RouteSimulator) in a child process, one
/// check at a time in the background, for the Simulation page and for live bots before an AutoMap move
/// (BotManager.RouteCheck). A live bot's check uses its build right now (inventory included) and its saved config; its
/// verdict is kept per character, level and route for VerdictTtl. Results are saved as route-checks/*.json.
/// </summary>
public sealed class RouteChecks
{
    /// <summary>A route check, or with Maps a level scan (To then only describes it).</summary>
    public sealed record Job(string Character, string Who, string? ConfigOf, string From, int X, int Y, string To, int Seeds, bool Live, int Level, DateTime QueuedUtc,
        IReadOnlyList<string>? Maps = null)
    {
        public bool IsScan => Maps != null;
        public string Key => IsScan ? ScanKey(Character, Level) : RouteChecks.Key(Character, Level, From, To);
    }

    /// <summary>A level scan is redone after this even without a level-up: a slow leveler (Bot4 at Lv 45) otherwise kept a
    /// day-old scan from another map and older gear. The rescan overwrites the level's file.</summary>
    // ponytail: a property, not a static readonly field: hot reload adds new fields without running their initializer (it was 0).
    public static TimeSpan ScanTtl => TimeSpan.FromHours(2);

    public const int LiveSeeds = 5;
    public static readonly TimeSpan VerdictTtl = TimeSpan.FromHours(2);
    public string Directory { get; }

    private readonly SimRunner _runner;
    private readonly object _lock = new();
    private readonly Queue<Job> _queue = new();
    private readonly Dictionary<string, RouteVerdict> _verdicts = new();
    private readonly Dictionary<string, LevelScan> _scans = new();
    private readonly Dictionary<string, Dictionary<string, int>> _survived = new(); // character -> map -> lowest level it got across alive
    private Task? _worker;
    public Job? Running { get; private set; }
    public int Percent { get; private set; } = -1;
    public string? LastError { get; private set; }
    public event Action? Changed;

    public RouteChecks(SimRunner runner, string repoRoBot)
    {
        _runner = runner;
        Directory = Path.Combine(repoRoBot, "route-checks");
        // ponytail: one-time move of check replays saved next to the leveling runs before they had their own folder.
        if (System.IO.Directory.Exists(runner.Directory))
            foreach (var f in ReplayPatterns.SelectMany(p => new DirectoryInfo(runner.Directory).GetFiles(p)))
            {
                System.IO.Directory.CreateDirectory(runner.ChecksDirectory);
                try { f.MoveTo(Path.Combine(runner.ChecksDirectory, f.Name), overwrite: true); } catch (IOException) { }
            }
    }

    /// <summary>The replays a check's CLI run writes ("{stamp}_route_...", "{stamp}_scan_...": one stamp per check).</summary>
    private static readonly string[] ReplayPatterns = ["*_route_*.json", "*_scan_*.json"];
    private static string Key(string character, int level, string from, string to) => $"{character}|{level}|{from}|{to}".ToLowerInvariant();
    private static string ScanKey(string character, int level) => $"{character}|{level}".ToLowerInvariant();
    private string ScanFile(string character, int level) => Path.Combine(Directory, "scans", $"{SimRunner.SafeName(character)}_L{level}.json");
    private static LevelScan? ReadScan(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<LevelScan>(File.ReadAllText(file), LevelScan.Json) : null; }
        catch (Exception) { return null; } // unreadable: as if never scanned
    }

    /// <summary>Maps this character got across alive at <paramref name="level"/> or lower, for --safe-maps: a crossing
    /// survived once stays survived as it levels (kept forever, route-checks/survived).</summary>
    private string? SafeMaps(string character, int level)
    {
        var maps = Survived(character).Where(kv => kv.Value <= level).Select(kv => kv.Key).ToList();
        return maps.Count > 0 ? string.Join(",", maps) : null;
    }

    private Dictionary<string, int> Survived(string character)
    {
        var key = character.ToLowerInvariant();
        if (_survived.TryGetValue(key, out var known)) return known;
        var file = Path.Combine(Directory, "survived", $"{SimRunner.SafeName(character)}.json");
        try { known = File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(file)) : null; }
        catch (Exception) { known = null; }
        return _survived[key] = new Dictionary<string, int>(known ?? new(), StringComparer.OrdinalIgnoreCase);
    }

    private void RecordCrossed(string character, int level, IEnumerable<string> maps)
    {
        lock (_lock)
        {
            var known = Survived(character);
            var changed = false;
            foreach (var map in maps)
                if (!known.TryGetValue(map, out var at) || level < at) { known[map] = level; changed = true; }
            if (!changed) return;
            System.IO.Directory.CreateDirectory(Path.Combine(Directory, "survived"));
            File.WriteAllText(Path.Combine(Directory, "survived", $"{SimRunner.SafeName(character)}.json"), JsonSerializer.Serialize(known, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public List<Job> Queued { get { lock (_lock) return _queue.ToList(); } }
    public List<RouteVerdict> LiveVerdicts { get { lock (_lock) return _verdicts.Values.OrderByDescending(v => v.AtUtc).ToList(); } }
    public List<LevelScan> LiveScans { get { lock (_lock) return _scans.Values.Where(s => s.Maps.Count > 0).OrderByDescending(s => s.AtUtc).ToList(); } }

    /// <summary>A live bot's once-per-level scan (BotManager.LevelScan): hunt sims of the candidate maps (no routes: the
    /// move gate's Check asks those one at a time, best map first). Null while it is queued or running; a failed scan comes back with no maps (the bot then uses its estimates).
    /// Kept for the level up to ScanTtl, on disk too (route-checks/scans), so a dashboard restart doesn't run it again.</summary>
    public LevelScan? Scan(string character, BotSession session, string from, int x, int y, IReadOnlyList<string> maps)
    {
        var (build, _) = session.CaptureCombatState();
        var key = ScanKey(character, build.Level);
        lock (_lock)
        {
            if (_scans.TryGetValue(key, out var scan) && DateTime.UtcNow - scan.AtUtc < ScanTtl) return scan;
            if (ReadScan(ScanFile(character, build.Level)) is { } saved && DateTime.UtcNow - saved.AtUtc < ScanTtl) return _scans[key] = saved;
        }
        EnqueueLive(new Job(character, "", SimRunner.ShortName(character), from, x, y, $"{maps.Count} maps (level scan)", Seeds: 1, Live: true, Level: build.Level,
            QueuedUtc: DateTime.UtcNow, Maps: maps.ToList()), build, "scan");
        return null;
    }

    /// <summary>The bot's scan for this level, as its map choice used it (memory, else route-checks/scans), or null.</summary>
    public LevelScan? ScanOf(string character, int level)
    {
        lock (_lock)
        {
            var key = ScanKey(character, level);
            if (_scans.TryGetValue(key, out var scan)) return scan.Maps.Count > 0 ? scan : null;
            return ReadScan(ScanFile(character, level)) is { } saved ? _scans[key] = saved : null;
        }
    }

    /// <summary>A live bot's question (BotManager.RouteCheck): the fresh verdict, or null while its check is queued or running.</summary>
    public RouteVerdict? Check(string character, BotSession session, string from, int x, int y, string to)
    {
        var (build, _) = session.CaptureCombatState();
        lock (_lock)
            if (_verdicts.TryGetValue(Key(character, build.Level, from, to), out var v) && DateTime.UtcNow - v.AtUtc < VerdictTtl) return v;
        EnqueueLive(new Job(character, "", SimRunner.ShortName(character), from, x, y, to, LiveSeeds, Live: true, Level: build.Level, QueuedUtc: DateTime.UtcNow), build, SimRunner.SafeName(to));
        return null;
    }

    /// <summary>Queues a live bot's job on its build right now (saved as route-checks/builds/{character}_{tag}.json), unless
    /// the same job is already queued or running.</summary>
    private void EnqueueLive(Job job, PlayerBuild build, string tag)
    {
        lock (_lock)
        {
            if (Running?.Key == job.Key || _queue.Any(j => j.Key == job.Key)) return;
            System.IO.Directory.CreateDirectory(Path.Combine(Directory, "builds"));
            var file = Path.Combine(Directory, "builds", $"{SimRunner.SafeName(job.Character)}_{tag}.json");
            File.WriteAllText(file, JsonSerializer.Serialize(build, DuelStore.JsonOptions));
            _queue.Enqueue(job with { Who = file });
            Kick();
        }
        Changed?.Invoke();
    }

    /// <summary>A check from the Simulation page: the bot's latest recorded build (bot-duels), with replays of each map.</summary>
    public void Request(string bot, string from, int x, int y, string to, int seeds)
    {
        lock (_lock) { _queue.Enqueue(new Job(bot, bot, null, from, x, y, to, Math.Clamp(seeds, 1, 50), false, 0, DateTime.UtcNow)); Kick(); }
        Changed?.Invoke();
    }

    /// <summary>Survival checks and level scans whose replays are kept (KeepReplays of each); the verdicts and scan results
    /// (route-checks/*.json, route-checks/scans) are kept forever. Leveling runs are never pruned.</summary>
    public const int KeepReplays = 10;

    /// <summary>Deletes the replays of all but the newest KeepReplays checks of each kind.</summary>
    private void PruneReplays()
    {
        if (!System.IO.Directory.Exists(_runner.ChecksDirectory)) return;
        foreach (var kind in ReplayPatterns)
            foreach (var f in new DirectoryInfo(_runner.ChecksDirectory).GetFiles(kind).GroupBy(f => f.Name.Split('_')[0]).OrderByDescending(g => g.Key).Skip(KeepReplays).SelectMany(g => g))
                try { f.Delete(); } catch (IOException) { /* being read: next check prunes it */ }
    }

    /// <summary>Every level scan ever saved (route-checks/scans), newest first.</summary>
    public List<LevelScan> SavedScans() => System.IO.Directory.Exists(Path.Combine(Directory, "scans"))
        ? new DirectoryInfo(Path.Combine(Directory, "scans")).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => ReadScan(f.FullName)).Where(s => s is { Maps.Count: > 0 }).Cast<LevelScan>().ToList()
        : new();

    /// <summary>Saved results, newest first.</summary>
    public List<string> Results() => System.IO.Directory.Exists(Directory)
        ? new DirectoryInfo(Directory).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Select(f => Path.GetFileNameWithoutExtension(f.Name)).ToList()
        : new();

    private const string LiveSuffix = "_live";
    /// <summary>A saved result a live bot asked for (not this page).</summary>
    public static bool IsLive(string resultName) => resultName.EndsWith(LiveSuffix, StringComparison.Ordinal);

    public RouteResult? Load(string name) =>
        SimRunner.IsSafeName(name) && File.Exists(Path.Combine(Directory, name + ".json"))
            ? JsonSerializer.Deserialize<RouteResult>(File.ReadAllText(Path.Combine(Directory, name + ".json")), RouteResult.Json) : null;

    private void Kick()
    {
        if (_worker is { IsCompleted: false }) return;
        _worker = Task.Run(WorkAsync);
    }

    private async Task WorkAsync()
    {
        while (true)
        {
            Job job;
            lock (_lock)
            {
                if (!_queue.TryDequeue(out job!)) { Running = null; break; }
                Running = job;
                Percent = -1;
            }
            Changed?.Invoke();
            if (job.IsScan)
            {
                LevelScan scan;
                try { scan = await RunScanAsync(job); LastError = null; }
                catch (Exception e)
                {
                    LastError = $"{job.Character} level scan: {e.Message}";
                    scan = new LevelScan(job.Character, job.Level, job.From, new(), DateTime.UtcNow, 0);
                }
                lock (_lock) _scans[job.Key] = scan;
                Changed?.Invoke();
                continue;
            }
            RouteVerdict verdict;
            try
            {
                var result = await RunAsync(job);
                verdict = new RouteVerdict(job.From, job.To, result.Level, result.FailsAt == null, result.FailsAt, result.Verdict, DateTime.UtcNow);
                if (job.Live) RecordCrossed(job.Character, job.Level, result.Crossed);
                LastError = null;
            }
            catch (Exception e)
            {
                LastError = $"{job.Character} {job.From} -> {job.To}: {e.Message}";
                verdict = RouteVerdict.Unchecked(job.From, job.To, $"route check failed ({e.Message}), not blocking the move");
            }
            if (job.Live) lock (_lock) _verdicts[job.Key] = verdict with { Level = job.Level };
            Changed?.Invoke();
        }
        Changed?.Invoke();
    }

    private async Task<LevelScan> RunScanAsync(Job job)
    {
        var json = ScanFile(job.Character, job.Level);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(json)!);
        await RunHostAsync(["levelscan", job.Who, job.From, job.X.ToString(), job.Y.ToString(), string.Join(",", job.Maps!),
            "--json", json, "--config-of", job.ConfigOf!, "--character", job.Character, "--replays", _runner.ChecksDirectory], json, TimeSpan.FromMinutes(30));
        PruneReplays();
        return JsonSerializer.Deserialize<LevelScan>(File.ReadAllText(json), LevelScan.Json)!;
    }

    private async Task<RouteResult> RunAsync(Job job)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var name = $"{DateTime.Now:yyyyMMdd-HHmmss}_{SimRunner.SafeName(job.Character)}_{SimRunner.SafeName(job.From)}-{SimRunner.SafeName(job.To)}{(job.Live ? LiveSuffix : "")}";
        var json = Path.Combine(Directory, name + ".json");
        var args = new List<string> { "routesim", job.Who, job.From, job.X.ToString(), job.Y.ToString(), job.To, job.Seeds.ToString(), "--json", json, "--character", job.Character };
        if (job.ConfigOf != null) args.AddRange(["--config-of", job.ConfigOf]);
        if (job.Live && SafeMaps(job.Character, job.Level) is { } safe) args.AddRange(["--safe-maps", safe]);
        args.AddRange(["--replays", _runner.ChecksDirectory]); // each map viewable in the replay viewer; recording costs no measurable time
        await RunHostAsync(args, json, TimeSpan.FromMinutes(10));
        PruneReplays();
        return JsonSerializer.Deserialize<RouteResult>(File.ReadAllText(json), RouteResult.Json)!;
    }

    private async Task RunHostAsync(List<string> args, string json, TimeSpan timeout)
    {
        var (exitCode, output) = await _runner.RunHostAsync(args, timeout, pct => { Percent = pct; Changed?.Invoke(); });
        if (exitCode != 0 || !File.Exists(json))
            throw new InvalidOperationException($"{args[0]} exited with code {exitCode}: " + string.Join(" ", output.Split('\n').Where(l => l.Contains("Exception") || l.Contains("no route")).Take(2)));
    }
}
