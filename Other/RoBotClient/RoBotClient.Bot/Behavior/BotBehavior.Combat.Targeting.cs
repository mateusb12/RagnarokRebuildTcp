using RebuildSharedData.ClientTypes;
using RebuildSharedData.Data;
using RebuildSharedData.Enum;
using RoBotClient.Bot.GameData;
using RoBotClient.Bot.Session;
using RoBotClient.Bot.State;

namespace RoBotClient.Bot.Behavior;

public sealed partial class BotBehavior
{
    private readonly HashSet<int> _unreachable = new();

    // One duel = Engaging a target until our kill of it, summarised in the log on the tick after the kill.
    private sealed record DuelStart(DateTime At, int Hp, int Sp, string Name);
    private DuelStart? _duel;
    private readonly object _duelLock = new();
    private (ExpUpdate Update, DateTime At)? _duelKill;

    private void LogDuelResult(Snapshot snap)
    {
        ExpUpdate update;
        lock (_duelLock)
        {
            if (_duelKill is not { } kill) return;
            update = kill.Update;
            _duelKill = null;
        }
        var duel = _duel;
        _duel = null;
        if (duel == null) return;
        var job = $"+{update.JobGained:N0} job exp";
        var hpLost = update.HpLost ?? Math.Max(0, duel.Hp - snap.SelfHp);
        OnLog?.Invoke($"Duel won: {(update.Monster.Length > 0 ? update.Monster : duel.Name)} in {(DateTime.UtcNow - duel.At).TotalSeconds:F1}s — " +
                      $"+{update.Gained:N0} base exp, {job}, HP -{hpLost}, SP -{Math.Max(0, duel.Sp - snap.SelfSp)}.");
    }

    // Close enough that it's on us rather than just around: a monster chasing a walking bot stays within a few cells.
    private const int DefendRadius = 5;
    // ponytail: guessed share; re-fit from the "it's chasing me" engagements' HP lost once there are some.
    private const double DefendDangerShare = 0.3;

    /// <summary>Off the hunt (shop trip, or traveling to the hunting map): keep fighting a target that's on us, or
    /// engage the nearest winnable monster that has hit us or is an aggressive one at melee range. False when
    /// there's nothing to defend against, so the trip goes on.</summary>
    private async Task<bool> DefendAsync(Snapshot snap, CancellationToken ct)
    {
        var offHunt = ShopTripActive || (!string.IsNullOrEmpty(_config.HomeMap)
            && !OnHomeMap(snap.Map));
        if (!offHunt) return false;
        // A fixed monster (Flora, Mandragora) can't follow: walking on IS the defense. Turning on one made Bot5 (archer,
        // 71 HP) stand in Flora's reach on mjolnir_11 trying to "defend" and die to its next hit.
        bool OnMe(MonsterInfo m) => IsHuntable(m) && Dist(snap.SelfPos, m.Pos) <= DefendRadius
            && !IsMonsterImmobile(_data?.Monster(m.ClassId))
            && (_bot.HasHitMe(m.Id) || IsAggressive(m.ClassId) && Dist(snap.SelfPos, m.Pos) <= 2);
        var chasers = snap.Monsters.Where(OnMe).ToList();
        if (chasers.Count == 0) return false;
        // How dangerous the tail is for THIS character: what fighting all of it would cost. A high-flee thief drags
        // several and loses little, so it keeps walking; a merchant turns before the pack gets past what it can win.
        var pack = ForecastGroupOf(snap, chasers);
        if (pack.CanWin && pack.TotalDamageTaken < snap.SelfHp * DefendDangerShare) return false;
        if (_targetId != 0 && chasers.Any(c => c.Id == _targetId)) return true; // already fighting one of them
        // Quickest kill first: it sheds incoming damage soonest (as ForecastGroup orders them).
        var pick = chasers.Select(m => (m, f: WithHealing(Forecast(snap, m), m.MaxHp, HealingOn(snap, m))))
            .Where(x => x.f.CanWin).OrderBy(x => x.f.SecondsToKillMonster).FirstOrDefault();
        if (pick.m == null) return false; // none winnable alone: hazard avoidance flees them
        await EngageAsync(snap, pick.m, pick.f, $", it's chasing me with {chasers.Count - 1} more (the pack would cost ~{pack.TotalDamageTaken:F0} of my {snap.SelfHp} HP).", ct);
        return true;
    }

    // The server drops the attack lock now and then (and during every walk), so a running fight re-sends Attack this often.
    private static readonly TimeSpan AttackResend = TimeSpan.FromSeconds(3);

