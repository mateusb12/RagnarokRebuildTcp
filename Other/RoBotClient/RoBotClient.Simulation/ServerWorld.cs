using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Lidgren.Network;
using RebuildSharedData.Data;
using RebuildSharedData.Enum;
using RebuildSharedData.Enum.EntityStats;
using RebuildSharedData.Networking;
using RebuildZoneServer.Networking;
using RoRebuildServer.Data.MapData;
using RoRebuildServer.Simulation.Util;
using RoRebuildServer.Data;
using RoRebuildServer.EntityComponents;
using RoRebuildServer.EntityComponents.Character;
using RoRebuildServer.EntityComponents.Util;
using RoRebuildServer.EntitySystem;
using RoRebuildServer.Networking;
using RoRebuildServer.Networking.PacketHandlers.Character;
using RoRebuildServer.Simulation;
using RoRebuildServer.Simulation.Pathfinding;
using RoRebuildServer.Simulation.Skills;
using RoRebuildServer.Simulation.Skills.SkillHandlers;

namespace RoBotClient.Simulation;

/// <summary>What one skill did over a run, as the server's own code executed it. Nothing here is computed per skill:
/// every number is read back from server state (SP, HP, status containers, queued DamageInfo.AttackSkill) or from the
/// packets the server sent the player (SkillError).</summary>
public sealed class SkillLedger
{
    public int Requested;        // skill packets the bot sent
    public int CastBars;         // casts that started a cast bar (cast time > 0)
    public double CastSeconds;
    public int Queued;           // the server queued the cast (after-cast delay still running) and resumed it later
    public int Executed;         // the server ran the handler: SP taken or the cast changed state
    public int SpSpent;
    public long Damage;          // damage results tagged with this skill that landed on monsters
    public int Hits;
    public int KillingBlows;
    public long Healed;          // HP the caster gained when it executed
    public readonly Dictionary<string, int> StatusesApplied = new(); // "self+IncreaseAgi", "target+Provoke", "area+..."
    public readonly Dictionary<string, int> Rejected = new();        // SkillError results the server sent, by reason
    // World events (server Npc events: ground effects, walls, traps...) this skill's execution created, children included.
    public int WorldEvents;
    public readonly Dictionary<string, int> EventKinds = new();      // by the server's event behaviour class
    public int EventsEnded;
    public double EventSeconds;  // summed lifetime of the ended ones, on the virtual clock
    public int Knockbacks;       // monsters the server's damage path moved on the walk data
    // A failure: server code threw. The first one is kept as evidence; the skill is not offered again after it.
    public string? Blocked;      // what failed: exception, the server frames that ran it
    public string? BlockedStage; // what the simulator was doing: the cast, or a world frame running its events
    public double BlockedAt;
    public int Failures;
    public int SuppressedPicks;  // later decisions where the live rule engine picked this skill but it was withheld
    public readonly List<string> RolledBack = new(); // player state a failed call had changed and that was put back
}

/// <param name="Outcome">1 target killed, -1 the player died, 0 time ran out, 2 the target got out of straight-line reach.</param>
/// <param name="Killed">Every monster that died in the encounter (the target and anything area damage finished).</param>
public sealed record EncounterResult(int Outcome, double Seconds, int DamageTaken, int Actions, int SkillActions, IReadOnlyList<int> Killed);

/// <summary>Something that happened in the world, at <paramref name="At"/> on the virtual clock. <paramref name="Text"/> is
/// its trace line (null: not traced, a replay-only fact such as a hit or a regen tick while walking).</summary>
/// <param name="Kind">hit, regen, knockback, executed, rejected, queued, no-effect, castbar, reach, abandon, area-start, area-end, area-status, world, failed.</param>
/// <param name="Entity">The server entity it concerns (a monster, the player, a ground effect); 0 when none.</param>
/// <param name="Amount">hit: damage landed; regen: HP gained; executed: SP spent; reach: cells to walk.</param>
/// <param name="Amount2">regen: SP gained; hit: damage results that landed (a 0-damage one is a miss).</param>
/// <param name="HpLeft">hit: the entity's HP after it.</param>
public sealed record WorldEvent(double At, string Kind, string? Text, int Entity = 0, CharacterSkill Skill = CharacterSkill.None,
    int Amount = 0, int Amount2 = 0, int HpLeft = -1, int X = -1, int Y = -1);

/// <summary>The player's choice when it may act: an auto-attack, a skill packet (the bytes the live bot sends), or
/// nothing yet (a pure caster with no rule ready re-decides a moment later).</summary>
public readonly record struct PlayerChoice(CharacterSkill Skill, byte[]? Packet, int Length, bool Wait)
{
    public static readonly PlayerChoice AutoAttack = new(CharacterSkill.None, null, 0, false);
    public static readonly PlayerChoice Hold = new(CharacterSkill.None, null, 0, true);
}

/// <summary>
/// A persistent slice of the game server for offline hunting. The map is a real server Map built from the same walk
/// file the live server loads (no spawn script runs on it), the player is one server Player entity for the whole run
/// and the monsters are server Monster entities at the hunt's positions, so HP, SP, status effects, skill cooldowns
/// and after-cast delays carry over from one encounter to the next on one virtual clock.
///
/// Skills have no per-skill code here. A cast is the packet the live bot sends (BotSession's encoding), handed to the
/// server's own PacketSkill handler: its checks (known level, skill cooldown, target type, SkillHandler.ValidateTarget),
/// then CombatEntity.AttemptStart*Skill, which takes SP, applies cast time, queues behind the after-cast delay and
/// dispatches through SkillHandler.ExecuteSkill to the skill's handler. Cast bars finish in CombatEntity.Update, the
/// same call that runs status effects. A queued cast resumes through Player.UpdateWithQueuedCast.
///
/// What the simulation still drives itself (the server's frame loop and AI are not run):
/// - the clock: events are taken earliest-first (damage landing, cast finishing, a 0.25 s status tick, the player's
///   next action slot, the target's next swing);
/// - the monster's side: only the current target swings back (CombatEntity.PerformMeleeAttack on the interval
///   ApplyCooldownForAttackAction gives it), monsters don't move, cast skills or assist;
/// - damage goes through the server's UpdateDamageQueue (status reactions, knockback on the walk data, cast
///   interruption) unless it is lethal: Monster.Die (rewards, drops, respawn) and Player.Die are not run;
/// - a knocked-back target is walked back into attack range in a straight line at the player's move speed (the live
///   server walks the player through TargetForAttack's path; monster AI, which would walk it back, is not run).
/// World events (ground effects, walls, traps, the area of effect they register) are the server's own: a handler calls
/// World.CreateEvent, the event's behaviour class runs its InitEvent/OnTimer/OnAoEInteraction, AreaOfEffect ticks in
/// Map.Update, and Npc.EndEvent/World.FullyRemoveEntity clean them up. What this class supplies is the server frame
/// those need: while any event lives, every 10 ms of virtual time (ZoneWorker's frame) it runs what Instance.Update and
/// World.Update run for them (Instance.DoRemovals, Map.Update, WorldObject.Update per event entity, World.PerformRemovals).
/// The process-wide World is a constructor-less stand-in (a real one loads every map and NPC), so its empty containers
/// are filled once (see CompleteWorld) and the map gets an Instance stand-in holding only its event entity list.
///
/// When server code throws (a world capability this slice still lacks) the player's SP/HP/statuses are put back to
/// what they were before the call, objects it created are ended, and the failure is booked with its evidence (skill,
/// stage, server frames, time). The skill is not offered again; the report marks the run INCOMPLETE and counts the
/// decisions where the live rule engine would still have picked it.
/// </summary>
public sealed class ServerWorld
{
    private const double StatusTickSeconds = 0.25;
    private const double Epsilon = 0.001; // the server compares cooldowns strictly (AttackCooldown < now)

