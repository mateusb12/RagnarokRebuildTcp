using System.Diagnostics;
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
public sealed class RouteChecks(SimRunner runner, string repoRoBot)
{
    /// <summary>A route check, or with Maps a level scan (To then only describes it).</summary>
    public sealed record Job(string Character, string Who, string? ConfigOf, string From, int X, int Y, string To, int Seeds, bool Live, int Level, DateTime QueuedUtc,
        IReadOnlyList<string>? Maps = null)
    {
        public string Key => Maps != null ? ScanKey(Character, Level) : RouteChecks.Key(Character, Level, From, To);
    }

    public const double ScanMinutes = 10;
    /// <summary>A level scan is redone after this even without a level-up: a slow leveler (Bot4 at Lv 45) otherwise kept a
    /// day-old scan from another map and older gear. The rescan overwrites the level's file.</summary>
    // ponytail: a property, not a static readonly field: hot reload adds new fields without running their initializer (it was 0).
    public static TimeSpan ScanTtl => TimeSpan.FromHours(2);

    public const int LiveSeeds = 5;
    public static readonly TimeSpan VerdictTtl = TimeSpan.FromHours(2);
    public string Directory { get; } = Path.Combine(repoRoBot, "route-checks");

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

    private static string Key(string character, int level, string from, string to) => $"{character}|{level}|{from}|{to}".ToLowerInvariant();
    private static string ScanKey(string character, int level) => $"{character}|{level}".ToLowerInvariant();
    private string ScanFile(string character, int level) => Path.Combine(Directory, "scans", $"{Safe(character)}_L{level}.json");
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
        var file = Path.Combine(Directory, "survived", $"{Safe(character)}.json");
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
            File.WriteAllText(Path.Combine(Directory, "survived", $"{Safe(character)}.json"), JsonSerializer.Serialize(known, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static string Safe(string s) => new(s.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());

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
            var saved = ScanFile(character, build.Level);
            if (File.Exists(saved))
                try
                {
                    var loaded = JsonSerializer.Deserialize<LevelScan>(File.ReadAllText(saved), LevelScan.Json)!;
                    if (DateTime.UtcNow - loaded.AtUtc < ScanTtl) return _scans[key] = loaded;
                }
                catch (Exception) { /* unreadable: scan again */ }
            if (Running?.Key == key || _queue.Any(j => j.Key == key)) return null;
            System.IO.Directory.CreateDirectory(Path.Combine(Directory, "builds"));
            var file = Path.Combine(Directory, "builds", $"{Safe(character)}_scan.json");
            File.WriteAllText(file, JsonSerializer.Serialize(build, DuelStore.JsonOptions));
            _queue.Enqueue(new Job(character, file, character.Split(' ')[^1], from, x, y, $"{maps.Count} maps (level scan)", LiveSeeds, true, build.Level,
                DateTime.UtcNow, maps.ToList()));
            Kick();
        }
        Changed?.Invoke();
        return null;
    }

    /// <summary>The bot's scan for this level, as its map choice used it (memory, else route-checks/scans), or null.</summary>
    public LevelScan? ScanOf(string character, int level)
    {
        lock (_lock)
        {
            var key = ScanKey(character, level);
            if (_scans.TryGetValue(key, out var scan)) return scan.Maps.Count > 0 ? scan : null;
            var saved = ScanFile(character, level);
            try { return File.Exists(saved) ? _scans[key] = JsonSerializer.Deserialize<LevelScan>(File.ReadAllText(saved), LevelScan.Json)! : null; }
            catch (Exception) { return null; }
        }
    }

    /// <summary>A live bot's question (BotManager.RouteCheck): the fresh verdict, or null while its check is queued or running.</summary>
    public RouteVerdict? Check(string character, BotSession session, string from, int x, int y, string to)
    {
        var (build, _) = session.CaptureCombatState();
        var key = Key(character, build.Level, from, to);
        lock (_lock)
        {
            if (_verdicts.TryGetValue(key, out var v) && DateTime.UtcNow - v.AtUtc < VerdictTtl) return v;
            if (Running?.Key == key || _queue.Any(j => j.Key == key)) return null;
            System.IO.Directory.CreateDirectory(Path.Combine(Directory, "builds"));
            var file = Path.Combine(Directory, "builds", $"{Safe(character)}_{Safe(to)}.json");
            File.WriteAllText(file, JsonSerializer.Serialize(build, DuelStore.JsonOptions));
            _queue.Enqueue(new Job(character, file, character.Split(' ')[^1], from, x, y, to, LiveSeeds, true, build.Level, DateTime.UtcNow));
            Kick();
        }
        Changed?.Invoke();
        return null;
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

    /// <summary>Deletes the route replays ("{stamp}_route_...", one stamp per check) of all but the newest KeepReplays checks.</summary>
    private void PruneReplays()
    {
        if (!System.IO.Directory.Exists(runner.Directory)) return;
        foreach (var kind in new[] { "*_route_*.json", "*_scan_*.json" })
            foreach (var f in new DirectoryInfo(runner.Directory).GetFiles(kind).GroupBy(f => f.Name.Split('_')[0]).OrderByDescending(g => g.Key).Skip(KeepReplays).SelectMany(g => g))
                try { f.Delete(); } catch (IOException) { /* being read: next check prunes it */ }
    }

    /// <summary>Every level scan ever saved (route-checks/scans), newest first.</summary>
    public List<LevelScan> SavedScans() => System.IO.Directory.Exists(Path.Combine(Directory, "scans"))
        ? new DirectoryInfo(Path.Combine(Directory, "scans")).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => { try { return JsonSerializer.Deserialize<LevelScan>(File.ReadAllText(f.FullName), LevelScan.Json); } catch (Exception) { return null; } })
            .Where(s => s is { Maps.Count: > 0 }).Cast<LevelScan>().ToList()
        : new();

