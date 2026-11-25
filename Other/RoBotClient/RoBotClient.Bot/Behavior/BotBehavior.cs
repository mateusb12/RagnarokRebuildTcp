using RebuildSharedData.ClientTypes;
using RebuildSharedData.Data;
using RebuildSharedData.Enum;
using RoBotClient.Bot.GameData;
using RoBotClient.Bot.Session;
using RoBotClient.Bot.State;

namespace RoBotClient.Bot.Behavior;

public enum BotMode { Idle, Hunting, Dead, Traveling, Looting, Fleeing, Shopping, JobChange, Parked, Resting, Following }

/// <summary>
/// Tick-based brain. It does NOT flee: before engaging it runs a <see cref="BattleSimulator"/> forecast
/// from the player's stats and the monster database, and only attacks monsters it projects it can kill.
/// Once engaged it commits to that target until it dies. If the bot itself dies (a fight the forecast got
/// wrong, or a swarm), it marks that monster class as off-limits, respawns, and resumes. With nothing
/// winnable in view it roams to find some.
/// </summary>
public sealed partial class BotBehavior
{
    private readonly BotSession _bot;
    private readonly BotBehaviorConfig _config;
    private readonly GameDatabase? _data;
    private readonly Random _rng = new();

    private int _targetId;
    private int _targetClass = -1;
    private DateTime _nextAttack = DateTime.MinValue;
    private BotMode _lastAnnouncedMode = BotMode.Idle;

    // Stuck failsafe: "progress" = moving OR the current target losing HP. No progress for StuckSeconds
    // means the target/route is unreachable (e.g. behind a wall), so we abandon it and relocate.
    private Position _lastProgressPos;
    private DateTime _lastProgressAt = DateTime.UtcNow;
    private int _lastTargetHp = -1;
    private string _lastMap = "";

    public BotMode Mode { get; private set; } = BotMode.Idle;
    public int TargetId => _targetId;
    public string TargetName { get; private set; } = "";
    // Synchronization for the (_targetId, _targetClass, TargetName) group when the receive-thread Threat
    // handler writes them concurrently with the tick loop. Cheap monitor-only lock — no async work inside.
    internal readonly object _targetLock = new();

    /// <summary>Wall-clock seconds since the bot last made "progress" (moved or damaged its current target).
    /// Useful at-a-glance signal for wedged bots — anything climbing above StuckSeconds is in or about to
    /// be in the stuck-recover branch. Surfaced in BotSnapshot for `list_bots`.</summary>
    public double IdleSeconds => (DateTime.UtcNow - _lastProgressAt).TotalSeconds;
    public event Action<string>? OnLog;

    public BotBehavior(BotSession bot, BotBehaviorConfig config, GameDatabase? data = null)
    {
        _bot = bot;
        _config = config;
        _data = data;
        SetRestockTarget(config.ButterflyWingItemId, 1); // one is enough: the ride home to sell
        _bot.OnAllyHit += OnAllyHit;
        _bot.OnChatHeard += OnChatHeard;
        _bot.OnBaseExpUpdated += update => { if (update.OwnKill) lock (_duelLock) _duelKill = (update, DateTime.UtcNow); };
    }

