using RoBotClient.Simulation;

namespace RoBotClient.Bot.Behavior;

/// <summary>The hunting decision rules that don't need a live session, shared by BotBehavior (PickTarget, IsReachable,
/// the server-forecast CanWin, the map estimate) and the offline HuntSimulator, so both decide the same way.</summary>
public static class HuntPolicy
{
    // ponytail: guessed cap on simulated deaths + timeouts for a fight without potions to count as winnable; the
    // WinMargin damage check bounds the mean, this bounds the tail. Re-fit from On-mode deaths if any appear.
    public const double MaxServerLossRate = 0.05;

    /// <summary>Solo target score: base exp per second of walking up, killing and sitting off the HP lost.</summary>
    public static double ExpPerSecond(int exp, double secondsToKill, int distance, double secondsPerCell, double restSeconds) =>
        exp / Math.Max(0.1, secondsToKill + distance * secondsPerCell + restSeconds);

    /// <summary>Seconds of sitting to regenerate <paramref name="damage"/> HP. Mirrors the server's Player.HpRegenTick:
    /// (MaxHp/50 + Vit/5)*(200+Vit)/200 per tick, a tick every 3 s sitting and doubled.
    /// ponytail: ignores potions, AddHpRecoveryPercent and Increased HP Recovery, so it overstates the cost
    /// for bots with those; fold them in if such bots start avoiding fights they'd win cheaply.</summary>
    public static double RestSeconds(int maxHp, int vit, double damage)
    {
        var perTick = Math.Max(1, (maxHp / 50 + vit / 5) * (200 + vit) / 200);
        return damage / (perTick * 2 / 3.0);
    }

    /// <summary>A target is worth walking to when the A* walk isn't a long loop around a wall: a mob across a wall is
    /// in view but the walk loops away from it (Bot2 ping-ponged with a Stainer 60-76 cells of walk away).</summary>
    public static bool DetourAcceptable(int pathSteps, int straightDistance) => pathSteps <= Math.Max(30, 2.5 * straightDistance);

    /// <summary>The old CanWin rule on a server prediction: expected HP lost within WinMargin of HP plus the potion
    /// buffer, attacks within MaxRoundsToKill, and without potions simulated deaths and timeouts capped too.</summary>
    public static bool ServerCanWin(DuelPrediction p, int hp, double potionBuffer, double winMargin, int maxRoundsToKill) =>
        p.WinRate > 0 && hp > 0
        && (p.Attacks.Mean <= maxRoundsToKill || p.DamageTaken.Mean <= 0)
        && p.DamageTaken.Mean <= winMargin * (hp + potionBuffer)
        && (potionBuffer > 0 || p.DeathRate + p.TimeoutRate <= MaxServerLossRate);

    /// <summary>The map estimate's search model: walking to the next target takes about the spacing between huntable
    /// monsters spread evenly over the area, at the base player walk.</summary>
    public static double MapSearchSecondsPerKill(int areaCells, double huntableCount) => 0.15 * Math.Sqrt(areaCells / huntableCount);
}
