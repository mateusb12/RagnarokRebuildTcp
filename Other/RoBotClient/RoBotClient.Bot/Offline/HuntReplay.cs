using System.Text.Json;
using RoBotClient.Simulation;
using System.Text.Json.Serialization;

namespace RoBotClient.Bot.Offline;

/// <summary>One offline hunt as recorded while it ran (HuntSimulator.Replay): the map's walk grid, every monster, the
/// player's steps, HP/SP samples and the events with what each decision saw. A viewer replays it; nothing in it is
/// recomputed afterwards. Written by "huntsim ... --replay file.json", read by the dashboard's Simulation tab.</summary>
public sealed class HuntReplay
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public required string Map { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>Row-major (index y * Width + x, world y up), '1' walkable.</summary>
    public required string Walk { get; init; }
    public double Duration { get; init; }
    public int Seed { get; init; }
    public required string Build { get; init; }
    public string? BuildSource { get; set; }
    public required string Config { get; init; }
    public required string SkillScript { get; init; }
    public int MaxHp { get; init; }
    public int MaxSp { get; init; }
    public required List<ReplayMob> Mobs { get; init; }
    public required List<ReplayStep> Track { get; init; }
    public required List<ReplayVitals> Vitals { get; init; }
    /// <summary>Monster walks, compact (thousands per run): [mob id, start s, seconds per cell, start x, start y, steps(, 1)], steps
    /// one digit per cell, an index into HuntSimulator.StepDirs (0 north, clockwise); a diagonal step takes x1.4142. Without
    /// the trailing 1: a planned wander (later events can cut it short); with it: steps the server AI walked, as they happened.</summary>
    public List<object[]>? MobMoves { get; init; }
    public required List<ReplayEvent> Events { get; init; }
    public required ReplaySummary Summary { get; init; }
    public required List<ReplayCapability> Capabilities { get; init; }
    /// <summary>Server code that threw during the run (runtime errors, not missing capabilities): the result is incomplete.</summary>
    public required List<string> Failures { get; init; }
    public required string Report { get; init; }
    public required string Fingerprint { get; init; }
    /// <summary>The simulated build itself (job, equipment), and the character's look when its duel record had one: for drawing it.</summary>
    public PlayerBuild? BuildSpec { get; init; }
    public Appearance? Look { get; set; }
}

public sealed record ReplayMob(int Id, int ClassId, string Name, int Level, int Exp, int MaxHp, bool Pinned, int[] Area, string Respawn, string Forecast);

/// <summary>The player arrived at (X, Y) at T after a step of Dt seconds (0: placed there, e.g. moved during a fight).</summary>
public sealed record ReplayStep(double T, int X, int Y, double Dt);

public sealed record ReplayVitals(double T, int Hp, int Sp);

/// <param name="Mob">The monster it concerns (ReplayMob.Id), 0 none.</param>
/// <param name="X">A cell it concerns (a spawn, a knockback, a ground effect), -1 none.</param>
/// <param name="Detail">What the decision or event saw, kind-specific (candidates, skipped rules, rest fractions...).</param>
public sealed record ReplayEvent(double T, string Kind, string Text, int Mob = 0, int X = -1, int Y = -1, int Amount = 0, int HpLeft = -1,
    string? Skill = null, object? Detail = null);

/// <param name="Level">server: runs the game server's own code; shared: the live bot's own decision code; approximated; absent.</param>
public sealed record ReplayCapability(string Area, string Level, string What, string Implication);

public sealed record ReplaySkill(string Skill, int Level, int Fired, int Sent, int CastBars, int Queued, int Executed, int SpSpent, long Damage,
    int Hits, int Kills, long Healed, Dictionary<string, int> Rejected, Dictionary<string, int> Statuses, int Failures, string? Blocked, bool NoServerHandler);

public sealed record ReplayType(string Name, int Population, int Seen, int Picked, int Killed, long Exp, double FightSecondsPerKill, string Forecast,
    Dictionary<string, int> Rejected);

public sealed record ReplaySummary(int Kills, int Deaths, long Exp, double ExpPerHour, double ExpPerHourNotSitting, double KillsPerHour,
    double WanderSeconds, double ApproachSeconds, double FightSeconds, double RestSeconds, double IdleSeconds, double NoHuntableAliveSeconds,
    double DamageTaken, long RegenHp, long RegenSp, int Rests, int RestsToWin, int SitRefusals, int FightsEndedUnderHealHp,
    int Steps, int PickDecisions, int Picks, Dictionary<string, int> Rejections, double SearchSecondsPerKill, double FightSecondsPerKill,
    List<ReplaySkill> Skills, Dictionary<string, int> RuleSkips, List<ReplayType> Types);
