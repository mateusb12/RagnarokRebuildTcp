using System.Text;
using RoBotClient.Bot.Behavior;
using RoBotClient.Bot.GameData;
using RoBotClient.Bot.Offline;
using RoBotClient.Bot.Session;
using RoBotClient.Bot.State;
using RoBotClient.Simulation;

// Usage:  [mode] [seconds]
//   hunt (default) : log in, enter, then hunt nearby monsters / flee when hurt for N seconds (default 300)
//   smoke          : log in, enter, print one state dump, exit
var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "hunt";
var seconds = args.Length > 1 && int.TryParse(args[1], out var ss) ? ss : 300;

void Log(string s) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s}");

//   validate [dir] : replay the duels real bots recorded (default ../bot-duels) through the server-code simulator
//   benchmark      : time and size the server-code simulator against the old BattleSimulator
if (mode is "validate" or "benchmark")
{
    var serverDirectory = ServerCombatRuntime.FindServerProjectDirectory(AppContext.BaseDirectory)
                          ?? throw new DirectoryNotFoundException("RoRebuildServer project not found above " + AppContext.BaseDirectory);
    if (mode == "validate")
    {
        var directory = args.Length > 1 ? args[1] : Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(serverDirectory))!, "Other", "RoBotClient", "bot-duels");
        ServerCombatRuntime.EnsureInitialized(serverDirectory);
        Console.WriteLine(DuelValidator.ToMarkdown(DuelValidator.Validate(DuelStore.ReadAll(directory))));
    }
    else
        SimulatorBenchmark.Run(serverDirectory, Console.WriteLine);
    return;
}

