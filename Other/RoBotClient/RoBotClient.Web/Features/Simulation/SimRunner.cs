using System.Diagnostics;
using System.Text.Json;
using RoBotClient.Bot.Offline;

namespace RoBotClient.Web.Features.Simulation;

/// <summary>
/// Runs offline hunts for the Simulation tab. Each run is the CLI's own "huntsim" in a separate process (the built
/// RoBotClient.Host), so the simulator's server state (static game-server singletons, its clock) never shares a process
/// with the live bots' forecasts; the run writes a HuntReplay JSON into sim-replays/, which the tab reads back.
/// One run at a time; nothing here touches a live bot.
/// </summary>
public sealed class SimRunner(string repoRoBot)
{
    public sealed record Request(string Map, string Bot, double Minutes, int Seed, string? Script);
    public sealed record Status(bool Running, Request? Request, DateTime StartedUtc, string? ReplayName, string? Error, string Output);

    private readonly object _lock = new();
    private Status _status = new(false, null, default, null, null, "");
    public event Action? Changed;

    public string Directory { get; } = Path.Combine(repoRoBot, "sim-replays");
    public string HostDll => Path.Combine(repoRoBot, "RoBotClient.Host", "bin", "Debug", "net9.0", "RoBotClient.Host.dll");
    public Status Current { get { lock (_lock) return _status; } }
    /// <summary>The running hunt's progress in whole percent (huntsim --progress), -1 before its first report.</summary>
    public int Percent => _percent;
    private volatile int _percent = -1;

    /// <summary>Replays of the live checks (RouteChecks: route crossings, level-scan hunts), apart from the leveling runs.</summary>
    public string ChecksDirectory => Path.Combine(Directory, "checks");

    /// <summary>Saved leveling runs, newest first.</summary>
    public List<string> Runs() => System.IO.Directory.Exists(Directory)
        ? new DirectoryInfo(Directory).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Select(f => Path.GetFileNameWithoutExtension(f.Name)).ToList()
        : new();

    /// <summary>Deletes every saved run (sim-replays/*.json); returns how many. Nothing while a run is writing one.</summary>
    public int DeleteAll()
    {
        lock (_lock) if (_status.Running) return 0;
        var files = System.IO.Directory.Exists(Directory) ? System.IO.Directory.GetFiles(Directory, "*.json") : [];
        foreach (var f in files) File.Delete(f);
        return files.Length;
    }

    /// <summary>A saved replay's file (a leveling run or a check's), or null for a name that isn't one (names come from the browser).</summary>
    public string? PathOf(string name) =>
        !IsSafeName(name) ? null
        : new[] { Directory, ChecksDirectory }.Select(d => Path.Combine(d, name + ".json")).FirstOrDefault(File.Exists);

    /// <summary>A file name from the browser that can't leave the folder.</summary>
    public static bool IsSafeName(string name) => System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9_.@-]+$");

    /// <summary>"Bot1" for "[BOT] Bot1": the short name the CLI finds a bot's builds and saved config by.</summary>
    public static string ShortName(string character) => character.Split(' ')[^1];

    public static string SafeName(string s) => new(s.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());

    /// <summary>Runs the Host CLI with --progress in a child process (the server code's statics can't share this one),
    /// reporting each "progress N" line. Stopped and a TimeoutException past the timeout.</summary>
    public async Task<(int ExitCode, string Output)> RunHostAsync(IEnumerable<string> args, TimeSpan timeout, Action<int> onProgress)
    {
        if (!File.Exists(HostDll)) throw new FileNotFoundException($"{HostDll} is missing: build it once with 'dotnet build RoBotClient.Host'.");
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } dotnet && File.Exists(dotnet) ? dotnet : "dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(HostDll)!,
        };
        foreach (var a in args.Prepend(HostDll).Append("--progress")) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = Task.Run(async () =>
        {
            var rest = new System.Text.StringBuilder();
            while (await p.StandardError.ReadLineAsync() is { } line)
                if (line.StartsWith("progress ") && int.TryParse(line[9..], out var pct)) onProgress(pct);
                else rest.AppendLine(line);
            return rest.ToString();
        });
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { p.Kill(true); throw new TimeoutException($"it took over {timeout.TotalMinutes:F0} minutes and was stopped"); }
        return (p.ExitCode, (await stdout) + (await stderr));
    }

    /// <summary>Bots with a recorded build (bot-duels/&lt;character&gt;.jsonl), by the short name huntsim takes ("Bot3").</summary>
    public List<string> BotsWithBuilds()
    {
        var dir = Path.Combine(repoRoBot, "bot-duels");
        return System.IO.Directory.Exists(dir)
            ? System.IO.Directory.GetFiles(dir, "*.jsonl").Select(f => ShortName(Path.GetFileNameWithoutExtension(f))).Distinct().OrderBy(n => n).ToList()
            : new();
    }

    public HuntReplay? Load(string name) =>
        PathOf(name) is { } path ? JsonSerializer.Deserialize<HuntReplay>(File.ReadAllText(path), HuntReplay.Json) : null;

    public bool Start(Request r)
    {
        lock (_lock)
        {
            if (_status.Running) return false;
            _status = new Status(true, r, DateTime.UtcNow, null, null, "");
            _percent = -1;
        }
        Changed?.Invoke();
        _ = Task.Run(() => RunAsync(r));
        return true;
    }

    private async Task RunAsync(Request r)
    {
        string? replay = null, error = null, output = "";
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var name = $"{DateTime.Now:yyyyMMdd-HHmmss}_{SafeName(r.Map)}_{SafeName(r.Bot)}_{r.Minutes:0.#}m_s{r.Seed}".Replace(',', '.');
            var file = Path.Combine(Directory, name + ".json");
            var args = new List<string> { "huntsim", r.Map, r.Bot, r.Minutes.ToString(System.Globalization.CultureInfo.InvariantCulture), r.Seed.ToString(), "--replay", file };
            if (r.Script != null) args.AddRange(["--script", r.Script.Replace("\r", "").Replace('\n', ';')]);
            int exitCode;
            (exitCode, output) = await RunHostAsync(args, TimeSpan.FromMinutes(10), pct => _percent = pct);
            if (exitCode != 0 || !File.Exists(file))
                throw new InvalidOperationException($"huntsim exited with code {exitCode}" + (File.Exists(file) ? "" : ", no replay written"));
            replay = name;
        }
        catch (Exception e) { error = e.Message; }
        lock (_lock) _status = _status with { Running = false, ReplayName = replay, Error = error, Output = output };
        Changed?.Invoke();
    }
}