    /// <summary>Saved results, newest first.</summary>
    public List<string> Results() => System.IO.Directory.Exists(Directory)
        ? new DirectoryInfo(Directory).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Select(f => Path.GetFileNameWithoutExtension(f.Name)).ToList()
        : new();

    public RouteResult? Load(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9_.@-]+$") && File.Exists(Path.Combine(Directory, name + ".json"))
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
            if (job.Maps != null)
            {
                LevelScan scan;
                try { scan = await RunScanAsync(job); LastError = null; }
                catch (Exception e)
                {
                    LastError = $"{job.Character} level scan: {e.Message}";
                    scan = new LevelScan(job.Character, job.Level, job.From, new(), DateTime.UtcNow, 0);
                }
                RecordCrossed(job.Character, job.Level, scan.Crossed);
                lock (_lock)
                {
                    _scans[job.Key] = scan;
                    foreach (var m in scan.Maps) // the scan's routes answer the move gate's RouteCheck from the same spot
                        if (m.Route != null) _verdicts[Key(job.Character, job.Level, job.From, m.Map)] = m.Route with { Level = job.Level };
                }
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
        await RunHostAsync(new[] { "levelscan", job.Who, job.From, job.X.ToString(), job.Y.ToString(), string.Join(",", job.Maps!),
            "--minutes", ScanMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json", json, "--config-of", job.ConfigOf! }
            .Concat(new[] { "--replays", runner.Directory }), json, TimeSpan.FromMinutes(30));
        PruneReplays();
        return JsonSerializer.Deserialize<LevelScan>(File.ReadAllText(json), LevelScan.Json)!;
    }

    private async Task<RouteResult> RunAsync(Job job)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var name = $"{DateTime.Now:yyyyMMdd-HHmmss}_{Safe(job.Character)}_{Safe(job.From)}-{Safe(job.To)}{(job.Live ? "_live" : "")}";
        var json = Path.Combine(Directory, name + ".json");
        var args = new List<string> { "routesim", job.Who, job.From, job.X.ToString(), job.Y.ToString(), job.To, job.Seeds.ToString(), "--json", json };
        if (job.ConfigOf != null) args.AddRange(new[] { "--config-of", job.ConfigOf });
        if (job.Live && SafeMaps(job.Character, job.Level) is { } safe) args.AddRange(new[] { "--safe-maps", safe });
        args.AddRange(new[] { "--replays", runner.Directory }); // each map viewable in the replay viewer; recording costs no measurable time
        await RunHostAsync(args, json, TimeSpan.FromMinutes(10));
        PruneReplays();
        return JsonSerializer.Deserialize<RouteResult>(File.ReadAllText(json), RouteResult.Json)!;
    }

    /// <summary>Runs the CLI (one child process: the server code's statics can't share this one), reporting its progress.</summary>
    private async Task RunHostAsync(IEnumerable<string> args, string json, TimeSpan timeout)
    {
        if (!File.Exists(runner.HostDll)) throw new FileNotFoundException($"{runner.HostDll} is missing: build it once with 'dotnet build RoBotClient.Host'.");
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } dotnet && File.Exists(dotnet) ? dotnet : "dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(runner.HostDll)!,
        };
        foreach (var a in args.Prepend(runner.HostDll).Append("--progress")) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = Task.Run(async () =>
        {
            var rest = new System.Text.StringBuilder();
            while (await p.StandardError.ReadLineAsync() is { } line)
                if (line.StartsWith("progress ") && int.TryParse(line[9..], out var pct)) { Percent = pct; Changed?.Invoke(); }
                else rest.AppendLine(line);
            return rest.ToString();
        });
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { p.Kill(true); throw new TimeoutException($"it took over {timeout.TotalMinutes:F0} minutes and was stopped"); }
        var output = (await stdout) + (await stderr);
        if (p.ExitCode != 0 || !File.Exists(json))
            throw new InvalidOperationException($"{psi.ArgumentList[1]} exited with code {p.ExitCode}: " + string.Join(" ", output.Split('\n').Where(l => l.Contains("Exception") || l.Contains("no route")).Take(2)));
    }
}