//   huntsim <map> <build> <minutes> <seed> [--script "rule; rule"] [--configs bot-configs.json] [--trace file] [--replay file.json] [--check]
//     --replay writes the run as a HuntReplay (JSON) for the dashboard's Simulation tab.
//     build: a PlayerBuild JSON file, or a bot name ("Bot3", or "Bot3@mjolnir_06" for its build when it hunted there)
//            whose latest recorded duel build is used; a bot name also brings its saved BotBehaviorConfig (skill script,
//            rest, win and hunt rules) from bot-configs.json. --script replaces the script (rules separated by ';').
if (mode == "huntsim")
{
    if (args.Length < 5) { Console.WriteLine("usage: huntsim <map> <build.json | bot[@map]> <minutes> <seed> [--script \"rule; rule\"] [--configs file] [--trace file] [--replay file.json] [--check]"); return; }
    var serverDirectory = ServerCombatRuntime.FindServerProjectDirectory(AppContext.BaseDirectory)
                          ?? throw new DirectoryNotFoundException("RoRebuildServer project not found above " + AppContext.BaseDirectory);
    ServerCombatRuntime.EnsureInitialized(serverDirectory);
    var repo = Path.GetDirectoryName(Path.GetDirectoryName(serverDirectory))!;
    var data = GameDatabase.Load(Path.Combine(repo, "RebuildClient", "Assets", "StreamingAssets", "ClientConfigGenerated"),
        Path.Combine(repo, "RebuildClient", "Assets", "Maps", "exportdata"),
        Path.Combine(repo, "RoRebuildServer", "GameConfig", "ServerData", "Script", "Warps"));
    PlayerBuild build;
    string buildSource;
    Appearance? look = null;
    if (File.Exists(args[2]))
    {
        build = System.Text.Json.JsonSerializer.Deserialize<PlayerBuild>(File.ReadAllText(args[2]), DuelStore.JsonOptions)!;
        buildSource = args[2];
    }
    else
    {
        var duels = Path.Combine(repo, "Other", "RoBotClient", "bot-duels");
        var who = args[2].Split('@'); // "Bot3" or "Bot3@mjolnir_06": the latest build recorded (on that map)
        var record = DuelStore.ReadAll(duels).Where(r => r.Build != null && r.Character.EndsWith(who[0], StringComparison.OrdinalIgnoreCase)
                && (who.Length == 1 || string.Equals(r.Map, who[1], StringComparison.OrdinalIgnoreCase)))
            .MaxBy(r => r.EngagedAtUtc) ?? throw new ArgumentException($"no duel record with a build for '{args[2]}' in {duels}");
        build = record.Build!;
        look = DuelStore.ReadAll(duels).Where(r => r.Character == record.Character && r.Look != null).MaxBy(r => r.EngagedAtUtc)?.Look; // older records have none
        buildSource = $"{record.Character}'s build recorded {record.EngagedAtUtc:yyyy-MM-dd HH:mm}Z on {record.Map}";
    }
    // The bot's saved BotBehaviorConfig from bot-configs.json, read by the store the live BotManager uses; --script
    // replaces its skill rules.
    RoBotClient.Bot.Behavior.BotBehaviorConfig? botConfig = null;
    string? configSource = null;
    var configsAt = Array.IndexOf(args, "--configs"); // another bot-configs.json (same format), for what-if configs
    var configs = configsAt > 0 && configsAt + 1 < args.Length ? args[configsAt + 1] : Path.Combine(repo, "Other", "RoBotClient", "bot-configs.json");
    if (!File.Exists(args[2]) && File.Exists(configs))
    {
        var botName = args[2].Split('@')[0];
        var store = new RoBotClient.Bot.Manager.BotConfigStore(configs);
        var key = store.List().FirstOrDefault(k => k.EndsWith(" " + botName, StringComparison.OrdinalIgnoreCase));
        if (key != null) (botConfig, configSource) = (store.Get(key), $"{key} in {configs}");
    }
    var script = botConfig?.SkillScript ?? "";
    var scriptAt = Array.IndexOf(args, "--script");
    if (scriptAt > 0 && scriptAt + 1 < args.Length) script = args[scriptAt + 1].Replace(';', '\n');
    var options = new HuntSimOptions(args[1], build, double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) * 60, int.Parse(args[4]),
        SkillScript: script, NoAutoAttack: botConfig?.NoAutoAttack ?? false, Config: botConfig, ConfigSource: configSource);
    var started = System.Diagnostics.Stopwatch.StartNew();
    var sim = new HuntSimulator(options, data);
    sim.Run();
    Console.WriteLine($"Build source: {buildSource}. Wall time {started.Elapsed.TotalSeconds:F1}s.");
    Console.WriteLine(sim.Report());
    var traceAt = Array.IndexOf(args, "--trace");
    if (traceAt > 0 && traceAt + 1 < args.Length) { File.WriteAllLines(args[traceAt + 1], sim.Trace); Console.WriteLine($"Trace: {sim.Trace.Count} events -> {args[traceAt + 1]}"); }
    var replayAt = Array.IndexOf(args, "--replay");
    if (replayAt > 0 && replayAt + 1 < args.Length)
    {
        var replay = sim.Replay(buildSource);
        replay.Look = look;
        File.WriteAllText(args[replayAt + 1], System.Text.Json.JsonSerializer.Serialize(replay, HuntReplay.Json));
        Console.WriteLine($"Replay: {replay.Events.Count} events, {replay.Track.Count} steps -> {args[replayAt + 1]}");
    }
    if (args.Contains("--check"))
    {
        Console.WriteLine("\n== Verification ==");
        Console.WriteLine("Invariants enforced during the run (any violation throws): every step is to an adjacent walkable cell without cutting a wall corner; "
            + "every candidate is within view (21 cells); every fight starts in melee range; a killed monster stays dead until a respawn delay inside its rule; "
            + "the clock equals walking + fighting + idle + resting time.");
        Console.WriteLine($"Clock: {sim.WanderSeconds:F3} wander + {sim.ApproachSeconds:F3} walk-up + {sim.FightSeconds:F3} fight + {sim.IdleSeconds:F3} idle + {sim.RestSeconds:F3} rest = "
            + $"{sim.WanderSeconds + sim.ApproachSeconds + sim.FightSeconds + sim.IdleSeconds + sim.RestSeconds:F3}s of {options.DurationSeconds:F0}s; "
            + $"{sim.Steps} steps = {sim.Steps - sim.DiagonalSteps} x 0.15s + {sim.DiagonalSteps} x 0.212s = {(sim.Steps - sim.DiagonalSteps) * 0.15 + sim.DiagonalSteps * 0.15 * 1.4142:F3}s walked");
        Console.WriteLine("Example obstacle: " + (sim.ObstacleExample ?? "none drawn"));
        Console.WriteLine("Example respawn: " + (sim.RespawnExample ?? "no respawn within the run"));
        foreach (var example in sim.Examples) Console.WriteLine("Example " + example);
        var again = new HuntSimulator(options, data);
        again.Run();
        var other = new HuntSimulator(options with { Seed = options.Seed + 1 }, data);
        other.Run();
        Console.WriteLine($"Determinism: seed {options.Seed} twice -> {sim.Fingerprint()} / {again.Fingerprint()} ({(sim.Fingerprint() == again.Fingerprint() && sim.Exp == again.Exp ? "identical" : "DIFFERENT")}); "
            + $"seed {options.Seed + 1} -> {other.Fingerprint()}, {other.Kills} kills / {other.Exp} exp vs {sim.Kills} / {sim.Exp}");
        Console.WriteLine("\nSkill evidence (first trace lines per skill: the rule firing, then what the server did):");
        foreach (var skill in sim.RuleFired.Keys)
        {
            var name = skill.ToString();
            var first = sim.Trace.FindIndex(l => l.Contains($"rule fires: {name} "));
            if (first < 0) continue;
            foreach (var line in sim.Trace.Skip(first).Take(12).Where(l => l.Contains(name) || l.Contains("engage") || l.Contains("killed")).Take(6))
                Console.WriteLine("  " + line);
            var effect = sim.Trace.FirstOrDefault(l => l.Contains($"{name}: executed"));
            if (effect != null && !sim.Trace.Skip(first).Take(12).Contains(effect)) Console.WriteLine("  ... " + effect);
            Console.WriteLine();
        }
        Console.WriteLine("\nFirst 40 trace events:");
        foreach (var line in sim.Trace.Take(40)) Console.WriteLine("  " + line);
    }
    return;
}