    private void ClearTarget()
    {
        _targetId = 0;
        _targetClass = -1;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var tickStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            try { await TickAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { OnLog?.Invoke($"behavior error: {ex.Message}"); }
            RoBotClient.Simulation.ForecastWorker.BotTickMs.Add(System.Diagnostics.Stopwatch.GetElapsedTime(tickStarted).TotalMilliseconds);

            // Tick cadence — every 250ms (was 400). Halves the latency on combat reactions, follower
            // target mirroring, threat switches, hazard avoidance. 6 bots × 4 Hz = 24 ticks/sec total —
            // still cheap, since most ticks are short-circuit returns (e.g. squad followers running the
            // mirror-leader path don't run the heavy Pickup/PickTarget machinery).
            try { await Task.Delay(250, ct); }
            catch { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (!_config.Enabled) return;
        var snap = _bot.WithState(Snapshot.From);
        CountActivityTime(snap);
        LogDuelResult(snap);

        await AnnounceModeAsync(ct);

        // Drain operator-requested ground drops. Done up here so it runs even when the bot is parked /
        // shopping / job-changing — drops aren't gated on autonomous behavior. One drop per tick to
        // give the server room to process and acknowledge.
        await DrainPendingDropAsync(ct);

        // Forget unreachable targets when we change maps.
        if (!string.Equals(snap.Map, _lastMap, StringComparison.OrdinalIgnoreCase))
        {
            var arrivedFrom = _lastMap;
            _lastMap = snap.Map;
            _unreachable.Clear();
            _skipDrops.Clear();
            ResetStuck(snap.SelfPos);
            _cachedLeaderEntityId = 0; // entity ids are per-map; the cached value is stale on the new map
            ResetFollowerLeaderState();
            _lastIdleReason = ""; // log a fresh PickTarget reason on the new map
            // Apron-departure (Phase 1): we just landed on a new map at snap.SelfPos. The cheapest portal
            // from here is almost certainly the back-portal we came through, which would bounce us. Record
            // the landing cell + arrival map so StepTowardMapAsync can force a step-away walk and soft-
            // penalize the back-portal for the next few seconds. Also clear any soft penalty against the
            // portal we just successfully traversed — a one-off trap shouldn't carry bias.
            BeginApronDeparture(snap.SelfPos, arrivedFrom, snap.Map);
            _travelWingMap = null; // a fresh visit gets a fresh Fly Wing budget
            _seenHazards.Clear();
            _serverInFlight.Clear();
            _walkGoal = null;
            OnLog?.Invoke($"Changed maps → {snap.Map}.");
        }

        // Refresh per-map danger when our level moves enough to change which mobs threaten us. Step-based
        // so we don't burn CPU rebuilding on every tick during exp-rich training.
        if (_mapDangerLastLevel < 0 || Math.Abs(snap.SelfLevel - _mapDangerLastLevel) >= DangerLevelStep)
            RecomputeMapDanger(snap);
        var stuck = TrackProgress(snap);

        // Stuck escalation: if NudgeAsync hasn't helped after a sustained no-progress window, try a Fly Wing
        // (and ultimately a Butterfly Wing). Runs before everything else so a wedged bot doesn't sit there
        // attempting hopeless heals/hazard-flees/etc. — but after the snap so we have current data.
        if (stuck && await TryEscapeStuckAsync(snap, ct)) return;

        // Dead -> blacklist the killer's class, stop, and respawn (retry on a cooldown). Never flee.
        if (snap.SelfDead || (snap.SelfMaxHp > 0 && snap.SelfHp <= 0)) { await HandleDeathAsync(snap, ct); return; }

        LearnSavePoint(snap);

        // Item-rule DSL goes first: when the user configured an ItemScript, it takes priority over the
        // hard-coded HealHpPercent logic. The script can fire a potion, Fly Wing, etc. and signal "done
        // for this tick". When ItemScript is empty, this is a no-op and MaybeHealAsync handles potions.
        if (await TickItemRulesAsync(snap, ct)) return;

        // Heal mid-anything (never when dead — handled above) before deciding what to do next.
        await MaybeHealAsync(snap, ct);
        await MaybeDrinkBuffAsync(snap, ct);

        // Steer clear of aggressive monsters the forecast says we'd lose to: if one's aggro range
        // already covers us, drop everything and flee away from it.
        NoteHp(snap);
        ComputeHazards(snap);
        PrefetchServerForecasts(snap);
        if (await AvoidHazardsAsync(snap, ct)) return;
        if (await TryEmergencyFlyWingAsync(snap, ct)) return;
        if (await TryEscapeWallAsync(snap, stuck, ct)) return;

        // Chat-driven leader commands (!engage / !flee / !hold / !regroup / !stop / !hunt / !follow).
        // Runs AFTER real-hazard avoidance — a !engage shouldn't pull a follower into an aggressive
        // monster they can't fight — but BEFORE ally rules / shopping / hunting, so the leader's
        // instructions take priority over default behavior.
        if (await TickPartyCommandAsync(snap, ct)) return;

        // User-queued skill casts from MCP queue_skill — highest priority among skill paths so an agent's
        // explicit plan beats any script-driven cast that tick. Fire-and-forget; doesn't preempt the tick.
        await TickQueuedSkillsAsync(snap, ct);

        // Ally support (#7): heal/buff visible party members per the bot's skill script before doing anything
        // else, so a hurt ally gets attention even if we're mid-shopping or hunting.
        if (await TickAllyRulesAsync(snap, ct)) return;

        // A manual "go here and wait" order overrides everything except fleeing danger.
        if (_parkActive) { await TickParkAsync(snap, stuck, ct); return; }

        // Spend stat/skill points per the build (StatTargets / SkillOrder). One point per tick, never blocks.
        await TickBuildAsync(ct);
        await TickGearAsync(snap, ct);
        TickMapChoice(snap);

        // A pending job change (a milestone) takes priority over shopping and hunting.
        if (JobChangeActive) { await TickJobChangeAsync(snap, stuck, ct); return; }
        if (await TryStartJobChangeAsync(snap, stuck, ct)) return;

        // A shopping trip (restock potions / sell junk) takes priority over hunting and home-travel —
        // UNLESS this bot is a squad follower, in which case the leader drives the schedule and we don't
        // want a Merchant to peel off to town mid-fight just because its weight crossed the threshold.
        // Forced shop trips (_forceShop, set by MCP) still go through; the gate only blocks the AUTO
        // trigger so a follower won't auto-detach. A squad leader / solo bot keeps the legacy behavior.
                // Shopping or traveling the bot doesn't hunt, so winnable monsters chasing it pile up into a pack it can't
        // beat. Kill what's on it first (the target block below fights it), then the trip resumes.
        var defending = await DefendAsync(snap, ct);
        if (!defending && ShopTripActive) { await TickShopTripAsync(snap, stuck, ct); return; }
        if (!defending && (_forceShop || (!IsSquadFollower && ShouldStartShopTrip(snap))))
        {
            StartShopTrip(snap, _forceShop);
            _forceShop = false;
            if (ShopTripActive) { await TickShopTripAsync(snap, stuck, ct); return; }
        }

        // Out of healing items and hurt? Sit to recover (when able + safe) instead of fighting without sustain.
        if (!defending && await TickRestAsync(snap, ct)) return;

        // Follower mode: in a party, NOT the leader, leader visible — mirror leader's target / stay close.
        // When the leader isn't in view this returns false and we fall through to normal travel/wander.
        if (!defending && await TickAsFollowerAsync(snap, ct)) return;

        // Respawning far from the hunting map? Re-save at a Kafra closer to it first.
        if (!defending && await TickSavePointAsync(snap, stuck, ct)) return;

        // Travel to the hunting map if we're elsewhere (e.g. respawned in town).
        if (!defending && !string.IsNullOrEmpty(_config.HomeMap) &&
            !OnHomeMap(snap.Map))
        {
            Mode = BotMode.Traveling;
            ClearTarget();
            if (stuck) { ResetStuck(snap.SelfPos); await NudgeAsync(snap, ct); return; } // blocked en route
            await TravelAsync(snap, ct);
            return;
        }

        // Commit to the current target until it dies or leaves view.
        var current = _targetId != 0 ? FindMonster(snap, _targetId) : null;
        if (current != null && IsHuntable(current)) { await TickEngagedAsync(snap, current, stuck, ct); return; }

        // Grab nearby drops before seeking a new target (we aren't committed to a live target here).
        if (await TickLootAsync(snap, stuck, ct)) return;

        // Pick the nearest monster the battle forecast says we can win against.
        var target = PickTarget(snap, out var forecast);
        if (target == null)
        {
            Mode = BotMode.Idle;
            ClearTarget();
            await WanderAsync(snap, ct);
            return;
        }

        await EngageAsync(snap, target, forecast, _bot.InParty ? "." : $", ~{ExpPerSecond(snap, target, forecast, Dist(snap.SelfPos, target.Pos)):F2} exp/s incl. walk + {RestSeconds(snap, forecast):F0}s rest (solo, best in view).", ct);
    }

    // Stuck detection: moving, or the current target losing HP, counts as progress.
    private bool TrackProgress(Snapshot snap)
    {
        var targetHpNow = _targetId != 0 ? (FindMonster(snap, _targetId)?.Hp ?? -1) : -1;
        // Healed back up (a Vitata healing an Ant Egg): measure progress from the new HP, not the old low, or the next
        // hits look like no progress and the target is dropped as "unreachable", then the next one, and so on.
        if (targetHpNow > _lastTargetHp)
        {
            // ponytail: log-only until heals are measured; Vitata (Heal 5, 5.5 s) isn't in Healers because the formula says it
            // out-heals Bot5 (56 vs 44 HP/s), yet its Ant Eggs died in ~20 s. These lines say how much really lands.
            if (_lastTargetHp >= 0 && FindMonster(snap, _targetId) is { } healed)
                OnLog?.Invoke($"Target '{healed.Name}' healed {_lastTargetHp} → {targetHpNow}/{healed.MaxHp} HP; in view: "
                              + string.Join(", ", snap.Monsters.Where(m => m.Id != healed.Id && Dist(m.Pos, healed.Pos) <= 9).GroupBy(m => m.Name).Select(g => $"{g.Count()} {g.Key}")));
            _lastTargetHp = targetHpNow;
        }
        if (snap.SelfPos.X != _lastProgressPos.X || snap.SelfPos.Y != _lastProgressPos.Y ||
            (targetHpNow >= 0 && targetHpNow < _lastTargetHp))
        {
            _lastProgressPos = snap.SelfPos;
            if (targetHpNow >= 0) _lastTargetHp = targetHpNow;
            _lastProgressAt = DateTime.UtcNow;
        }
        return (DateTime.UtcNow - _lastProgressAt).TotalSeconds > _config.StuckSeconds;
    }

    /// <summary>One-line description of what the bot is doing right now, for the dashboard and map chat.</summary>
    public string Activity => Mode switch
    {
        BotMode.Hunting => string.IsNullOrEmpty(TargetName) ? "Hunting" : $"Hunting {TargetName}",
        BotMode.Traveling => string.IsNullOrEmpty(_config.HomeMap) ? "Traveling"
            : $"Traveling to {_data?.MapName(_config.HomeMap) ?? _config.HomeMap}" + (_shopPhase == ShopPhase.ReturnTravel ? " after shopping" : ""),
        BotMode.Dead => "Dead, respawning",
        BotMode.Fleeing => "Fleeing danger",
        BotMode.Shopping => (_shopPhase == ShopPhase.Travel
            ? $"Heading to {_shopNpcName} in {_data?.MapName(_shopMap) ?? _shopMap}"
            : $"At {_shopNpcName}") + (_shopPurpose.Length > 0 ? $" to {_shopPurpose}" : ""),
        BotMode.JobChange => $"Changing job to {JobName(_config.DesiredJobId)}",
        BotMode.Parked => "Holding position (ordered)",
        BotMode.Resting => "Sitting to recover HP/SP",
        BotMode.Following => "Following the party leader",
        BotMode.Looting => "Picking up loot",
        _ => string.IsNullOrEmpty(_idleSummary) ? "Looking for targets" : $"Looking for targets: {_idleSummary}",
    };

    private void ResetStuck(Position pos, int targetHp = -1)
    {
        _lastProgressPos = pos;
        _lastProgressAt = DateTime.UtcNow;
        _lastTargetHp = targetHp;
    }

    // Verbose toggle: announce FSM state changes in map chat, once per transition.
    private async Task AnnounceModeAsync(CancellationToken ct)
    {
        if (!_config.Verbose || Mode == _lastAnnouncedMode || Mode == BotMode.Looting) return;
        _lastAnnouncedMode = Mode;
        var msg = Activity + ".";
        await _bot.SayAsync(msg, ct);
    }
}