    /// <summary>Sends Attack on <paramref name="targetId"/>: right away when <paramref name="now"/>, otherwise only once the
    /// re-send is due.</summary>
    private async Task SendAttackAsync(int targetId, bool now, CancellationToken ct)
    {
        // NoAutoAttack: pure casters (Mage / Wizard / skill-only builds) skip the auto re-assert.
        // The skill script is the entire combat loop in that mode. Without an auto-attack the bot
        // also can't waste its swing animation on a wrong-element target while the skill cooldown
        // ticks. Skills, heals, walking, looting still work the same.
        if (_config.NoAutoAttack || (!now && DateTime.UtcNow < _nextAttack)) return;
        _nextAttack = DateTime.UtcNow + AttackResend;
        await _bot.AttackAsync(targetId, ct);
    }

    private async Task EngageAsync(Snapshot snap, MonsterInfo target, BattleForecast forecast, string why, CancellationToken ct)
    {
        _targetId = target.Id;
        _targetClass = target.ClassId;
        TargetName = target.Name;
        Mode = BotMode.Hunting;
        ResetStuck(snap.SelfPos, target.Hp); // start the progress clock for this target
        _bot.NoteForecast(target.Id, forecast.SecondsToKillMonster, forecast.AttacksToKill, forecast.ExpectedDamageTaken);
        RecordDuelStart(snap, target, forecast);
        var source = _config.ServerForecast == ServerForecastMode.On ? $" [forecast: {_decision?.Source ?? "old, not a PickTarget decision"}]" : "";
        _decision = null;
        _nextAttack = DateTime.UtcNow + AttackResend;
        _duel = new DuelStart(DateTime.UtcNow, snap.SelfHp, snap.SelfSp, target.Name);
        OnLog?.Invoke($"Engaging {target.Name} (hp {target.MaxHp}): my hit ~{forecast.MyDamagePerHit}@{forecast.MyHitChance:P0}, " +
                      $"its hit ~{forecast.MonsterDamagePerHit}@{forecast.MonsterHitChance:P0}, " +
                      $"kill ~{forecast.SecondsToKillMonster:F1}s vs my death ~{forecast.SecondsToKillMe:F1}s{why}{source}");
        await MaybeAnnounceTargetAsync(target, ct);
        // Skill-only bots still need the server to know who their target is (skills auto-target the
        // bot's current target id when no explicit target is passed in the rule). Just don't issue the
        // basic attack — the next tick's TickSkillsAsync will fire the right rule.
        // A fixed monster is shot from a safe cell; the next tick walks there first (the server's attack-walk would stop
        // at the first in-range cell, maybe inside another plant's reach).
        var walkFirst = SnipesFixed(snap, target) && FindFiringSpot(snap, target) is { } spot && (spot.X != snap.SelfPos.X || spot.Y != snap.SelfPos.Y);
        if (!walkFirst)
            await SendAttackAsync(target.Id, now: true, ct);
    }

    /// <summary>One tick against the committed target: abandon it when stuck, otherwise position, cast, kite or attack.</summary>
    private async Task TickEngagedAsync(Snapshot snap, MonsterInfo current, bool stuck, CancellationToken ct)
    {
        if (stuck)
        {
            OnLog?.Invoke($"Can't reach '{current.Name}' — abandoning it as unreachable.");
            _unreachable.Add(_targetId);
            ClearTarget();
            ResetStuck(snap.SelfPos);
            await NudgeAsync(snap, ct);
            return;
        }
        Mode = BotMode.Hunting;
        TargetName = current.Name;
        if (await HoldFiringSpotAsync(snap, current, ct)) return; // fixed monster: get to a safe firing cell first
        if (await WalkAroundToTargetAsync(snap, current, ct)) return; // behind a wall: server won't path there
        if (await TickSkillsAsync(snap, current, ct)) return; // a skill rule fired — skip the basic attack this tick
        // Kite sub-FSM: ranged classes with EnableKite set get positional management here. If it
        // owned the tick (stepped / fired / held), we're done; otherwise fall through to the
        // melee-style attack re-assert below. The predicate inside ShouldKiteThisTarget gates all
        // negative-space cases (melee, tank, stationary mob, equal-range mob, etc.).
        if (await TryKiteEngagementAsync(snap, current, ct)) return;
        await SendAttackAsync(_targetId, now: false, ct); // re-assert in case the server dropped the attack lock
        return;
    }

    private string _lastIdleReason = "";
    private string _idleSummary = "";