var gameData = GameDatabase.Load(ResolveRepoSubdir(Path.Combine("RebuildClient", "Assets", "StreamingAssets", "ClientConfigGenerated")),
                                 ResolveRepoSubdir(Path.Combine("RebuildClient", "Assets", "Maps", "exportdata")));
Log($"GameData: {gameData.MonstersById.Count} monsters, {gameData.MapsByCode.Count} maps loaded.");

var config = new BotConfig { CharacterBaseName = "BotZero" };
await using var bot = new BotSession(config, gameData);
bot.OnLog += Log;

if (!await bot.ConnectAndEnterAsync())
{
    Log("Could not enter the world. Is the server running?");
    return;
}

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(mode == "smoke" ? 5 : seconds));
var runTask = bot.RunAsync(cts.Token);
await Task.Delay(2500); // let the world-state burst (UpdatePlayerData + CreateEntity2 x N) arrive

if (mode == "smoke")
{
    PrintState(null);
    cts.Cancel();
    try { await runTask; } catch { }
    Log("Done.");
    return;
}

// HUNT: target weak field monsters, ignore the indestructible dummy, flee the boss or when hurt.
var behaviorConfig = new BotBehaviorConfig { HomeMap = "prt_fild08", Verbose = true };
AddCodes(behaviorConfig.IgnoreClassIds, "TARGET_DUMMY");
Log($"Engaging any monster the battle forecast deems winnable (margin {behaviorConfig.WinMargin}, <= {behaviorConfig.MaxRoundsToKill} hits); ignoring [{string.Join(", ", behaviorConfig.IgnoreClassIds)}].");

var behavior = new BotBehavior(bot, behaviorConfig, gameData);
behavior.OnLog += Log;
var behaviorTask = behavior.RunAsync(cts.Token);

Log($"HUNT mode for {seconds}s on '{bot.World.Self.Map}'. Watch '{bot.World.Self.Name}' in your client.");
var nextPrint = DateTime.UtcNow;
while (!cts.IsCancellationRequested)
{
    if (DateTime.UtcNow >= nextPrint) { PrintState(behavior); nextPrint = DateTime.UtcNow.AddSeconds(5); }
    try { await Task.Delay(1000, cts.Token); } catch { break; }
}

