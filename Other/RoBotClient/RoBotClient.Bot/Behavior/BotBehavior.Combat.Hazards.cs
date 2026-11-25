using RebuildSharedData.ClientTypes;
using RebuildSharedData.Data;
using RebuildSharedData.Enum;
using RoBotClient.Bot.GameData;
using RoBotClient.Bot.Session;
using RoBotClient.Bot.State;

namespace RoBotClient.Bot.Behavior;

// hazard avoidance: stay clear of aggressive monsters the forecast says we'd lose to.
public sealed partial class BotBehavior
{
    private readonly List<(Position pos, int radius, string name, string engine, int id)> _hazards = new();

    // Per-map static danger score, relative to THIS bot's level. Built from MonsterDb.Spawns; higher
    // means "more / stronger aggressive mobs spawn here vs my level." Cross-map routing adds this along
    // the path so a level-15 bot detours around moc_fild04 (level-50 mobs) on the way to Morroc, even
    // though the direct route would be one map shorter.
    private Dictionary<string, float> _mapDanger = new(StringComparer.OrdinalIgnoreCase);
    private int _mapDangerLastLevel = -1;
    // Recompute trigger: when bot level changes by this much, the danger map is rebuilt.
    private const int DangerLevelStep = 3;

    private bool IsAggressive(int classId) => HuntPolicy.IsAggressive(_data?.Monster(classId));