    /// <summary>Expected base exp per second of walking to, killing and then sitting off the HP lost to
    /// <paramref name="m"/>, solo. The server pays a solo killer the monster's full base exp (no level-gap
    /// penalty), so this is the database exp over walk + forecast kill time + rest time, which makes an
    /// HP-draining fight cost what it really costs: the sitting it forces later.</summary>
    private double ExpPerSecond(Snapshot snap, MonsterInfo m, BattleForecast f, int distance)
    {
        var exp = _data?.Monster(m.ClassId)?.Exp ?? 0;
        return HuntPolicy.ExpPerSecond(exp, f.SecondsToKillMonster, distance, _bot.SelfSecondsPerCell, RestSeconds(snap, f));
    }

    /// <summary>Seconds of sitting to regenerate the HP the forecast says this fight costs (HuntPolicy.RestSeconds).</summary>
    private static double RestSeconds(Snapshot snap, BattleForecast f) => HuntPolicy.RestSeconds(snap.SelfMaxHp, snap.SelfVit, f.ExpectedDamageTaken);

    /// <summary>The live side of HuntPolicy.PickTarget (the same rules the offline HuntSimulator picks by), plus the
    /// bookkeeping only live has: the server-forecast decision record, the rest-to-win flag and the idle reason.</summary>
    private MonsterInfo? PickTarget(Snapshot snap, out BattleForecast chosen)
    {
        _decision = null;
        // ServerForecast On: the nearest few are decided by the server forecast when it's ready; the shadow pick is
        // what the old forecast alone would choose, to count where the two pick differently.
        var shortlist = ServerShortlist(snap);
        var world = new LiveTargets(this, snap, shortlist);
        var pick = HuntPolicy.PickTarget(snap.Monsters, world, _config, _data, snap.Map, wantShadow: shortlist != null);
        chosen = pick.Forecast.Value;
        if (shortlist != null) NoteServerPick(pick.Target, pick.ShadowTarget);
        if (pick.Target is { } target)
        {
            if (shortlist != null && world.Sources[target.Id] is var (source, old))
                _decision = (source == "old" ? "old: not shortlisted" : source, old);
            NotePostFleePick(snap, target);
            _restToWin = false;
            _lastIdleReason = ""; // armed for the next idle window
            _idleSummary = "";
            return target;
        }

        // No target. Log WHY (deduped — only fire when the reason string actually changes), so the user can
        // diagnose "FSM keeps the bot Idle" without enabling Verbose chat.
        _restToWin = world.WinnableRested > 0;
        int N(TargetReject r) => world.Rejects[(int)r];
        int notHuntable = N(TargetReject.NotHuntable), avoided = N(TargetReject.AvoidedClass), blacklisted = N(TargetReject.Blacklisted),
            hazardous = N(TargetReject.Hazard), portalRisk = N(TargetReject.NearPortal), unwinnable = N(TargetReject.TooStrong),
            swarmed = N(TargetReject.Swarm), noSafeCell = N(TargetReject.NoSafeCell), unreachable = N(TargetReject.Unreachable);
        var reason = $"PickTarget idle — {snap.Monsters.Count} mon in view (not-huntable={notHuntable}, avoided-class={avoided}, blacklisted={blacklisted}, in-hazard={hazardous}, near-portal={portalRisk}, unwinnable={unwinnable}, group-swarm={swarmed}, no-safe-cell={noSafeCell}, unreachable={unreachable}).";
        // Main reason for the stay log (time split): which filter dropped the most monsters in view.
        _idleCause = new (string Why, int Count)[] { ("not huntable", notHuntable), ("avoided class", avoided), ("blacklisted", blacklisted),
                ("near a dangerous monster", hazardous), ("near a portal", portalRisk), ("too strong", unwinnable), ("would pull a group", swarmed),
                ("no safe cell", noSafeCell), ("unreachable", unreachable) }
            .MaxBy(c => c.Count) is { Count: > 0 } top ? top.Why : "none in view";
        _idleSummary = snap.Monsters.Count == 0 ? "none in view"
            : hazardous > 0 ? $"{hazardous} near a dangerous monster"
            : unwinnable > 0 ? $"{unwinnable} too strong"
            : noSafeCell > 0 ? $"{noSafeCell} fixed, no safe cell to shoot from"
            : $"{snap.Monsters.Count} in view, none huntable";
        if (reason != _lastIdleReason)
        {
            _lastIdleReason = reason;
            OnLog?.Invoke(reason);
        }
        return null;
    }