cts.Cancel();
try { await Task.WhenAll(runTask, behaviorTask); } catch { }
Log("Done.");
DumpTelemetry(bot);

void DumpTelemetry(BotSession b)
{
    var t = b.Telemetry;
    var sb = new StringBuilder();
    sb.AppendLine("──────── TELEMETRY ────────");
    sb.AppendLine($" Kills {t.Count(TelemetryEventType.Kill)}  Deaths {t.Count(TelemetryEventType.Death)}  Looted {t.Count(TelemetryEventType.Loot)}  Used {t.Count(TelemetryEventType.UsedItem)}  LevelUps {t.Count(TelemetryEventType.LevelUp)}");
    foreach (var kv in t.CountByDetail(TelemetryEventType.Kill).OrderByDescending(k => k.Value))
        sb.AppendLine($"   kill: {kv.Key} x{kv.Value}");
    foreach (var kv in t.CountByDetail(TelemetryEventType.Loot).OrderByDescending(k => k.Value))
        sb.AppendLine($"   loot: {kv.Key} x{kv.Value}");
    foreach (var kv in t.CountByDetail(TelemetryEventType.UsedItem).OrderByDescending(k => k.Value))
        sb.AppendLine($"   used: {kv.Key} x{kv.Value}");
    foreach (var kv in t.CountByDetail(TelemetryEventType.Death).OrderByDescending(k => k.Value))
        sb.AppendLine($"   died to: {kv.Key} x{kv.Value}");
    foreach (var kv in t.TimeByMap())
        sb.AppendLine($"   map {kv.Key}: {kv.Value.TotalSeconds:F0}s");
    sb.Append("───────────────────────────");
    Console.WriteLine(sb.ToString());
}

void AddCodes(HashSet<int> set, params string[] codes)
{
    foreach (var code in codes)
    {
        var id = gameData.ClassIdOf(code);
        if (id.HasValue) set.Add(id.Value);
        else Log($"  (warning) unknown monster code '{code}'");
    }
}

void PrintState(BotBehavior? b)
{
    var text = bot.WithState(w =>
    {
        var s = w.Self;
        var pos = w.SelfPosition;
        var monsters = w.Entities.Values.Count(e => e.IsMonster && e.Id != s.EntityId);
        var npcCount = w.Entities.Values.Count(e => e.IsNpc);
        EntityView? nearNpc = null; var nearNpcD = int.MaxValue;
        foreach (var e in w.Entities.Values)
        {
            if (!e.IsNpc) continue;
            var d = Math.Abs(e.Position.X - pos.X) + Math.Abs(e.Position.Y - pos.Y);
            if (d < nearNpcD) { nearNpcD = d; nearNpc = e; }
        }
        var selfEntity = w.Entities.TryGetValue(s.EntityId, out var se) ? se : null;
        var hp = selfEntity?.Hp ?? s.Hp;       // the live, combat-tracked HP
        var maxHp = selfEntity?.MaxHp ?? s.MaxHp;
        var sb = new StringBuilder();
        sb.AppendLine("──────── STATE ────────");
        sb.AppendLine($" {s.Name}  Lv {s.Level}/{s.JobLevel}  HP {hp}/{maxHp}  SP {s.Sp}/{s.MaxSp}  Kills {s.Kills}  BaseExp {s.BaseExp}");
        sb.AppendLine($" Map {s.Map}  Pos ({pos.X},{pos.Y})  Zeny {s.Zeny}  Items {s.Inventory.Count}  GroundDrops {w.GroundItems.Count}");
        if (b != null)
            sb.AppendLine($" Mode {b.Mode}  Target {(b.TargetId != 0 ? $"{b.TargetName} (#{b.TargetId})" : "none")}  Monsters in view {monsters}");
        sb.AppendLine($" NPCs in view {npcCount}; nearest {(nearNpc != null ? $"'{nearNpc.Name}'@({nearNpc.Position.X},{nearNpc.Position.Y}) d{nearNpcD}" : "none")}");
        sb.Append("───────────────────────");
        return sb.ToString();
    });
    Console.WriteLine(text);
}

static string ResolveRepoSubdir(string relative)
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, relative);
        if (Directory.Exists(candidate))
            return candidate;
    }
    return "";
}