    private static readonly FieldInfo OutboundField = typeof(NetworkManager).GetField("outboundChannel", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly FieldInfo EntityListField = typeof(World).GetField("entityList", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo HandlersField = typeof(SkillHandler).GetField("handlers", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly Func<Player, WorldObject, bool> PerformAttack = typeof(Player)
        .GetMethod("PerformAttack", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Func<Player, WorldObject, bool>>();
    private static readonly Action<Player> UpdateWithQueuedCast = typeof(Player)
        .GetMethod("UpdateWithQueuedCast", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Action<Player>>();
    private static readonly Action<Instance> DoRemovals = typeof(Instance)
        .GetMethod("DoRemovals", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Action<Instance>>();
    private const double FrameSeconds = 0.01; // ZoneWorker runs world.Update every 10 ms
    private static readonly Action<Player> UpdateRegenTick = typeof(Player)
        .GetMethod("UpdateRegenTick", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Action<Player>>();
    // ponytail: Player.Update's timers run in slices of at most this; a regen tick (6 s, 3 s sitting) lands up to a slice late.
    private const double TimerSliceSeconds = 0.25;
    private bool _walking;
    private static readonly FieldInfo ActiveAoEChunksField = typeof(Map).GetField("ActiveAoEChunks", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly Instance _instance;
    private double _nextFrame = double.PositiveInfinity;
    // Live server event entities -> the skill whose execution created them (children inherit the parent's skill).
    private readonly Dictionary<Entity, (CharacterSkill Skill, double Born, string Kind, int Id)> _events = new();

    private readonly Map _map;
    private readonly Player _player;
    private readonly NetworkConnection _connection;
    private readonly Random _random;
    private readonly Dictionary<int, Monster> _monsters = new();
    private readonly PacketSkill _skillPacketHandler = new();
    private readonly PacketSitStand _sitStandHandler = new();

    public readonly Dictionary<CharacterSkill, SkillLedger> Skills = new();
    public double Clock { get; private set; }
    public int MapWidth => _map.Width;
    public int MapHeight => _map.Height;
    public int PlayerId => _player.Character.Id;
    public int Hp => _player.CombatEntity.GetStat(CharacterStat.Hp);
    public int MaxHp => _player.CombatEntity.GetStat(CharacterStat.MaxHp);
    public int Sp => _player.CombatEntity.GetStat(CharacterStat.Sp);
    public int MaxSp => _player.CombatEntity.GetStat(CharacterStat.MaxSp);
    public bool Sitting => _player.Character.State == CharacterState.Sitting;
    /// <summary>HP and SP the server's own regen (Player.UpdateRegenTick) gave the player so far.</summary>
    public long RegenHp { get; private set; }
    public long RegenSp { get; private set; }
    /// <summary>The player's current attack delay: what the live server sends the bot, buffs included.</summary>
    public double AttackDelay => _player.GetTiming(TimingStat.AttackDelayTime);

    /// <summary>What happens in the world as it happens: events worth a trace line (casts, rejections, statuses gained)
    /// carry their Text; hits and regen ticks are also reported, for a replay, with no text.</summary>
    public Action<WorldEvent>? OnEvent;

    private void Emit(string kind, string? text, int entity = 0, CharacterSkill skill = CharacterSkill.None, int amount = 0, int amount2 = 0,
        int hpLeft = -1, int x = -1, int y = -1) =>
        OnEvent?.Invoke(new WorldEvent(Clock, kind, text, entity, skill, amount, amount2, hpLeft, x, y));

    static ServerWorld()
    {
        // The server DLL is a Debug build: a failed Debug.Assert would end the process. Inside this simulator it is a
        // world capability missing, reported like any other exception.
        Trace.Listeners.Clear();
        Trace.Listeners.Add(new ThrowingListener());
    }

    private sealed class ThrowingListener : TraceListener
    {
        public override void Fail(string? message, string? detailMessage) => throw new InvalidOperationException($"server assert: {message} {detailMessage}");
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { }
    }

    public ServerWorld(string mapName, PlayerBuild build, int x, int y, int seed)
    {
        if (!ServerCombatRuntime.IsInitialized) throw new InvalidOperationException("ServerCombatRuntime.EnsureInitialized must run first.");
        _random = new Random(seed);
        lock (ServerCombatRuntime.Gate)
        {
            Enter();
            // The map's own config would run its spawn script (World.CreateMonster): hide it while the map is built.
            var configs = DataManager.MapConfigs;
            DataManager.MapConfigs = new ReadOnlyDictionary<string, Action<IServerMapConfig>>(
                configs.Where(c => !string.Equals(c.Key, mapName, StringComparison.OrdinalIgnoreCase)).ToDictionary(c => c.Key, c => c.Value));
            CompleteWorld(World.Instance);
            _instance = NewInstance(mapName);
            var canTeleport = DataManager.Maps.FirstOrDefault(m => m.Code == mapName)?.MapMode != "Indoor"; // as Instance's constructor
            try { _map = new Map(World.Instance, _instance, mapName, mapName + ".walk", canTeleport); }
            finally { DataManager.MapConfigs = configs; }
            _instance.Maps.Add(_map);

            _player = DuelSimulator.CreatePlayer(build);
            _player.Character.Position = new Position(x, y);
            var entity = _player.Entity;
            _map.AddEntity(ref entity, false);
            Register(entity);
            // PacketSkill reads the caster from the connection that sent the packet.
            _connection = _player.Connection;
            _connection.Status = ConnectionStatus.Connected;
            _connection.Entity = entity;
            _connection.Character = _player.Character;
            _connection.Player = _player;
            ServerCombatRuntime.ResetPacketQueue();
            Restore(); // a character entering the map rested; Player.Init leaves SP at 0 here
        }
    }

    // ---- world state ----

    /// <summary>Places a server monster entity of this class on the map; returns its entity id.</summary>
    public int AddMonster(int classId, int x, int y)
    {
        lock (ServerCombatRuntime.Gate)
        {
            Enter();
            var monster = DuelSimulator.CreateMonster(DataManager.MonsterIdLookup[classId]);
            monster.Character.Position = new Position(x, y);
            var entity = monster.Entity;
            _map.AddEntity(ref entity, false);
            Register(entity);
            _monsters[monster.Character.Id] = monster;
            return monster.Character.Id;
        }
    }

    public void RemoveMonster(int id)
    {
        lock (ServerCombatRuntime.Gate)
        {
            Enter();
            if (!_monsters.Remove(id, out var monster)) return;
            DuelSimulator.ClearStatusEffects(monster.CombatEntity);
            var entity = monster.Entity;
            _map.RemoveEntity(ref entity, CharacterRemovalReason.Dead, false);
            ((Dictionary<int, Entity>)EntityListField.GetValue(World.Instance)!).Remove(id);
            EntityManager.Recycle(entity);
        }
    }

    /// <summary>Where the server has the player now (its own skills can move it: Back Slide, Teleport, walking into range).</summary>
    public (int X, int Y) PlayerPosition => (_player.Character.Position.X, _player.Character.Position.Y);

    /// <summary>Where the server has the monster now (knockback moves it).</summary>
    public (int X, int Y) MonsterPosition(int id) =>
        _monsters.TryGetValue(id, out var m) ? (m.Character.Position.X, m.Character.Position.Y) : (0, 0);

    /// <summary>Server event entities alive now (ground effects, wall pieces...).</summary>
    public int LiveEvents => _events.Count;

    public (int Hp, int MaxHp) MonsterHp(int id) =>
        _monsters.TryGetValue(id, out var m) ? (m.CombatEntity.GetStat(CharacterStat.Hp), m.CombatEntity.GetStat(CharacterStat.MaxHp)) : (0, 0);

    /// <summary>Client-visible status effects (what the live bot is told about) on the player (id 0) or a monster.</summary>
    public IReadOnlyCollection<CharacterStatusEffect> StatusesOf(int id)
    {
        var combat = id == 0 ? _player.CombatEntity : _monsters.TryGetValue(id, out var m) ? m.CombatEntity : null;
        return combat != null && combat.TryGetStatusContainer(out var c) ? c.GetClientSerializationContainer().Keys.ToList() : Array.Empty<CharacterStatusEffect>();
    }

    /// <summary>Moves the player on the server map (chunks and visibility updated by Map.ChangeEntityPosition3).</summary>
    public void MovePlayer(int x, int y)
    {
        lock (ServerCombatRuntime.Gate)
        {
            Enter();
            var ch = _player.Character;
            if (ch.Position.X == x && ch.Position.Y == y) return;
            _map.ChangeEntityPosition3(ch, ch.WorldPosition, new Position(x, y), false);
        }
    }

    /// <summary>Full HP and SP: a character entering the map, and the death stand-in (Revive).</summary>
    public void Restore()
    {
        lock (ServerCombatRuntime.Gate)
        {
            _player.CombatEntity.SetStat(CharacterStat.Hp, MaxHp);
            _player.CombatEntity.SetStat(CharacterStat.Sp, MaxSp);
        }
    }

    /// <summary>Death stand-in: Player.Die isn't run, so its status wipe is done here, then full HP/SP.</summary>
    public void Revive()
    {
        lock (ServerCombatRuntime.Gate)
        {
            Enter();
            DuelSimulator.ClearStatusEffects(_player.CombatEntity);
            _player.CombatEntity.CancelCast(true);
            _player.Character.QueuedAction = QueuedAction.None;
            Restore();
        }
    }

    /// <summary>Sends the client's sit/stand packet (BotSession.SitStandPacket) to the server's PacketSitStand; returns
    /// whether the player sits afterwards (the server refuses within its input delay, mid-cast, a Novice without Basic
    /// Mastery 2...).</summary>
    public bool SitStand(byte[] packet, int length)
    {
        lock (ServerCombatRuntime.Gate)
        {
            Enter();
            Receive(packet, length);
            ServerCombatRuntime.ResetPacketQueue();
            return Sitting;
        }
    }

    /// <summary>Time passes outside a fight (walking, or sitting): the player's status effects keep running (and
    /// expiring) on the server's own update every 0.25 s while there are any, world events keep running on server frames
    /// (a ground effect left behind can still hit monsters), and the server's regen ticks (as every SetClock).
    /// <paramref name="walking"/>: the time was spent walking, which the server's regen reads as CharacterState.Moving.
    /// Returns the monsters that died meanwhile.</summary>
    public IReadOnlyList<int> Advance(double until, bool walking = false)
    {
        if (until < Clock - 1e-9) throw new InvalidOperationException($"clock going back: {Clock} -> {until}");
        var killed = new List<int>();
        lock (ServerCombatRuntime.Gate)
        {
            Enter();
            _walking = walking;
            try { AdvanceTo(until, killed); }
            finally { _walking = false; }
        }
        return killed;
    }

    private void AdvanceTo(double until, List<int> killed)
    {
        var self = _player.CombatEntity;
        var nextTick = Clock + StatusTickSeconds;
        while (true)
        {
            var tick = self.TryGetStatusContainer(out _) ? nextTick : double.PositiveInfinity;
            var landing = EarliestLanding();
            var t = Math.Max(Clock, Math.Min(Math.Min(tick, _nextFrame), landing));
            if (t > until) break;
            SetClock(t);
            if (landing <= t)
            {
                foreach (var (id, monster) in _monsters.ToList())
                {
                    if (monster.CombatEntity.DamageQueue.Count == 0) continue;
                    LandOnMonster(monster, out var killedBy);
                    if (monster.CombatEntity.GetStat(CharacterStat.Hp) > 0) continue;
                    killed.Add(id);
                    if (killedBy != CharacterSkill.None) Ledger(killedBy).KillingBlows++;
                    RemoveMonster(id);
                }
                if (self.DamageQueue.Count > 0 && LandOnPlayer(self) < 0) self.SetStat(CharacterStat.Hp, 1); // ponytail: a walk-time death is kept alive at 1 HP
                continue;
            }
            if (_nextFrame <= t) { Frame(); continue; }
            nextTick = t + StatusTickSeconds;
            Observe(CharacterSkill.None, null, () => self.Update());
        }
        SetClock(until);
        ServerCombatRuntime.ResetPacketQueue();
    }

    // ---- one encounter ----

    /// <summary>Fights the monster <paramref name="targetId"/> from the current clock until it dies, the player dies, or
    /// <paramref name="maxSeconds"/> pass. <paramref name="decide"/> is asked whenever the player may act.</summary>
    public EncounterResult Fight(int targetId, Func<PlayerChoice> decide, double maxSeconds)
    {
        lock (ServerCombatRuntime.Gate)
        {
            Enter();
            var self = _player.CombatEntity;
            var target = _monsters[targetId];
            var enemy = target.CombatEntity;
            var start = Clock;
            var interval = DuelSimulator.MonsterAttackInterval(enemy);
            var fightsBack = DuelSimulator.FightsBack(target);
            var nextMonster = double.PositiveInfinity;
            var nextTick = HasStatuses() ? Clock + StatusTickSeconds : double.PositiveInfinity;
            var holdUntil = double.NegativeInfinity;
            var pending = CharacterSkill.None; // the skill whose cast bar is up or that the server queued
            int taken = 0, actions = 0, skillActions = 0, outcome = 0;
            var killed = new List<int>();
            var steps = 0;

            while (outcome == 0)
            {
                if (double.IsPositiveInfinity(nextTick) && HasStatuses()) nextTick = Clock + StatusTickSeconds; // a status gained mid-fight starts ticking
                var landing = EarliestLanding();
                var castDone = self.IsCasting ? self.CastingTime + Epsilon : double.PositiveInfinity;
                var ready = self.IsCasting ? double.PositiveInfinity : Math.Max(_player.Character.AttackCooldown, holdUntil) + Epsilon;
                var t = Math.Max(Clock, Math.Min(Math.Min(Math.Min(landing, castDone), _nextFrame), Math.Min(Math.Min(ready, nextMonster), nextTick)));
                if (t - start > maxSeconds) { SetClock(start + maxSeconds); break; } // cut off: time still passes to the limit
                if (++steps > 100_000) // a fight is hundreds of events; this many means one isn't consuming itself
                    throw new InvalidOperationException($"encounter stuck at {t:F3}s: landing {landing:F3}, cast {castDone:F3}, ready {ready:F3}, "
                        + $"monster {nextMonster:F3}, tick {nextTick:F3}, frame {_nextFrame:F3}, queued {_player.Character.QueuedAction}, player queue {self.DamageQueue.Count}, pending {pending}");
                SetClock(t);

                if (landing <= t)
                {
                    // Damage on monsters first (it can end the fight), then on the player.
                    foreach (var (id, monster) in _monsters.ToList())
                    {
                        if (monster.CombatEntity.DamageQueue.Count == 0) continue;
                        var dealt = LandOnMonster(monster, out var killedBy);
                        if (dealt > 0 && monster == target && fightsBack && double.IsPositiveInfinity(nextMonster)) nextMonster = t; // retaliation
                        if (monster.CombatEntity.GetStat(CharacterStat.Hp) > 0) continue;
                        killed.Add(id);
                        if (killedBy != CharacterSkill.None) Ledger(killedBy).KillingBlows++;
                        RemoveMonster(id);
                        if (id == targetId) outcome = 1;
                    }
                    if (outcome == 0 && self.DamageQueue.Count > 0 && LandOnPlayer(self) < 0) outcome = -1;
                    continue;
                }

                if (_nextFrame <= t)
                {
                    Frame(); // world events: their timers, area-of-effect ticks, expiry
                    continue;
                }

                if (castDone <= t || nextTick <= t)
                {
                    // The server's own update: a cast bar that ran out executes (FinishCasting), statuses tick/expire.
                    Observe(pending, enemy, () =>
                    {
                        self.Update();
                        foreach (var monster in _monsters.Values.ToList())
                            if (monster.CombatEntity.TryGetStatusContainer(out _)) monster.CombatEntity.Update();
                    });
                    nextTick = HasStatuses() ? t + StatusTickSeconds : double.PositiveInfinity;
                    continue;
                }

                if (ready <= t)
                {
                    // A cast the server queued behind the after-cast delay goes first, as in Player.Update.
                    if (_player.Character.QueuedAction == QueuedAction.Cast)
                    {
                        if (self.HasBodyState(BodyStateFlags.Silence) || _player.SkillCooldownTime > Time.ElapsedTime + 1.5f)
                            _player.Character.QueuedAction = QueuedAction.None; // Player.Update drops it
                        else if (_player.SkillCooldownTime <= Time.ElapsedTime)
                        {
                            var resumed = Observe(pending, enemy, () => UpdateWithQueuedCast(_player));
                            if (resumed is Effect.CastBar or Effect.Executed) { actions++; skillActions++; continue; }
                        }
                    }

                    var choice = decide();
                    if (choice.Wait)
                    {
                        holdUntil = t + StatusTickSeconds;
                        continue;
                    }
                    if (choice.Packet != null && Ledger(choice.Skill).Blocked == null)
                    {
                        Ledger(choice.Skill).Requested++;
                        var effect = Observe(choice.Skill, enemy, () => Receive(choice.Packet, choice.Length), packet: true);
                        if (effect is Effect.CastBar or Effect.Queued) pending = choice.Skill;
                        if (effect is Effect.CastBar or Effect.Executed)
                        {
                            actions++;
                            skillActions++;
                            continue;
                        }
                        // Rejected, queued or blocked: the live bot's auto-attack carries on (the server keeps swinging
                        // while a cast waits out the after-cast delay).
                    }
                    var range = Math.Max(1, self.GetStat(CharacterStat.Range));
                    var steps2 = StepsIntoRange(target.Character.Position, range);
                    if (steps2 == 0 && DistanceCache.IntDistance(_player.Character.Position, target.Character.Position) > range)
                    {
                        Emit("abandon", $"target out of reach and the straight line is blocked: fight abandoned (pathing back is not simulated)");
                        outcome = 2;
                        break;
                    }
                    if (steps2 > 0)
                    {
                        // ponytail: straight-line approach at MoveSpeed per cell; the live server paths the walk (TargetForAttack)
                        Emit("reach", $"target out of reach ({DistanceCache.IntDistance(_player.Character.Position, target.Character.Position)} > {range}): walking {steps2} cells", amount: steps2);
                        holdUntil = t + steps2 * _player.Character.MoveSpeed;
                        continue;
                    }
                    if (!PerformAttack(_player, target.Character))
                        throw new InvalidOperationException($"the server refused the auto-attack on {target.Character.Name} at {_player.Character.Position} -> {target.Character.Position}");
                    actions++;
                    continue;
                }

                // The target's swing (it only fights back once hit, not while disabled, and only within its own reach:
                // monsters don't walk here, so a player knocked, slid or teleported away is out of it).
                if (enemy.GetStat(CharacterStat.Disabled) > 0 || !target.Character.StateCanAttack || !self.IsValidTarget(enemy)
                    || DistanceCache.IntDistance(target.Character.Position, _player.Character.Position) > Math.Max(1, target.MonsterBase.Range))
                {
                    nextMonster = t + StatusTickSeconds;
                    continue;
                }
                var before = self.DamageQueue.Count;
                enemy.PerformMeleeAttack(self);
                for (var i = before; i < self.DamageQueue.Count; i++)
                    if (self.DamageQueue[i].IsDamageResult) taken += self.DamageQueue[i].Damage * Math.Max(1, (int)self.DamageQueue[i].HitCount);
                nextMonster = t + interval;
            }

            // A cast still up when the fight ends is dropped (its target is gone): the live one fails validation.
            if (self.IsCasting) self.CancelCast(true);
            _player.Character.QueuedAction = QueuedAction.None;
            self.DamageQueue.Clear();
            ServerCombatRuntime.ResetPacketQueue();
            return new EncounterResult(outcome, Clock - start, taken, actions, skillActions, killed);
        }
    }

    // ---- server plumbing ----

    private enum Effect { None, Rejected, Queued, CastBar, Executed, Blocked }

    /// <summary>The packet as the server receives it from a client: its type byte, then the handler for that type.</summary>
    private void Receive(byte[] packet, int length)
    {
        var message = new InboundMessage(_connection, packet, length);
        var type = (PacketType)message.ReadByte();
        if (type == PacketType.Skill) _skillPacketHandler.Process(_connection, message);
        else if (type == PacketType.SitStand) _sitStandHandler.Process(_connection, message);
        else throw new ArgumentException($"not a skill or sit/stand packet: {type}");
    }

    /// <summary>Runs server code that may execute <paramref name="skill"/> and books what it observably did.</summary>
    /// <param name="packet">The call is the bot's skill packet arriving (not a server update): a queued or unanswered packet is reported.</param>
    private Effect Observe(CharacterSkill skill, CombatEntity? target, Action serverCall, bool packet = false)
    {
        var self = _player.CombatEntity;
        var (sp, hp, cooldown, casting) = (Sp, Hp, _player.Character.AttackCooldown, self.IsCasting);
        var selfBefore = StatusSet(self);
        var targetBefore = target != null ? StatusSet(target) : null;
        DrainPackets();
        try
        {
            serverCall();
        }
        catch (Exception e)
        {
            // Put back what the failed call already did to the player, and end what it created in the world: a
            // missing capability must not cost or grant anything.
            var undone = new List<string>();
            if (Sp != sp) { undone.Add($"SP {Sp}->{sp}"); self.SetStat(CharacterStat.Sp, sp); }
            if (Hp != hp) { undone.Add($"HP {Hp}->{hp}"); self.SetStat(CharacterStat.Hp, hp); }
            foreach (var s in StatusSet(self).Except(selfBefore)) { self.RemoveStatusOfTypeIfExists(s); undone.Add($"status {s} removed"); }
            if (_player.Character.AttackCooldown != cooldown) { _player.Character.AttackCooldown = cooldown; undone.Add("attack delay restored"); }
            undone.AddRange(EndNewEvents());
            self.CancelCast(true);
            _player.Character.QueuedAction = QueuedAction.None;
            DrainPackets();
            Fail(skill != CharacterSkill.None ? skill : SoleEventSkill(), skill != CharacterSkill.None ? "cast/execute" : "status tick", e, undone);
            return Effect.Blocked;
        }
        TrackNewEvents(skill);
        var errors = DrainPackets();
        if (skill == CharacterSkill.None) return Effect.None;
        var book = Ledger(skill);
        foreach (var error in errors)
        {
            book.Rejected[error.ToString()] = book.Rejected.GetValueOrDefault(error.ToString()) + 1;
            Emit("rejected", $"{skill}: server rejected it ({error})", skill: skill);
        }

        var spent = sp - Sp;
        var healed = Math.Max(0, Hp - hp);
        var gained = new List<string>();
        foreach (var s in StatusSet(self).Except(selfBefore)) gained.Add($"self+{s}");
        if (target != null && targetBefore != null && target.Character.IsActive)
            foreach (var s in StatusSet(target).Except(targetBefore)) gained.Add($"target+{s}");
        if (self.IsCasting && !casting)
        {
            book.CastBars++;
            book.CastSeconds += self.CastingTime - Time.ElapsedTimeFloat;
            Emit("castbar", $"{skill}: cast bar {self.CastingTime - Time.ElapsedTimeFloat:F2}s", skill: skill);
            return Effect.CastBar;
        }
        if (_player.Character.QueuedAction == QueuedAction.Cast && errors.Count == 0 && spent == 0)
        {
            book.Queued++;
            if (packet) Emit("queued", null, PlayerId, skill); // replay only: the trace stays as it was
            return Effect.Queued;
        }
        var acted = spent > 0 || healed > 0 || gained.Count > 0 || _player.Character.AttackCooldown != cooldown || (casting && !self.IsCasting && errors.Count == 0);
        if (!acted)
        {
            if (packet && errors.Count == 0) Emit("no-effect", null, PlayerId, skill);
            return errors.Count > 0 ? Effect.Rejected : Effect.None;
        }
        book.Executed++;
        book.SpSpent += Math.Max(0, spent);
        book.Healed += healed;
        foreach (var g in gained) book.StatusesApplied[g] = book.StatusesApplied.GetValueOrDefault(g) + 1;
        Emit("executed", $"{skill}: executed" + (spent > 0 ? $", SP -{spent}" : "") + (healed > 0 ? $", HP +{healed}" : "")
                        + (gained.Count > 0 ? ", " + string.Join(", ", gained) : "") + (IsGenericHandler(skill) ? " (no server handler: generic cast)" : ""), skill: skill, amount: spent);
        return Effect.Executed;
    }

    /// <summary>The server has no handler for this skill and runs SkillHandlerGenericCast (cooldown and packet only).</summary>
    public static bool IsGenericHandler(CharacterSkill skill) =>
        ((SkillHandlerBase?[])HandlersField.GetValue(null)!)[(int)skill] is SkillHandlerGenericCast;

    /// <summary>The server's handler declares the skill passive (applied through the stat pipeline, never cast).</summary>
    public static bool IsPassive(CharacterSkill skill) => SkillHandler.GetSkillAttributes(skill).SkillTarget == SkillTarget.Passive;

    /// <summary>The target type the server's handler declares (Self, Enemy, Ground, Ally, Any, Passive): PacketSkill drops a
    /// packet of another type without an error.</summary>
    public static string DeclaredTarget(CharacterSkill skill) => SkillHandler.GetSkillAttributes(skill).SkillTarget.ToString();

    /// <summary>The exception and the server frames it went through, innermost first: the operation that failed, then
    /// who called it (the event behaviour, the skill handler...).</summary>
    private static string Describe(Exception e)
    {
        var frames = new StackTrace(e).GetFrames().Select(f => f.GetMethod())
            .Where(m => m?.DeclaringType?.Namespace?.StartsWith("RoRebuildServer") == true)
            .Select(m => $"{m!.DeclaringType!.Name}.{m.Name}").Distinct().Take(4).ToList();
        return $"{e.GetType().Name} in {string.Join(" <- ", frames)}: {e.Message}";
    }

    /// <summary>Damage due on a monster, booked to the skill that dealt it, then applied by the server's own
    /// UpdateDamageQueue (status reactions, knockback over the walk data, area effects re-triggered by the move) unless
    /// it is lethal, as Monster.Die (rewards, drops, respawn) is not run.</summary>
    private int LandOnMonster(Monster monster, out CharacterSkill killedBy)
    {
        killedBy = CharacterSkill.None;
        var combat = monster.CombatEntity;
        var hp = combat.GetStat(CharacterStat.Hp);
        var left = hp;
        var total = 0;
        var pusher = CharacterSkill.None;
        var results = 0;
        foreach (var d in combat.DamageQueue)
        {
            if (d.Time > Time.ElapsedTimeFloat || !d.IsDamageResult) continue;
            results++;
            var damage = d.Damage * Math.Max(1, (int)d.HitCount) + Math.Max(0, (int)d.DamageOffHand);
            total += damage;
            left -= damage;
            if ((d.KnockBack > 0 || pusher == CharacterSkill.None) && d.AttackSkill != CharacterSkill.None) pusher = d.AttackSkill; // knockback's skill, else the first skill
            if (d.AttackSkill != CharacterSkill.None)
            {
                var book = Ledger(d.AttackSkill);
                book.Damage += damage;
                book.Hits++;
            }
            if (left <= 0 && killedBy == CharacterSkill.None) killedBy = d.AttackSkill;
        }
        if (left <= 0)
        {
            combat.SetStat(CharacterStat.Hp, 0);
            combat.DamageQueue.Clear();
        }
        else
        {
            var from = monster.Character.Position;
            Time.ElapsedTimeFloat += (float)Epsilon; // UpdateDamageQueue applies what is strictly due
            try
            {
                combat.UpdateDamageQueue();
            }
            catch (Exception e)
            {
                // The damage itself is certain (booked above); what the server failed to do around it (knockback,
                // reactions) is reported. The HP loss is applied without it and the rest of the due entries dropped.
                combat.SetStat(CharacterStat.Hp, left);
                combat.DamageQueue.RemoveAll(d => d.Time <= Time.ElapsedTimeFloat);
                Fail(pusher != CharacterSkill.None ? pusher : killedBy, $"damage landing on {monster.Character.Name} (UpdateDamageQueue)", e,
                    new List<string> { $"{monster.Character.Name} HP set to {left} without the failed step" });
            }
            finally { Time.ElapsedTimeFloat = (float)Clock; }
            var to = monster.Character.Position;
            if (from != to)
            {
                if (pusher != CharacterSkill.None) Ledger(pusher).Knockbacks++;
                Emit("knockback", $"{(pusher != CharacterSkill.None ? pusher : "hit")}: {monster.Character.Name} knocked back {from} -> {to}", monster.Character.Id, pusher, x: to.X, y: to.Y);
            }
        }
        if (total > 0) DuelSimulator.MonsterLastDamageField.SetValue(monster, Time.ElapsedTimeFloat);
        if (results > 0) Emit("hit", null, monster.Character.Id, pusher, total, results, Math.Max(0, combat.GetStat(CharacterStat.Hp)),
            monster.Character.Position.X, monster.Character.Position.Y);
        return total;
    }

    /// <summary>Cells the player walks in a straight line (walkable cells only) to get within range of the target.</summary>
    private int StepsIntoRange(Position target, int range)
    {
        var p = _player.Character.Position;
        var steps = 0;
        while (DistanceCache.IntDistance(p, target) > range)
        {
            var next = new Position(p.X + Math.Sign(target.X - p.X), p.Y + Math.Sign(target.Y - p.Y));
            if (!_map.WalkData.IsCellWalkable(next.X, next.Y)) break;
            p = next;
            steps++;
        }
        if (steps > 0) _map.ChangeEntityPosition3(_player.Character, _player.Character.WorldPosition, p, false);
        return steps;
    }

    // ---- world events: the server frame they need ----

    /// <summary>The process-wide World stand-in had no constructor run, so no field initializer either: give every
    /// empty container field an empty instance (entity lock, AoE lock, removal bag...), then the AoE pool and id range
    /// the constructor sets. No maps, NPCs or instances are loaded.</summary>
    private static void CompleteWorld(World world)
    {
        var pool = typeof(World).GetField("<AoEPool>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!;
        if (pool.GetValue(world) != null) return;
        DistanceCache.Init(); // the server's startup (ZoneWorker.Initialize) builds it before the world: knockback, ranges
        FillEmpty(world);
        var poolType = pool.FieldType.Assembly.GetType("Microsoft.Extensions.ObjectPool.DefaultObjectPool`1")!.MakeGenericType(typeof(AreaOfEffect));
        pool.SetValue(world, Activator.CreateInstance(poolType, new AreaOfEffectPoolPolicy(), 128));
        typeof(World).GetField("maxEntityId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(world, 10_000_000);
    }

    /// <summary>The map's Instance, as far as world events use it: the entity list CreateEvent adds to (Map.AddEntity)
    /// and the removal list FullyRemoveEntity feeds. Its constructor would build and populate every map of the instance.
    /// Players and monsters here are added with addToInstance false, so the list holds only events.</summary>
    private static Instance NewInstance(string mapName)
    {
        var instance = (Instance)RuntimeHelpers.GetUninitializedObject(typeof(Instance));
        instance.Name = "offline " + mapName;
        instance.Maps = new List<Map>();
        instance.Entities = new EntityList(128) { IsActive = true }; // IsActive as the constructor sets it in DEBUG (EntityListPool borrow tracking)
        instance.Pathfinder = new PathFinder(); // a monster hit by the player plans a chase through it (Monster.OutStartChase -> TryMove)
        typeof(Instance).GetField("removeList", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, new List<Entity>(30));
        typeof(Instance).GetField("world", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, World.Instance);
        return instance;
    }

    /// <summary>Empty .NET containers and locks only: a server type's constructor could do anything (World's loads
    /// every map and replaces World.Instance).</summary>
    private static void FillEmpty(object o)
    {
        foreach (var f in o.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            if (f.GetValue(o) == null && f.FieldType.Namespace?.StartsWith("System") == true && !f.FieldType.IsAbstract && f.FieldType.GetConstructor(Type.EmptyTypes) != null)
                f.SetValue(o, Activator.CreateInstance(f.FieldType));
    }

    /// <summary>One server frame for the world events, in Instance.Update/World.Update order. Scheduled every
    /// FrameSeconds while any event or area of effect is live.</summary>
    private void Frame()
    {
        Time.UpdateCount++; // WorldObject.Update checks it was not updated twice in one frame
        var self = _player.CombatEntity;
        var selfBefore = StatusSet(self);
        try
        {
            DoRemovals(_instance);
            _map.Update(); // area-of-effect stay ticks (AreaOfEffect.Update -> the event's OnAoEInteraction)
        }
        catch (Exception e)
        {
            var undone = EndAllEvents();
            Fail(SoleEventSkill(), "world frame: Map.Update (area-of-effect tick)", e, undone);
        }
        var entities = _instance.Entities;
        for (var i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];
            if (!entity.IsAlive()) continue;
            var ch = entity.Get<WorldObject>();
            if (!ch.IsActive) continue;
            var skill = _events.TryGetValue(entity, out var ev) ? ev.Skill : CharacterSkill.None;
            try
            {
                ch.Update(); // Npc.Update: timers (OnTimer), owner checks, event expiry
            }
            catch (Exception e)
            {
                var undone = new List<string>();
                try { if (ch.IsActive) ch.Npc.EndEvent(); undone.Add($"{ch.Npc.Behavior.GetType().Name} ended"); }
                catch (Exception e2) { undone.Add($"ending it failed too ({e2.GetType().Name})"); }
                Fail(skill, $"world frame: {ch.Npc.Behavior?.GetType().Name ?? ch.Type.ToString()} update", e, undone);
            }
            TrackNewEvents(skill);
        }
        World.Instance.PerformRemovals();
        foreach (var (entity, ev) in _events.ToList())
        {
            if (entity.IsAlive() && entity.Get<WorldObject>().IsActive) continue;
            _events.Remove(entity);
            Emit("area-end", $"{ev.Skill}: world event {ev.Kind} ended after {Clock - ev.Born:F2}s", ev.Id, ev.Skill);
            if (ev.Skill == CharacterSkill.None) continue;
            var book = Ledger(ev.Skill);
            book.EventsEnded++;
            book.EventSeconds += Clock - ev.Born;
        }
        if (!WorldActive()) // the server drops removed entities a frame late (Instance.DoRemovals), so this is the frame after the last one ended
            Emit("world", $"world: all events ended and removed; instance entity list {_instance.Entities.Count}, map chunks holding an area of effect "
                            + ((int[])ActiveAoEChunksField.GetValue(_map)!).Count(c => c > 0));
        // Statuses the events put on the player (a protective ground effect, say) are booked to their skill.
        var gained = StatusSet(self).Except(selfBefore).ToList();
        if (gained.Count > 0)
        {
            var skill = SoleEventSkill();
            if (skill != CharacterSkill.None)
                foreach (var s in gained) Ledger(skill).StatusesApplied[$"area+{s}"] = Ledger(skill).StatusesApplied.GetValueOrDefault($"area+{s}") + 1;
            Emit("area-status", $"{skill}: world event put {string.Join(", ", gained)} on the player", PlayerId, skill);
        }
        ScheduleFrame();
    }

    private bool WorldActive() => _events.Count > 0 || _instance.Entities.Count > 0;

    private void ScheduleFrame() => _nextFrame = WorldActive() ? Clock + FrameSeconds : double.PositiveInfinity;

    /// <summary>Server event entities that appeared since the last look are booked to the skill whose execution (or
    /// whose parent event) created them.</summary>
    private void TrackNewEvents(CharacterSkill skill)
    {
        var entities = _instance.Entities;
        for (var i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];
            if (!entity.IsAlive() || _events.ContainsKey(entity)) continue;
            var ch = entity.Get<WorldObject>();
            if (!ch.IsActive) continue;
            var kind = ch.Type is CharacterType.NPC or CharacterType.BattleNpc ? ch.Npc.Behavior?.GetType().Name ?? ch.Name : ch.Type.ToString();
            _events[entity] = (skill, Clock, kind, ch.Id);
            if (skill != CharacterSkill.None)
            {
                var book = Ledger(skill);
                book.WorldEvents++;
                book.EventKinds[kind] = book.EventKinds.GetValueOrDefault(kind) + 1;
            }
            var aoe = ch.Type is CharacterType.NPC or CharacterType.BattleNpc && ch.Npc.AreaOfEffect != null ? $", area {ch.Npc.AreaOfEffect.Area}" : "";
            Emit("area-start", $"{skill}: world event {kind} created at {ch.Position}{aoe}", ch.Id, skill, x: ch.Position.X, y: ch.Position.Y);
        }
        if (double.IsPositiveInfinity(_nextFrame) && WorldActive()) _nextFrame = Clock + FrameSeconds;
    }

    /// <summary>The skill all live events belong to, when there is exactly one (to attribute a map-wide failure).</summary>
    private CharacterSkill SoleEventSkill()
    {
        var skills = _events.Values.Select(e => e.Skill).Where(s => s != CharacterSkill.None).Distinct().ToList();
        return skills.Count == 1 ? skills[0] : CharacterSkill.None;
    }

    /// <summary>Ends events a failed call created (not yet tracked).</summary>
    private List<string> EndNewEvents()
    {
        var undone = new List<string>();
        var entities = _instance.Entities;
        for (var i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];
            if (!entity.IsAlive() || _events.ContainsKey(entity)) continue;
            undone.Add(EndEvent(entity));
        }
        return undone;
    }

    private List<string> EndAllEvents() => _events.Keys.ToList().Where(e => e.IsAlive()).Select(EndEvent).ToList();

    private string EndEvent(Entity entity)
    {
        var ch = entity.Get<WorldObject>();
        var name = ch.Type is CharacterType.NPC or CharacterType.BattleNpc ? ch.Npc.Behavior?.GetType().Name ?? ch.Name : ch.Name;
        try
        {
            if (ch.IsActive) ch.Npc.EndEvent();
            return $"{name} ended";
        }
        catch (Exception e)
        {
            // Force it off the map so it can't act again.
            ch.IsActive = false;
            if (ch.Map != null) { var en = entity; ch.Map.RemoveEntity(ref en, CharacterRemovalReason.OutOfSight, false); }
            return $"{name} removed by force (EndEvent threw {e.GetType().Name})";
        }
    }

    /// <summary>Books a failure with its evidence. The skill (if known) is withheld from later decisions.</summary>
    private void Fail(CharacterSkill skill, string stage, Exception e, List<string> undone)
    {
        var diagnostic = Describe(e);
        var record = $"{(skill != CharacterSkill.None ? skill.ToString() : "(unattributed)")} at {Clock:F2}s during {stage}: {diagnostic}"
                     + (undone.Count > 0 ? $" [undone: {string.Join(", ", undone)}]" : "");
        Failures.Add(record);
        Emit("failed", "FAILED " + record, skill: skill);
        var book = Ledger(skill);
        book.Failures++;
        book.RolledBack.AddRange(undone);
        if (book.Blocked != null) return;
        book.Blocked = diagnostic;
        book.BlockedStage = stage;
        book.BlockedAt = Clock;
    }

    /// <summary>Every failure of server code in this run, in order, with its evidence.</summary>
    public readonly List<string> Failures = new();

    /// <summary>Damage due on the player: through the server's UpdateDamageQueue (status reactions, cast interruption)
    /// unless it is lethal, as Player.Die is not run. Returns -1 when the player died.</summary>
    private int LandOnPlayer(CombatEntity self)
    {
        int due = 0, results = 0;
        foreach (var d in self.DamageQueue)
            if (d.Time <= Time.ElapsedTimeFloat && d.IsDamageResult) { due += d.Damage * Math.Max(1, (int)d.HitCount); results++; }
        if (due >= Hp)
        {
            self.SetStat(CharacterStat.Hp, 0);
            self.DamageQueue.Clear();
            Emit("hit", null, PlayerId, amount: due, amount2: results, hpLeft: 0);
            return -1;
        }
        Time.ElapsedTimeFloat += (float)Epsilon; // UpdateDamageQueue applies what is strictly due
        var hp = Hp;
        try
        {
            self.UpdateDamageQueue();
        }
        catch (Exception e)
        {
            self.SetStat(CharacterStat.Hp, hp - due);
            self.DamageQueue.RemoveAll(d => d.Time <= Time.ElapsedTimeFloat);
            Fail(CharacterSkill.None, "damage landing on the player (UpdateDamageQueue)", e, new List<string> { $"player HP set to {hp - due} without the failed step" });
        }
        finally { Time.ElapsedTimeFloat = (float)Clock; }
        if (results > 0) Emit("hit", null, PlayerId, amount: due, amount2: results, hpLeft: Hp);
        return due;
    }

    private double EarliestLanding()
    {
        var earliest = double.PositiveInfinity;
        foreach (var d in _player.CombatEntity.DamageQueue) earliest = Math.Min(earliest, d.Time);
        foreach (var m in _monsters.Values)
            foreach (var d in m.CombatEntity.DamageQueue) earliest = Math.Min(earliest, d.Time);
        return earliest;
    }

    private bool HasStatuses() => _player.CombatEntity.TryGetStatusContainer(out _) || _monsters.Values.Any(m => m.CombatEntity.TryGetStatusContainer(out _));

    private static HashSet<CharacterStatusEffect> StatusSet(CombatEntity c) =>
        c.TryGetStatusContainer(out var container) ? container.GetClientSerializationContainer().Keys.ToHashSet() : new();

    private SkillLedger Ledger(CharacterSkill skill) => Skills.TryGetValue(skill, out var l) ? l : Skills[skill] = new SkillLedger();

    /// <summary>The SkillError results the server sent this player since the last drain; everything else is dropped.</summary>
    private List<SkillValidationResult> DrainPackets()
    {
        var errors = new List<SkillValidationResult>();
        var queue = (NetQueue<OutboundMessage>)OutboundField.GetValue(null)!;
        while (queue.TryDequeue(out var message))
            if (message.Length >= 2 && message.Message[0] == (byte)PacketType.SkillError && message.Clients.Contains(_connection))
                errors.Add((SkillValidationResult)message.Message[1]);
        return errors;
    }

    private void Register(Entity entity) =>
        ((Dictionary<int, Entity>)EntityListField.GetValue(World.Instance)!)[entity.Get<WorldObject>().Id] = entity;

    private void Enter() => DuelSimulator.RandomField.SetValue(null, _random); // this run's GameRandom stream

    /// <summary>Moves the clock, running the player's per-frame timers from Player.Update over the time that passed: the
    /// input-action delay and the server's own regen (UpdateRegenTick: its 6 s ticks, sitting and walking rates, the
    /// HpRegenTick/SpRegenTick amounts). Not the rest of Player.Update: casts and attacks are this class's to drive.</summary>
    private void SetClock(double seconds)
    {
        for (var at = Clock; at < seconds - 1e-9;)
        {
            var dt = Math.Min(TimerSliceSeconds, seconds - at);
            at += dt;
            SetTime(at);
            Time.DeltaTime = dt;
            Time.DeltaTimeFloat = (float)dt;
            _player.InputActionCooldown = Math.Max(0, _player.InputActionCooldown - (float)dt); // Player.Update's first lines
            var ch = _player.Character;
            var state = ch.State;
            if (_walking && state == CharacterState.Idle) ch.State = CharacterState.Moving; // the walk MovePlayer jumped over
            var (hp, sp) = (Hp, Sp);
            try { UpdateRegenTick(_player); }
            finally { ch.State = state; }
            RegenHp += Math.Max(0, Hp - hp);
            RegenSp += Math.Max(0, Sp - sp);
            if (Hp > hp || Sp > sp)
                Emit("regen", Sitting ? $"regen tick (sitting): +{Hp - hp} HP, +{Sp - sp} SP -> HP {Hp}/{MaxHp} SP {Sp}/{MaxSp}" : null, PlayerId, amount: Hp - hp, amount2: Sp - sp);
        }
        SetTime(seconds);
    }

    private void SetTime(double seconds)
    {
        Clock = seconds;
        Time.ElapsedTime = seconds;
        Time.ElapsedTimeFloat = (float)seconds;
    }
}