    /// <summary>What HuntPolicy.PickTarget reads live: the session snapshot, the death-avoided classes (time-decayed on
    /// the wall clock), the unreachable blacklist, hazards, the hand-formula or server forecast, fixed-monster firing
    /// spots, group pulls and the hazard/portal-weighted A*.</summary>
    private sealed class LiveTargets(BotBehavior b, Snapshot snap, HashSet<int>? shortlist) : ITargetWorld<MonsterInfo, BattleForecast>
    {
        public readonly int[] Rejects = new int[Enum.GetValues<TargetReject>().Length];
        public int WinnableRested; // too strong now, winnable at full HP: rest first
        public readonly Dictionary<int, (string Source, BattleForecast Old)> Sources = new();

        public TargetSelf Self => new(!b._bot.InParty, snap.SelfLevel, snap.SelfMaxHp, snap.SelfVit, b._bot.SelfSecondsPerCell);

        public TargetObservation Observe(MonsterInfo m)
        {
            var db = b._data?.Monster(m.ClassId);
            return new(m.ClassId, db?.Exp ?? 0, db?.Level ?? 0, m.Hp, m.MaxHp, m.Pos.X, m.Pos.Y, Dist(snap.SelfPos, m.Pos));
        }

        public TargetReject? Excluded(MonsterInfo m)
        {
            // Time-decayed avoidance: drop the entry once it's older than AvoidClassDuration so a fluke
            // death doesn't permanently exclude a mob from the rotation.
            if (b._avoidClasses.TryGetValue(m.ClassId, out var avoidUntil))
            {
                if (DateTime.UtcNow >= avoidUntil) b._avoidClasses.Remove(m.ClassId);
                else return TargetReject.AvoidedClass;
            }
            if (b._unreachable.Contains(m.Id)) return TargetReject.Blacklisted;
            // In or near an aggressive monster's zone, or the walk there crosses one.
            if (b.ApproachNearHazard(snap, m.Pos, out var hzHit, out var hzWhere)) { b.NoteHazardReject(snap, m, hzHit, hzWhere); return TargetReject.Hazard; }
            return null;
        }

        public (TargetForecast<BattleForecast> Decision, TargetForecast<BattleForecast> Shadow)? Forecast(MonsterInfo m)
        {
            var old = WithHealing(b.Forecast(snap, m), m.MaxHp, b.HealingOn(snap, m));
            var source = "old";
            var forecast = shortlist?.Contains(m.Id) == true ? b.DecisionForecast(snap, m, old, "pick", out source) : old;
            Sources[m.Id] = (source, old);
            return (View(forecast), View(old));
        }

        private static TargetForecast<BattleForecast> View(BattleForecast f) => new(f, f.CanWin, f.SecondsToKillMonster, f.ExpectedDamageTaken);

        public bool NoSafeCell(MonsterInfo m) => b.SnipesFixed(snap, m) && b.FindFiringSpot(snap, m) == null;

        // One already hitting us isn't a pull: idling leaves it (and the pack) on us, so fight it if it's winnable 1v1.
        public bool PullsLosingGroup(MonsterInfo m)
        {
            var group = b.EngagementGroup(snap, m);
            return group.Count > 1 && !b._bot.HasHitMe(m.Id) && !b.ForecastGroupOf(snap, group).CanWin;
        }

        // Checking reachability up front avoids committing to monsters behind walls and waiting on the stuck timeout.
        public bool Reachable(MonsterInfo m) => b.IsReachable(snap, m.Pos);

        public void Rejected(MonsterInfo m, TargetReject why)
        {
            Rejects[(int)why]++;
            if (why != TargetReject.TooStrong || snap.SelfHp >= snap.SelfMaxHp) return;
            var hp = snap.SelfHp;
            snap.SelfHp = snap.SelfMaxHp;
            if (WithHealing(b.Forecast(snap, m), m.MaxHp, b.HealingOn(snap, m)).CanWin) WinnableRested++;
            snap.SelfHp = hp;
        }
    }

    // True if the bot can walk to a cell next to the target (bounded A* over the walkmap). With no walkmap
    // loaded we optimistically defer to the server's own pathing.
    private bool IsReachable(Snapshot snap, Position target)
    {
        var map = _data?.GetWalkMap(snap.Map);
        if (map == null) return true;
        if (!map.TryFindWalkableNear(target.X, target.Y, 4, out var gx, out var gy)) return false;
        // Reachability check uses the danger overlay too — a target that's only reachable through a kill-
        // you mob's aggro should be treated as effectively unreachable for safety.
        var path = map.FindPath(snap.SelfPos.X, snap.SelfPos.Y, gx, gy, 6000, MakePathCost(snap.Map));
        return path != null && HuntPolicy.DetourAcceptable(path.Count - 1, Dist(snap.SelfPos, target));
    }

    private bool IsHuntable(MonsterInfo m) => HuntPolicy.IsHuntable(_config, m.ClassId, m.Hp, m.MaxHp);
}
