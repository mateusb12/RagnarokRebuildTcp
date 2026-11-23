using System.Diagnostics;
using RebuildSharedData.Enum;
using RoBotClient.Bot.Behavior;
using RoBotClient.Simulation;

/// <summary>Costs of the server-code simulator: initialisation, entity setup and reset, one attack, one duel, many
/// duels, several bots asking at once, and memory; the old BattleSimulator forecast as the baseline.</summary>
static class SimulatorBenchmark
{
    public static void Run(string serverDirectory, Action<string> print)
    {
        var memoryBefore = GC.GetTotalMemory(true);
        ServerCombatRuntime.EnsureInitialized(serverDirectory);
        print($"init (server data + Script.dll): {ServerCombatRuntime.InitializationTime.TotalMilliseconds:0} ms, " +
              $"retained {(GC.GetTotalMemory(true) - memoryBefore) / 1048576.0:0.0} MiB");

        // A Lv51 Swordsman (one of the live bots' builds) against Giearth (4075), and a Lv30 one against Poring (4000).
        var swordsman = new PlayerBuild(51, 44, 1, 43, 8, 51, 1, 26, 1,
            new Dictionary<CharacterSkill, int> { [CharacterSkill.Bash] = 10, [CharacterSkill.SwordMastery] = 10 },
            new[] { new EquippedItem(EquipSlot.RightHand, 1157, 0, new int[4]), new EquippedItem(EquipSlot.Body, 2316, 0, new int[4]) });
        const int giearth = 4075;

        DuelSimulator.Simulate(new DuelRequest(swordsman, giearth, Trials: 50, Seed: 1)); // JIT
        Time("entity create + reset (Inspect)", 2000, () => DuelSimulator.Inspect(swordsman, giearth), print);
        var one = DuelSimulator.Simulate(new DuelRequest(swordsman, giearth, Trials: 1, Seed: 2));
        Time("one duel (1 trial, includes create + reset)", 2000, () => DuelSimulator.Simulate(new DuelRequest(swordsman, giearth, Trials: 1, Seed: 2)), print);

        var stopwatch = Stopwatch.StartNew();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var many = DuelSimulator.Simulate(new DuelRequest(swordsman, giearth, Trials: 10000, Seed: 3));
        stopwatch.Stop();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var actions = many.Attacks.Mean * many.Attacks.Count; // won trials
        print($"10000 duels: {stopwatch.Elapsed.TotalMilliseconds:0} ms = {stopwatch.Elapsed.TotalMicroseconds / 10000:0.0} us/duel, " +
              $"~{stopwatch.Elapsed.TotalMicroseconds / Math.Max(1, actions):0.00} us per attack action (monster swings included), " +
              $"allocated {allocated / 10000.0:0} B/duel");
        var bash = DuelSimulator.Simulate(new DuelRequest(swordsman, giearth, Trials: 10000, Seed: 3,
            Policy: (in DuelMoment moment) => moment.Sp >= 15 ? new PlayerAction(CharacterSkill.Bash, 10) : PlayerAction.AutoAttack));
        print($"10000 duels with Bash: {bash.Elapsed.TotalMilliseconds:0} ms");

        // Several bots asking for a 300-trial forecast at once: the global gate serialises them.
        foreach (var bots in new[] { 1, 4, 16 })
        {
            var latencies = new double[bots];
            stopwatch.Restart();
            Parallel.For(0, bots, new ParallelOptions { MaxDegreeOfParallelism = bots }, bot =>
            {
                var started = Stopwatch.StartNew();
                DuelSimulator.Simulate(new DuelRequest(swordsman, giearth, Trials: 300, Seed: bot));
                latencies[bot] = started.Elapsed.TotalMilliseconds;
            });
            print($"{bots,2} bots x 300-trial forecast at once: wall {stopwatch.Elapsed.TotalMilliseconds:0} ms, " +
                  $"worst latency {latencies.Max():0} ms, mean {latencies.Average():0} ms");
        }

        // Baseline: the old closed-form forecast with comparable inputs.
        var me = new Combatant(51, 155, 265, 25, 51, 26, 8, 0, 0, 1.122, 1891, 1891);
        var monster = new Combatant(29, 154, 474, 0, 0, 0, 0, 0, 0, 1.872, 2770, 2770);
        Time("old BattleSimulator.Forecast", 100000, () => BattleSimulator.Forecast(me, monster, 1.0, 200), print);
        print($"retained after runs: {(GC.GetTotalMemory(true) - memoryBefore) / 1048576.0:0.0} MiB managed heap; process working set {Environment.WorkingSet / 1048576.0:0} MiB; one-duel sample: {one.Attacks.Mean} attacks");
    }

    private static void Time(string name, int repetitions, Action action, Action<string> print)
    {
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < repetitions; i++) action();
        print($"{name}: {stopwatch.Elapsed.TotalMicroseconds / repetitions:0.0} us");
    }
}
