using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RoBotClient.Simulation;

public enum ForecastPriority { Urgent = 0, Background = 1 }

/// <param name="Prediction">Null when the forecast failed; Error says why.</param>
/// <param name="CacheHit">Answered from the cache: an identical request (same inputs, same server scripts) was simulated before.</param>
/// <param name="Deduplicated">Joined an identical request already waiting or running.</param>
public sealed record ForecastResult(DuelPrediction? Prediction, string? Error, bool CacheHit, bool Deduplicated);

/// <summary>
/// The one background thread every bot's server-code simulations run on, so no simulation ever runs on a bot's
/// decision loop. Requests go into a bounded queue ordered by priority (urgent first, then arrival); identical
/// requests share one simulation, and finished ones are cached.
///
/// The cache key is a hash of everything the prediction depends on: the request itself (build with equipment,
/// refine, cards and skills; monster; HP; SP; monster HP; who attacks first; trials; seed), a key the caller gives
/// for its action policy (a delegate can't be compared), and the server Script.dll the simulator loaded. A request
/// without a seed gets one derived from that key, so a cached answer is exactly what simulating again would give.
/// Any changed input is a different key; nothing is ever bucketed or approximated.
///
/// ponytail: one thread, so simulations never contend for ServerCombatRuntime.Gate with each other. Ceiling: one
/// core of simulation. A running background job is never interrupted, so an urgent request waits at most one job.
/// </summary>
public static class ForecastWorker
{
    public const int QueueCapacity = 64;      // urgent requests are refused only when it is full...
    public const int BackgroundCapacity = 32; // ...background ones already at half, leaving the rest to urgent ones
    public const int CacheCapacity = 4096;    // ponytail: cleared when full; an LRU if hit rates ever suffer from it
    private static readonly TimeSpan StatsEvery = TimeSpan.FromSeconds(10);

    private sealed class Job(string key, DuelRequest request)
    {
        public readonly string Key = key;
        public readonly DuelRequest Request = request;
        public readonly TaskCompletionSource<ForecastResult> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long EnqueuedAt = Stopwatch.GetTimestamp();
        public bool Started;
    }

    private static readonly object Lock = new();
    private static readonly PriorityQueue<Job, (int Priority, long Order)> Queue = new();
    private static readonly Dictionary<string, Job> Pending = new();
    private static readonly Dictionary<string, DuelPrediction> Cache = new();
    private static long order;
    private static Thread? thread;
    private static string? initError;
    private static bool scriptsChanged;

    /// <summary>Where the worker writes its counters every 10 s (JSON); null: not written.</summary>
    public static string? StatsPath { get; set; }

    /// <summary>Decision-loop durations of the bots, recorded by them into the same stats file.</summary>
    public static readonly LatencyStats BotTickMs = new();
    public static readonly LatencyStats QueueWaitMs = new();
    public static readonly LatencyStats ComputeMs = new();
    private static long submitted, cacheHits, deduplicated, rejected, computed, failed, maxQueueDepth;
    private static double busySeconds;
    private static readonly DateTime StartedAt = DateTime.UtcNow;

    /// <summary>Queue a simulation. Never blocks on simulation work; the task completes on the worker thread.</summary>
    /// <param name="policyKey">Identifies the request's Policy: equal keys must mean the policy chooses the same
    /// action in every moment. Ignored (and may be anything) when Policy is null.</param>
    public static Task<ForecastResult> Submit(DuelRequest request, string policyKey, ForecastPriority priority)
    {
        if (request.Policy == null) request = request with { PlayerSp = 0 }; // SP only matters to skills
        var key = Key(request, policyKey);
        if (request.Seed == null) request = request with { Seed = BitConverter.ToInt32(Convert.FromHexString(key[..8])) };
        lock (Lock)
        {
            EnsureStarted();
            submitted++;
            if (scriptsChanged)
            {
                rejected++;
                return Task.FromResult(new ForecastResult(null, "rejected: the server's Script.dll changed since the simulator loaded it (restart to reload)", false, false));
            }
            if (Cache.TryGetValue(key, out var cached))
            {
                cacheHits++;
                return Task.FromResult(new ForecastResult(cached, null, true, false));
            }
            if (Pending.TryGetValue(key, out var waiting))
            {
                deduplicated++;
                if (!waiting.Started && priority == ForecastPriority.Urgent) Queue.Enqueue(waiting, (0, order++)); // promote; the stale entry is skipped
                return waiting.Done.Task.ContinueWith(t => t.Result with { Deduplicated = true }, TaskContinuationOptions.ExecuteSynchronously);
            }
            if (Queue.Count >= (priority == ForecastPriority.Urgent ? QueueCapacity : BackgroundCapacity))
            {
                rejected++;
                return Task.FromResult(new ForecastResult(null, $"rejected: forecast queue full ({Queue.Count})", false, false));
            }
            var job = new Job(key, request);
            Pending[key] = job;
            Queue.Enqueue(job, ((int)priority, order++));
            maxQueueDepth = Math.Max(maxQueueDepth, Pending.Count);
            Monitor.Pulse(Lock);
            return job.Done.Task;
        }
    }