    /// <summary>Walk every monster in the DB, aggregate per-map danger contributions weighted by the
    /// level gap (mob_level - bot_level), aggression, boss/MVP flag, and spawn count. Mobs at or below
    /// the bot's level contribute 0 (no threat). Run on bot level change in steps of <see cref="DangerLevelStep"/>
    /// so it's not recomputed every tick.</summary>
    private void RecomputeMapDanger(Snapshot snap)
    {
        if (_data == null) return;
        var botLevel = snap.SelfLevel;
        var fresh = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in _data.MonstersById.Values)
        {
            if (m.Spawns == null) continue;
            var levelGap = Math.Max(0, m.Level - botLevel);
            // Level gap alone missed mjolnir_02 for a Lv31: Argos/Mantis/Flora are only 1-5 levels up (danger 24),
            // yet the forecast loses to all three and 85 of them spawn there. So every aggressive spawn we can't
            // beat counts on its own, whatever its level. Immobile ones only hurt next to them: a quarter.
            var aggressive = IsAggressive(m.Id);
            // A class that killed us counts as unwinnable, whatever the forecast says: otherwise a same-level killer
            // scored 0 (the diedTo multiplier below never ran) and routes kept walking back through its map.
            var unwinnable = aggressive && (_avoidClasses.ContainsKey(m.Id)
                || !Forecast(snap, new MonsterInfo(0, m.Id, m.Name, default, m.HP, m.HP)).CanWin);
            if (levelGap == 0 && !unwinnable) continue; // mob is at or below the bot's level and beatable — not a danger
            var unwinnableDanger = unwinnable ? UnwinnableSpawnDanger * (IsMonsterImmobile(m) ? 0.25f : 1f) : 0f;
            var aggMul = aggressive ? 3.0f : 1.0f;
            // Per-class severity. Pow(1.5) makes the gap super-linear so a 35-level gap (instant-death)
            // weights ~6× a 10-level gap.
            var perGap = (float)Math.Pow(levelGap, 1.5);
            // Self-experience penalty: bot died to this class → treat as MUCH more dangerous than its
            // raw level suggests (it actually killed us before).
            var diedTo = _avoidClasses.ContainsKey(m.Id) ? 4.0f : 1.0f;
            foreach (var sp in m.Spawns)
            {
                if (string.IsNullOrEmpty(sp.Map)) continue;
                var bossMul = sp.IsMvp ? 10.0f : (sp.IsBoss ? 4.0f : 1.0f);
                var contrib = (perGap * aggMul / 100.0f + unwinnableDanger) * diedTo * bossMul * Math.Max(1, sp.Count);
                fresh[sp.Map] = (fresh.TryGetValue(sp.Map, out var prev) ? prev : 0) + contrib;
            }
        }
        _mapDanger = fresh;
        _mapDangerLastLevel = botLevel;
    }

    /// <summary>Returns map danger relative to this bot. 0 = safe (no mobs above our level), higher = more dangerous.</summary>
    private float GetMapDanger(string map) =>
        _mapDanger.TryGetValue(map, out var d) ? d : 0f;

    private int HazardRadius(int classId) => HuntPolicy.HazardRadius(_data?.Monster(classId));

    private const float UnwinnableSpawnDanger = 3f; // per spawn of an aggressive mob the forecast loses to ≈ 15 walked cells

    // Recompute danger zones once per tick (one Forecast per aggressive monster) so the cell checks
    // are cheap lookups instead of re-running the battle simulator for every candidate cell.
    private void ComputeHazards(Snapshot snap)
    {
        _hazards.Clear();
        foreach (var m in snap.Monsters)
        {
            if (m.MaxHp > 0 && m.Hp <= 0) continue;
            // Ignored (e.g. training dummy): neither hunt nor fear. Force-hunted: the hazard system runs BEFORE PickTarget on
            // every tick and a hazard short-circuits to AvoidHazardsAsync, so a force-hunt entry checked only in PickTarget
            // would never fire. Not aggressive: never a hazard.
            if (!HuntPolicy.HazardCandidate(_config, m.ClassId, _data?.Monster(m.ClassId))) continue;
            // Either engine saying "lose" makes a hazard; a server "win" never cancels the old "lose". The deaths on in_sphinx1
            // (Mantis, Zerom) came from the engines disagreeing at the zone's edge: a server "win" ended the flee, the next
            // tick's old "lose" restarted it. A server "lose" sticks to the monster until a server "win" arrives while we're
            // not fleeing, so a pending re-simulation (old says win) can't end a flee either.
            var old = Forecast(snap, m);
            var f = DecisionForecast(snap, m, old, "hazard", out var source);
            if (source == "server")
            {
                if (!f.CanWin) _serverLoses.Add(m.Id);
                else if (Mode != BotMode.Fleeing) _serverLoses.Remove(m.Id);
            }
            var serverLose = _serverLoses.Contains(m.Id);
            // The one we're fleeing stays a hazard until the flee ends: the old forecast flips with our HP/SP mid-chase,
            // and each "win" ended the flee and re-engaged it at 1 cell (Bot1 vs Zerom on in_sphinx2, Bot3 vs Raggler).
            var fleeing = Mode == BotMode.Fleeing && _fleeThreats.Contains(m.Id);
            if (!HuntPolicy.IsHazard(!old.CanWin || serverLose, fleeing)) continue; // aggressive but winnable = a target, not a hazard
            var engine = !old.CanWin ? (serverLose ? "old+server" : "old") : serverLose ? "server" : "earlier (still fleeing it)";
            _hazards.Add((m.Pos, HazardRadius(m.ClassId), m.Name, engine, m.Id));
            _seenHazards[m.Id] = (m.Pos, HazardRadius(m.ClassId), DateTime.UtcNow);
        }
        foreach (var (id, h) in _seenHazards)
            if (DateTime.UtcNow - h.at > HazardMemory) _seenHazards.Remove(id);
        _serverLoses.RemoveWhere(id => !snap.Monsters.Any(m => m.Id == id));
    }

    // Monsters the server forecast last called a loss (see ComputeHazards); only ever set with ServerForecast On.
    private readonly HashSet<int> _serverLoses = new();
    // Monsters the current flee is from (AvoidHazardsAsync); cleared when the flee ends.
    private readonly HashSet<int> _fleeThreats = new();
    private const int HazardBuffer = HuntPolicy.HazardBuffer;

    // A target in or near a hazard zone, or whose straight walk crosses one, pulls us back to the threat: Bot3 left See
    // Otter's zone by one cell, engaged an Aster beyond it and re-entered the zone three times in 6 s before dying.
    private bool ApproachNearHazard(Snapshot snap, Position target, out int hazard, out string where)
    {
        // In view, plus the ones seen in the last HazardMemory: Bot3 escaped one See Otter and engaged an Aster inside the
        // zone of a second one that had just left view. A remembered zone we stand in is stale (it'd be in view if there).
        var zones = _hazards.Select(h => (h.id, h.pos, h.radius)).Concat(_seenHazards
            .Where(s => _hazards.All(h => h.id != s.Key) && Dist(snap.SelfPos, s.Value.pos) > s.Value.radius)
            .Select(s => (id: s.Key, s.Value.pos, s.Value.radius))).ToList();
        return HuntPolicy.ApproachNearHazard(snap.SelfPos, target, zones, out hazard, out where);
    }

    // Verification telemetry for the flee/target-selection fix: one line per (candidate, threat) rejection, and one per pick
    // made shortly after a flee ended, so both sides of the guard show up in the log without per-tick spam.
    private readonly HashSet<(int, int)> _hazardRejectLogged = new();
    private (int id, string name, Position pos, int radius, DateTime endedAt) _lastFlee;
    private (int id, string name, Position pos, int radius) _fleeHz;
    private static readonly TimeSpan PostFleeWindow = TimeSpan.FromSeconds(30); // telemetry only

    private void NoteHazardReject(Snapshot snap, MonsterInfo m, int hazard, string where)
    {
        var inView = _hazards.FindIndex(x => x.id == hazard);
        var h = inView >= 0 ? _hazards[inView] : (pos: _seenHazards[hazard].pos, radius: _seenHazards[hazard].radius, name: "a monster out of view", engine: "earlier", id: hazard);
        if (_hazardRejectLogged.Count > 500) _hazardRejectLogged.Clear();
        if (!_hazardRejectLogged.Add((m.Id, h.id))) return;
        var winnable = WithHealing(Forecast(snap, m), m.MaxHp, HealingOn(snap, m)).CanWin;
        var fled = h.id == _fleeHz.id && Mode == BotMode.Fleeing ? "fleeing it now"
            : h.id == _lastFlee.id && DateTime.UtcNow - _lastFlee.endedAt < PostFleeWindow ? $"fled it {(DateTime.UtcNow - _lastFlee.endedAt).TotalSeconds:F0}s ago" : "not fled from";
        RoBotClient.Simulation.ForecastWorker.Count($"pick: rejected near hazard ({(winnable ? "winnable" : "unwinnable")}, {(fled == "not fled from" ? "other threat" : "threat being escaped")})");
        OnLog?.Invoke($"Target rejected: '{m.Name}' at ({m.Pos.X},{m.Pos.Y}) — {where} the zone of '{h.name}' at ({h.pos.X},{h.pos.Y}) "
            + $"(radius {h.radius}+{HazardBuffer}, {h.engine} says lose, {fled}). Me at ({snap.SelfPos.X},{snap.SelfPos.Y}), mode {Mode}, "
            + $"old forecast {(winnable ? "win" : "lose")}.");
    }

    private void NotePostFleePick(Snapshot snap, MonsterInfo m)
    {
        var since = DateTime.UtcNow - _lastFlee.endedAt;
        if (_lastFlee.name == null || since > PostFleeWindow) return;
        var threat = snap.Monsters.FirstOrDefault(x => x.Id == _lastFlee.id);
        var tpos = threat?.Pos ?? _lastFlee.pos;
        RoBotClient.Simulation.ForecastWorker.Count("pick: accepted after a flee");
        OnLog?.Invoke($"Post-flee pick: '{m.Name}' at ({m.Pos.X},{m.Pos.Y}), {since.TotalSeconds:F0}s after fleeing '{_lastFlee.name}' "
            + $"({(threat == null ? "out of view, last" : "in view")} at ({tpos.X},{tpos.Y}), radius {_lastFlee.radius}); me {Dist(snap.SelfPos, tpos)} cells from it, "
            + $"target {Dist(m.Pos, tpos)}; threat {(_hazards.Any(h => h.id == _lastFlee.id) ? "still a hazard" : "no longer a hazard")}.");
    }

    // Hazards seen recently but now out of view. Only path costs use them: once a fled-from mob left view, the
    // next A* went straight back through it and the bot ping-ponged between fleeing and traveling.
    // ponytail: last-seen position only; a chasing mob may have moved. Predict its position if that bites.
    private readonly Dictionary<int, (Position pos, int radius, DateTime at)> _seenHazards = new();
    private static readonly TimeSpan HazardMemory = TimeSpan.FromSeconds(30);

    private List<(int x, int y, int radius)> HazardZones()
    {
        var list = new List<(int x, int y, int radius)>(_seenHazards.Count + _hazards.Count);
        foreach (var h in _hazards) list.Add((h.pos.X, h.pos.Y, h.radius));
        foreach (var h in _seenHazards.Values) list.Add((h.pos.X, h.pos.Y, h.radius));
        return list;
    }

    /// <summary>First waypoint (≤ 8 cells) of an A* path to the current walk goal where every hazard zone is
    /// steeply priced (deeper = dearer, so from inside the path leaves by the shortest way out). Null when there's
    /// no fresh goal, no path, or the waypoint wouldn't take us farther from <paramref name="threat"/>.</summary>
    // HP over the last few seconds, to tell "a scary mob is in view" from "something is chewing on me".
    private readonly Queue<(DateTime at, int hp)> _hpHistory = new();
    private static readonly TimeSpan HpWindow = TimeSpan.FromSeconds(5);

    private void NoteHp(Snapshot snap)
    {
        var now = DateTime.UtcNow;
        _hpHistory.Enqueue((now, snap.SelfHp));
        while (_hpHistory.Count > 0 && now - _hpHistory.Peek().at > HpWindow) _hpHistory.Dequeue();
    }

    /// <summary>HP dropped from its peak in the last 5 s: by 15%+ of max, or at all while under 35%.</summary>
    private bool Ambushed(Snapshot snap, out string why)
    {
        why = "";
        if (snap.SelfMaxHp <= 0 || _hpHistory.Count == 0) return false;
        var lost = _hpHistory.Max(h => h.hp) - snap.SelfHp;
        if (lost <= 0) return false;
        why = $"lost {lost} HP in {HpWindow.TotalSeconds:0}s, at {snap.SelfHp}/{snap.SelfMaxHp}";
        return IsAmbushed(snap.SelfHp + lost, snap.SelfHp, snap.SelfMaxHp);
    }

    // Waiting to get hurt only works if the first hits don't kill: a Lv 65 Fur Seal hit Bot2 (614 HP) for 796 while it
    // was still "saving Fly Wings". Closing in (half its danger radius) and two of its hits kill us: wing now.
    private bool KillsMeFast(Snapshot snap, string name, int dist, int radius, out string why)
    {
        why = "";
        var m = snap.Monsters.Where(x => x.Name == name).MinBy(x => Dist(snap.SelfPos, x.Pos));
        if (m == null) return false;
        // Running only escapes what can't reach: a skill it casts while chasing hits us anywhere inside that skill's
        // range. Two of those kill us → wing out now, however far it still is.
        var (reachHit, reachSkill) = ChaseSkillReaching(snap, m, dist);
        if (reachHit * 2 >= snap.SelfHp)
        {
            why = $"its {reachSkill} (~{reachHit:F0}) reaches me while it chases, vs my {snap.SelfHp} HP";
            return true;
        }
        if (dist > radius / 2) return false;
        var hit = Forecast(snap, m).MonsterDamagePerHit;
        why = $"its hit ~{hit} vs my {snap.SelfHp} HP";
        return hit * 2 >= snap.SelfHp;
    }

    /// <summary>Biggest expected hit among the skills this monster casts while chasing that reach <paramref name="dist"/>
    /// cells (0 when none do).</summary>
    private (double hit, CharacterSkill skill) ChaseSkillReaching(Snapshot snap, MonsterInfo m, int dist)
    {
        if (_data?.Monster(m.ClassId) is not { } db) return (0, CharacterSkill.None);
        var me = MeCombatant(snap);
        var mon = MonsterCombatant(m);
        (double hit, CharacterSkill skill) best = (0, CharacterSkill.None);
        foreach (var c in _data.MonsterChaseCasts(db.Code))
        {
            if (BattleSimulator.MonsterSkillShape(c.Skill, c.Level) is not { } s || BattleSimulator.MonsterSkillReach(s, db.Range) < dist) continue;
            var hit = BattleSimulator.MonsterCastDamage(me, snap.SelfInt, snap.SelfMDef, mon, _data.MonsterInt(m.ClassId), c);
            if (hit > best.hit) best = (hit, c.Skill);
        }
        return best;
    }

    public static bool IsAmbushed(int peakHp, int hp, int maxHp) =>
        maxHp > 0 && peakHp > hp && (peakHp - hp >= maxHp * 0.15 || hp < maxHp * 0.35);

    // Outside a hazard flee (a forecast-winnable fight gone wrong, a pack we didn't see coming): wing out when low
    // and still bleeding after MaybeHealAsync had its go, instead of fighting to the death.
    private async Task<bool> TryEmergencyFlyWingAsync(Snapshot snap, CancellationToken ct)
    {
        if (snap.SelfMaxHp <= 0 || snap.SelfHp >= snap.SelfMaxHp * 0.25 || !Ambushed(snap, out var hurt)) return false;
        if (!EscapeWingReady || !HasUsableItem(_config.FlyWingItemId)) return false;
        OnLog?.Invoke($"Emergency: {hurt} — using a Fly Wing to get out.");
        ClearTarget();
        await UseEscapeWingAsync(_config.FlyWingItemId, 3, ct);
        return true;
    }

    private bool CellNearHazard(int x, int y, int skip = -1) => HuntPolicy.CellNearHazard(_hazards, x, y, skip);

    private async Task<bool> AvoidHazardsAsync(Snapshot snap, CancellationToken ct)
    {
        // An ongoing flee lasts until we're past the warning band too: ending it one cell outside the zone handed the next
        // tick to PickTarget at the zone's edge, and the hazard was back in range two seconds later.
        var (bestIdx, nearestD) = HuntPolicy.NearestHazardInReach(_hazards, snap.SelfPos, Mode == BotMode.Fleeing);
        if (bestIdx < 0)
        {
            if (Mode == BotMode.Fleeing && _fleeHz.name != null) _lastFlee = (_fleeHz.id, _fleeHz.name, _fleeHz.pos, _fleeHz.radius, DateTime.UtcNow);
            _fleeHz = default;
            _fleeThreats.Clear();
            // Hazard cleared (mob died, walked away, or we moved out of range). Surface the transition so a
            // tail of the bot's log shows the flee window ending instead of just going silent.
            if (Mode == BotMode.Fleeing)
                OnLog?.Invoke("Hazard no longer in range — resuming normal behavior.");
            _lastFleeLog = "";
            return false;
        }
        var hz = _hazards[bestIdx];

        if (Mode != BotMode.Fleeing)
        {
            RoBotClient.Simulation.ForecastWorker.Count($"hazard flee started: {hz.engine} says lose");
            OnLog?.Invoke($"Avoiding '{hz.name}' (aggressive, {hz.engine} forecast says I'd lose) — fleeing. It's {nearestD} cells away "
                + $"(danger radius {hz.radius}), I'm at ({snap.SelfPos.X},{snap.SelfPos.Y}), it's at ({hz.pos.X},{hz.pos.Y}).");
            NoteMapFlee(snap.Map);
        }
        Mode = BotMode.Fleeing;
        _fleeThreats.Add(hz.id);
        _fleeHz = (hz.id, hz.name, hz.pos, hz.radius);
        ClearTarget();

        // The move itself is HuntPolicy.ChooseFlee's (shared with the offline HuntSimulator): sidestep toward the walk goal
        // (the last WalkPathTowardAsync target, fresh within 5 s), Fly Wing, walk away, around a portal, fight back, hold.
        var goal = _walkGoal is { } g && g.map == snap.Map && DateTime.UtcNow - g.at <= TimeSpan.FromSeconds(5) ? (g.x, g.y) : ((int x, int y)?)null;
        var flee = HuntPolicy.ChooseFlee(_data, snap.Map, _data?.GetWalkMap(snap.Map), snap.SelfPos, _hazards, bestIdx, nearestD, goal, HazardZones(),
            MakePathCost(snap.Map), snap.Monsters,
            hurt: () =>
            {
                var hurt = Ambushed(snap, out var why) || KillsMeFast(snap, hz.name, nearestD, hz.radius, out why);
                return (hurt, why);
            },
            canFlyWing: () => EscapeWingReady && HasUsableItem(_config.FlyWingItemId));
        switch (flee.Kind)
        {
            case FleeKind.FlyWing:
                OnLog?.Invoke(flee.Message);
                _lastFleeAction = $"Fly Wing away from '{hz.name}'";
                _lastFleeAt = DateTime.UtcNow;
                await UseEscapeWingAsync(_config.FlyWingItemId, 3, ct); // let the teleport land before judging again
                break;
            case FleeKind.WalkBlind: // no walk map
                await _bot.WalkToAsync(flee.X, flee.Y, ct);
                break;
            case FleeKind.FightBack:
                LogFlee(flee.LogKind, flee.Message);
                if (DateTime.UtcNow >= _nextFightBack || _fightBackId != flee.MonsterId)
                {
                    _fightBackId = flee.MonsterId;
                    _nextFightBack = DateTime.UtcNow.AddSeconds(3); // re-assert like the hunt loop; flee clears _targetId every tick
                    await _bot.AttackAsync(flee.MonsterId, ct);
                }
                break;
            case FleeKind.Hold:
                LogFlee(flee.LogKind, flee.Message);
                break;
            default: // Dodge, Away, AroundPortal, CorneredBlind: walk there
                LogFlee(flee.LogKind, flee.Message);
                await _bot.WalkToAsync(flee.X, flee.Y, ct);
                break;
        }
        return true;
    }

    // Flee decisions log once per change of action (not every tick); the last one is quoted in the death log.
    private string _lastFleeLog = "";
    private int _fightBackId;
    private DateTime _nextFightBack;
    private string _lastFleeAction = "";
    private DateTime _lastFleeAt;
    private void LogFlee(string kind, string msg)
    {
        _lastFleeAction = msg.StartsWith("Flee: ") ? msg[6..] : msg;
        _lastFleeAt = DateTime.UtcNow;
        if (kind == _lastFleeLog) return;
        _lastFleeLog = kind;
        OnLog?.Invoke(msg);
    }
}
