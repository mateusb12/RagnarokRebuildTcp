using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
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
using RoRebuildServer.EntitySystem;
using RoRebuildServer.Networking;
using RoRebuildServer.Networking.PacketHandlers.Character;
using RoRebuildServer.Simulation;
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
    public string? Blocked;      // the server code threw: the world capability it needed
}

/// <param name="Outcome">1 target killed, -1 the player died, 0 time ran out.</param>
/// <param name="Killed">Every monster that died in the encounter (the target and anything area damage finished).</param>
public sealed record EncounterResult(int Outcome, double Seconds, int DamageTaken, int Actions, int SkillActions, IReadOnlyList<int> Killed);

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
/// - damage on monsters is subtracted here (ApplyQueuedCombatResult would run Monster.Die: rewards, drops, respawn);
///   damage on the player goes through the server's UpdateDamageQueue unless it is lethal (Player.Die is not run).
/// When server code throws (a world capability this slice doesn't have: World entity creation, NPC effects...) the
/// skill is marked Blocked with the failing server frame and is not offered again.
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

    private readonly Map _map;
    private readonly Player _player;
    private readonly NetworkConnection _connection;
    private readonly Random _random;
    private readonly Dictionary<int, Monster> _monsters = new();
    private readonly PacketSkill _skillPacketHandler = new();

    public readonly Dictionary<CharacterSkill, SkillLedger> Skills = new();
    public double Clock { get; private set; }
    public int MapWidth => _map.Width;
    public int MapHeight => _map.Height;
    public int PlayerId => _player.Character.Id;
    public int Hp => _player.CombatEntity.GetStat(CharacterStat.Hp);
    public int MaxHp => _player.CombatEntity.GetStat(CharacterStat.MaxHp);
    public int Sp => _player.CombatEntity.GetStat(CharacterStat.Sp);
    public int MaxSp => _player.CombatEntity.GetStat(CharacterStat.MaxSp);
    /// <summary>The player's current attack delay: what the live server sends the bot, buffs included.</summary>
    public double AttackDelay => _player.GetTiming(TimingStat.AttackDelayTime);

    /// <summary>Events worth a trace line (casts, rejections, statuses gained), as they happen.</summary>
    public Action<string>? OnEvent;

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
            try { _map = new Map(World.Instance, null!, mapName, mapName + ".walk", false); }
            finally { DataManager.MapConfigs = configs; }

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

    /// <summary>The recovery stand-in (no recovery model yet): HP and SP back to full, statuses kept.</summary>
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

    /// <summary>Time passes outside a fight (walking): the player's status effects keep running (and expiring) on the
    /// server's own update, every 0.25 s while there are any.</summary>
    public void Advance(double until)
    {
        if (until < Clock - 1e-9) throw new InvalidOperationException($"clock going back: {Clock} -> {until}");
        lock (ServerCombatRuntime.Gate)
        {
            Enter();
            var self = _player.CombatEntity;
            while (self.TryGetStatusContainer(out _) && Clock + StatusTickSeconds <= until)
            {
                SetClock(Clock + StatusTickSeconds);
                Observe(CharacterSkill.None, null, () => self.Update());
                if (self.DamageQueue.Count > 0 && LandOnPlayer(self) < 0) self.SetStat(CharacterStat.Hp, 1); // ponytail: a walk-time death is kept alive at 1 HP
            }
            SetClock(until);
            ServerCombatRuntime.ResetPacketQueue();
        }
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
                var landing = EarliestLanding();
                var castDone = self.IsCasting ? self.CastingTime + Epsilon : double.PositiveInfinity;
                var ready = self.IsCasting ? double.PositiveInfinity : Math.Max(_player.Character.AttackCooldown, holdUntil) + Epsilon;
                var t = Math.Max(Clock, Math.Min(Math.Min(landing, castDone), Math.Min(Math.Min(ready, nextMonster), nextTick)));
                if (t - start > maxSeconds) { SetClock(start + maxSeconds); break; } // cut off: time still passes to the limit
                if (++steps > 100_000) // a fight is hundreds of events; this many means one isn't consuming itself
                    throw new InvalidOperationException($"encounter stuck at {t:F3}s: landing {landing:F3}, cast {castDone:F3}, ready {ready:F3}, "
                        + $"monster {nextMonster:F3}, tick {nextTick:F3}, queued {_player.Character.QueuedAction}, player queue {self.DamageQueue.Count}, pending {pending}");
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
                        var effect = Observe(choice.Skill, enemy, () => Receive(choice.Packet, choice.Length));
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
                    if (!PerformAttack(_player, target.Character))
                        throw new InvalidOperationException($"the server refused the auto-attack on {target.Character.Name} at {_player.Character.Position} -> {target.Character.Position}");
                    actions++;
                    continue;
                }

                // The target's swing (it only fights back once hit, and not while disabled).
                if (enemy.GetStat(CharacterStat.Disabled) > 0 || !target.Character.StateCanAttack)
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
        if (type != PacketType.Skill) throw new ArgumentException($"not a skill packet: {type}");
        _skillPacketHandler.Process(_connection, message);
    }

    /// <summary>Runs server code that may execute <paramref name="skill"/> and books what it observably did.</summary>
    private Effect Observe(CharacterSkill skill, CombatEntity? target, Action serverCall)
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
        catch (Exception e) when (skill != CharacterSkill.None)
        {
            var ledger = Ledger(skill);
            ledger.Blocked ??= Describe(e);
            OnEvent?.Invoke($"{skill}: BLOCKED, {ledger.Blocked}");
            self.CancelCast(true);
            _player.Character.QueuedAction = QueuedAction.None;
            return Effect.Blocked;
        }
        var errors = DrainPackets();
        if (skill == CharacterSkill.None) return Effect.None;
        var book = Ledger(skill);
        foreach (var error in errors)
        {
            book.Rejected[error.ToString()] = book.Rejected.GetValueOrDefault(error.ToString()) + 1;
            OnEvent?.Invoke($"{skill}: server rejected it ({error})");
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
            OnEvent?.Invoke($"{skill}: cast bar {self.CastingTime - Time.ElapsedTimeFloat:F2}s");
            return Effect.CastBar;
        }
        if (_player.Character.QueuedAction == QueuedAction.Cast && errors.Count == 0 && spent == 0)
        {
            book.Queued++;
            return Effect.Queued;
        }
        var acted = spent > 0 || healed > 0 || gained.Count > 0 || _player.Character.AttackCooldown != cooldown || (casting && !self.IsCasting && errors.Count == 0);
        if (!acted) return errors.Count > 0 ? Effect.Rejected : Effect.None;
        book.Executed++;
        book.SpSpent += Math.Max(0, spent);
        book.Healed += healed;
        foreach (var g in gained) book.StatusesApplied[g] = book.StatusesApplied.GetValueOrDefault(g) + 1;
        OnEvent?.Invoke($"{skill}: executed" + (spent > 0 ? $", SP -{spent}" : "") + (healed > 0 ? $", HP +{healed}" : "")
                        + (gained.Count > 0 ? ", " + string.Join(", ", gained) : "") + (IsGenericHandler(skill) ? " (no server handler: generic cast)" : ""));
        return Effect.Executed;
    }

    /// <summary>The server has no handler for this skill and runs SkillHandlerGenericCast (cooldown and packet only).</summary>
    public static bool IsGenericHandler(CharacterSkill skill) =>
        ((SkillHandlerBase?[])HandlersField.GetValue(null)!)[(int)skill] is SkillHandlerGenericCast;

    /// <summary>The server's handler declares the skill passive (applied through the stat pipeline, never cast).</summary>
    public static bool IsPassive(CharacterSkill skill) => SkillHandler.GetSkillAttributes(skill).SkillTarget == SkillTarget.Passive;

    private static string Describe(Exception e)
    {
        var frame = new StackTrace(e).GetFrames().Select(f => f.GetMethod()).FirstOrDefault(m => m?.DeclaringType?.Namespace?.StartsWith("RoRebuildServer") == true);
        return $"{e.GetType().Name} in {frame?.DeclaringType?.Name}.{frame?.Name}: {e.Message}";
    }

    private int LandOnMonster(Monster monster, out CharacterSkill killedBy)
    {
        killedBy = CharacterSkill.None;
        var combat = monster.CombatEntity;
        var hp = combat.GetStat(CharacterStat.Hp);
        var total = 0;
        for (var i = 0; i < combat.DamageQueue.Count; i++)
        {
            var d = combat.DamageQueue[i];
            if (d.Time > Time.ElapsedTimeFloat) continue;
            combat.DamageQueue.RemoveAt(i--);
            if (!d.IsDamageResult) continue;
            var damage = d.Damage * Math.Max(1, (int)d.HitCount) + Math.Max(0, (int)d.DamageOffHand);
            total += damage;
            hp -= damage;
            if (d.AttackSkill != CharacterSkill.None)
            {
                var book = Ledger(d.AttackSkill);
                book.Damage += damage;
                book.Hits++;
            }
            if (hp <= 0 && killedBy == CharacterSkill.None) killedBy = d.AttackSkill;
        }
        combat.SetStat(CharacterStat.Hp, Math.Max(0, hp));
        if (total > 0) DuelSimulator.MonsterLastDamageField.SetValue(monster, Time.ElapsedTimeFloat);
        return total;
    }

    /// <summary>Damage due on the player: through the server's UpdateDamageQueue (status reactions, cast interruption)
    /// unless it is lethal, as Player.Die is not run. Returns -1 when the player died.</summary>
    private int LandOnPlayer(CombatEntity self)
    {
        var due = 0;
        foreach (var d in self.DamageQueue)
            if (d.Time <= Time.ElapsedTimeFloat && d.IsDamageResult) due += d.Damage * Math.Max(1, (int)d.HitCount);
        if (due >= Hp)
        {
            self.SetStat(CharacterStat.Hp, 0);
            self.DamageQueue.Clear();
            return -1;
        }
        Time.ElapsedTimeFloat += (float)Epsilon; // UpdateDamageQueue applies what is strictly due
        self.UpdateDamageQueue();
        Time.ElapsedTimeFloat = (float)Clock;
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

    private void SetClock(double seconds)
    {
        Clock = seconds;
        Time.ElapsedTime = seconds;
        Time.ElapsedTimeFloat = (float)seconds;
    }
}
