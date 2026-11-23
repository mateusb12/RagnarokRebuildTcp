using RebuildSharedData.Enum;

namespace RoBotClient.Simulation;

public enum DuelOutcome
{
    Won,        // the target died with our damage on it
    Died,       // we died during the duel
    Abandoned,  // we engaged something else, or stopped attacking it, before it died
    LostSight,  // the target left our view alive
    Stolen,     // the target died without any of our damage on it
}

/// <summary>One attack action as the server broadcast it (Attack or Skill packet). Damage is the total the action
/// deals (per-hit damage x hits + off-hand), exactly as the server computed it; 0 for misses.</summary>
public sealed record ObservedAction(double At, CharacterSkill Skill, int Level, AttackResult Result, int HitCount, int Damage);

/// <summary>The stats the live server sent the bot (UpdatePlayerData) when the duel started: the reference the
/// offline reconstruction of the same build is checked against.</summary>
public sealed record LiveStats(int MaxHp, int MaxSp, int AttackMin, int AttackMax, int Def, int MDef, int Hit, int Flee,
    int Crit, int PerfectDodge, double AttackSpeed);

/// <summary>What a forecast predicted for this duel, in the observation's units.</summary>
public sealed record ForecastSummary(double WinRate, double Attacks, double Seconds, double DamageTaken,
    double AttacksStdDev = 0, double SecondsStdDev = 0, double DamageTakenStdDev = 0, double DamagePerAction = 0,
    string? Note = null);

/// <summary>
/// One duel: the bot's combat state when it engaged a monster, what the old and the server-based forecasts
/// predicted, and what happened on the server. Times are seconds since the engagement. Written by the bot
/// (BotSession) for every engagement, whatever its outcome; read by <see cref="DuelValidator"/>.
/// </summary>
public sealed class DuelRecord
{
    public DateTime EngagedAtUtc { get; set; }
    public string Character { get; set; } = "";
    public string Map { get; set; } = "";
    public int MonsterId { get; set; }
    public int MonsterClassId { get; set; }
    public string MonsterName { get; set; } = "";
    public int MonsterHpAtEngage { get; set; }
    public int MonsterMaxHpAtEngage { get; set; }
    public int HpAtEngage { get; set; }
    public int SpAtEngage { get; set; }
    public bool InParty { get; set; }
    public List<string> SelfStatuses { get; set; } = new();
    /// <summary>The bot's enemy-targeted skill rules, as written in its script (what the policy can use).</summary>
    public List<string> SkillRules { get; set; } = new();
    public PlayerBuild? Build { get; set; }
    public LiveStats? Live { get; set; }

    public List<ObservedAction> Actions { get; set; } = new();
    /// <summary>The target's actions on us (Attack/Skill packets), misses included.</summary>
    public List<ObservedAction> TargetActions { get; set; } = new();
    /// <summary>Total damage other monsters dealt us during the duel.</summary>
    public int DamageFromOthers { get; set; }
    public int OtherAttackers { get; set; }
    /// <summary>Another player attacked the target during the duel.</summary>
    public bool HelpedByPlayer { get; set; }
    /// <summary>A status effect landed on us or the target during the duel (poison, stun, buffs...).</summary>
    public List<string> StatusChanges { get; set; } = new();

    public DuelOutcome Outcome { get; set; }
    public double EndedAt { get; set; }

    public ForecastSummary? OldForecast { get; set; }
    public ForecastSummary? ServerForecast { get; set; }
    public string? ServerForecastError { get; set; }

    public double? FirstActionAt => Actions.Count > 0 ? Actions[0].At : null;
}
