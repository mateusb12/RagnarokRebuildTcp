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

    /// <summary>Saved runs, newest first.</summary>
    public List<string> Runs() => System.IO.Directory.Exists(Directory)
        ? new DirectoryInfo(Directory).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Select(f => Path.GetFileNameWithoutExtension(f.Name)).ToList()
        : new();

    /// <summary>A saved run's file, or null for a name that isn't one (names come from the browser).</summary>
    public string? PathOf(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9_.@-]+$") && File.Exists(Path.Combine(Directory, name + ".json"))
            ? Path.Combine(Directory, name + ".json") : null;

    /// <summary>Bots with a recorded build (bot-duels/&lt;character&gt;.jsonl), by the short name huntsim takes ("Bot3").</summary>
    public List<string> BotsWithBuilds()
    {
        var dir = Path.Combine(repoRoBot, "bot-duels");
        return System.IO.Directory.Exists(dir)
            ? System.IO.Directory.GetFiles(dir, "*.jsonl").Select(f => Path.GetFileNameWithoutExtension(f).Split(' ')[^1]).Distinct().OrderBy(n => n).ToList()
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
            if (!File.Exists(HostDll)) throw new FileNotFoundException($"{HostDll} is missing: build it once with 'dotnet build RoBotClient.Host'.");
            System.IO.Directory.CreateDirectory(Directory);
            var safe = (string s) => new string(s.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());
            var name = $"{DateTime.Now:yyyyMMdd-HHmmss}_{safe(r.Map)}_{safe(r.Bot)}_{r.Minutes:0.#}m_s{r.Seed}".Replace(',', '.');
            var file = Path.Combine(Directory, name + ".json");
            var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } dotnet && File.Exists(dotnet) ? dotnet : "dotnet")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(HostDll)!,
            };
            foreach (var a in new[] { HostDll, "huntsim", r.Map, r.Bot, r.Minutes.ToString(System.Globalization.CultureInfo.InvariantCulture), r.Seed.ToString(), "--replay", file })
                psi.ArgumentList.Add(a);
            if (r.Script != null) { psi.ArgumentList.Add("--script"); psi.ArgumentList.Add(r.Script.Replace("\r", "").Replace('\n', ';')); }
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { p.Kill(true); throw new TimeoutException("the run took over 10 minutes and was stopped"); }
            output = (await stdout) + (await stderr);
            if (p.ExitCode != 0 || !File.Exists(file))
                throw new InvalidOperationException($"huntsim exited with code {p.ExitCode}" + (File.Exists(file) ? "" : ", no replay written"));
            replay = name;
        }
        catch (Exception e) { error = e.Message; }
        lock (_lock) _status = _status with { Running = false, ReplayName = replay, Error = error, Output = output };
        Changed?.Invoke();
    }
}