    private static string Key(DuelRequest request, string policyKey)
    {
        var text = JsonSerializer.Serialize(request with { Policy = null }, DuelStore.JsonOptions)
                   + (request.Policy == null ? "" : "|" + policyKey) + "|" + ServerCombatRuntime.ScriptVersion;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    // Under Lock.
    private static void EnsureStarted()
    {
        if (thread != null) return;
        thread = new Thread(Run) { IsBackground = true, Name = "ForecastWorker" };
        thread.Start();
    }

    /// <summary>Starts the worker (and its stats file) without queueing anything.</summary>
    public static void Start() { lock (Lock) EnsureStarted(); }

    private static void Run()
    {
        initError = Initialize(); // the server's data and Script.dll: ~0.6 s, here rather than on a bot's tick
        var nextStats = DateTime.UtcNow;
        while (true)
        {
            Job? job = null;
            lock (Lock)
            {
                while (job == null)
                {
                    if (DateTime.UtcNow >= nextStats) break;
                    if (Queue.TryDequeue(out var next, out _)) { if (!next.Started) job = next; continue; }
                    Monitor.Wait(Lock, nextStats - DateTime.UtcNow);
                }
                if (job != null) job.Started = true;
            }
            if (job == null)
            {
                nextStats = DateTime.UtcNow + StatsEvery;
                WriteStats();
                continue;
            }

            QueueWaitMs.Add(Stopwatch.GetElapsedTime(job.EnqueuedAt).TotalMilliseconds);
            var started = Stopwatch.GetTimestamp();
            ForecastResult result;
            try
            {
                result = initError != null ? new ForecastResult(null, initError, false, false)
                    : new ForecastResult(DuelSimulator.Simulate(job.Request), null, false, false);
            }
            catch (Exception e)
            {
                result = new ForecastResult(null, e.ToString(), false, false);
            }
            var elapsed = Stopwatch.GetElapsedTime(started);
            ComputeMs.Add(elapsed.TotalMilliseconds);
            lock (Lock)
            {
                busySeconds += elapsed.TotalSeconds;
                Pending.Remove(job.Key);
                if (result.Prediction != null)
                {
                    computed++;
                    if (Cache.Count >= CacheCapacity) Cache.Clear();
                    Cache[job.Key] = result.Prediction;
                }
                else failed++;
            }
            job.Done.SetResult(result);
        }
    }

    private static string? Initialize()
    {
        if (ServerCombatRuntime.IsInitialized) return null; // a host that initialised it itself
        try
        {
            var directory = ServerCombatRuntime.FindServerProjectDirectory(AppContext.BaseDirectory)
                            ?? ServerCombatRuntime.FindServerProjectDirectory(Environment.CurrentDirectory);
            if (directory == null) return "RoRebuildServer project directory not found";
            ServerCombatRuntime.EnsureInitialized(directory);
            return null;
        }
        catch (Exception e)
        {
            return $"simulator initialisation failed: {e.GetType().Name}: {e.Message}";
        }
    }

    private static void WriteStats()
    {
        if (initError == null && ServerCombatRuntime.IsInitialized && ServerCombatRuntime.ScriptsChangedSinceLoad())
            lock (Lock) scriptsChanged = true;
        if (StatsPath is not { } path) return;
        try
        {
            object stats;
            lock (Lock)
            {
                var wall = (DateTime.UtcNow - StartedAt).TotalSeconds;
                stats = new
                {
                    WrittenAtUtc = DateTime.UtcNow, StartedAtUtc = StartedAt, InitError = initError, ScriptsChangedSinceLoad = scriptsChanged,
                    Submitted = submitted, CacheHits = cacheHits, Deduplicated = deduplicated, Rejected = rejected, Computed = computed, Failed = failed,
                    CacheHitRate = submitted > 0 ? (double)cacheHits / submitted : 0,
                    QueueDepth = Pending.Count, MaxQueueDepth = maxQueueDepth, CacheEntries = Cache.Count,
                    CacheBytesApprox = Cache.Count * 580L, // measured: ~400 B per DuelPrediction + 64-char key + entry
                    BusySeconds = busySeconds, WallSeconds = wall, Utilisation = wall > 0 ? busySeconds / wall : 0,
                    WorkerCpuSeconds = workerCpuSeconds(),
                    QueueWaitMs = QueueWaitMs.Summary(), ComputeMs = ComputeMs.Summary(), BotTickMs = BotTickMs.Summary(),
                };
            }
            File.WriteAllText(path, JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }

        // CPU time of this thread from Linux procfs (utime + stime, in clock ticks of 1/100 s); null elsewhere.
        static double? workerCpuSeconds()
        {
            try
            {
                var fields = File.ReadAllText("/proc/thread-self/stat").Split(')')[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return (long.Parse(fields[11]) + long.Parse(fields[12])) / 100.0;
            }
            catch { return null; }
        }
    }
}

/// <summary>The last 4096 samples of a duration, for percentiles; count and max over all samples.</summary>
public sealed class LatencyStats
{
    private readonly double[] samples = new double[4096];
    private long count;
    private double max;

    public void Add(double ms)
    {
        lock (samples)
        {
            samples[count++ % samples.Length] = ms;
            max = Math.Max(max, ms);
        }
    }

    public object Summary()
    {
        double[] window;
        long n;
        lock (samples)
        {
            n = count;
            window = samples.Take((int)Math.Min(count, samples.Length)).ToArray();
        }
        Array.Sort(window);
        double At(double q) => window.Length == 0 ? 0 : window[(int)Math.Min(window.Length - 1, q * window.Length)];
        return new { Count = n, P50 = At(0.5), P95 = At(0.95), P99 = At(0.99), Max = max, WindowMean = window.Length == 0 ? 0 : window.Average(), Over250 = window.Count(v => v > 250) };
    }
}
