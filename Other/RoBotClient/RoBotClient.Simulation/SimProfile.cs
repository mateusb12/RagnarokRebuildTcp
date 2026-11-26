using System.Diagnostics;

namespace RoBotClient.Simulation;

/// <summary>Opt-in exclusive-time profiler for the offline sims (huntsim --profile). Each scope's time minus its nested
/// scopes' is charged to its name, so the buckets add up to the wall time of the profiled thread (work on other threads
/// adds on top). Off: a scope costs one bool check.</summary>
public static class SimProfile
{
    public static bool Enabled;
    private static readonly Dictionary<string, (long Self, long Calls)> Totals = new();
    [ThreadStatic] private static Stack<Frame>? _stack;
    private sealed class Frame { public string Name = ""; public long Start, Child; }

    /// <param name="byCaller">charge it as "name <- enclosing scope" (who asks for it).</param>
    public static Scope Of(string name, bool byCaller = false)
    {
        if (!Enabled) return default;
        if (byCaller) name += " <- " + (_stack is { Count: > 0 } s ? s.Peek().Name : "(no scope)");
        (_stack ??= new()).Push(new Frame { Name = name, Start = Stopwatch.GetTimestamp() });
        return new Scope(true);
    }

    public readonly struct Scope(bool on) : IDisposable
    {
        public void Dispose()
        {
            if (!on) return;
            var f = _stack!.Pop();
            var elapsed = Stopwatch.GetTimestamp() - f.Start;
            if (_stack.Count > 0) _stack.Peek().Child += elapsed;
            lock (Totals) { var t = Totals.GetValueOrDefault(f.Name); Totals[f.Name] = (t.Self + elapsed - f.Child, t.Calls + 1); }
        }
    }

    public static string Report(double wallSeconds)
    {
        List<(string Name, double S, long Calls)> rows;
        lock (Totals) rows = Totals.Select(kv => (kv.Key, kv.Value.Self / (double)Stopwatch.Frequency, kv.Value.Calls)).OrderByDescending(r => r.Item2).ToList();
        var sum = rows.Sum(r => r.S);
        return $"== Profile (exclusive time; wall {wallSeconds:F2}s, scopes {sum:F2}s) ==\n" + string.Join("\n", rows.Select(r =>
            $"{r.Name,-52} {r.S,8:F3}s {100 * r.S / wallSeconds,6:F1}%  {r.Calls,10:N0} calls  {1e6 * r.S / Math.Max(1, r.Calls),9:F1} us/call"));
    }
}
