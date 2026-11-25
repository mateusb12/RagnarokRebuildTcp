using RebuildSharedData.ClientTypes;
using RebuildSharedData.Data;
using RebuildSharedData.Enum;
using RoBotClient.Bot.GameData;
using RoBotClient.Bot.Session;
using RoBotClient.Bot.State;

namespace RoBotClient.Bot.Behavior;

// Dying: blacklist the killer's class for a while (longer when it kills us again), log the lost duel, and respawn.
public sealed partial class BotBehavior
{
    // Per-class death blacklist with time-decay. Dying to a mob class adds it here; PickTarget skips
    // anything with a not-yet-expired entry. Decays after AvoidClassDuration so a fluke death doesn't
    // permanently ban a class — particularly important for low-level bots that may die once and then be
    // unable to hunt the most common mob on their map for the rest of the session.
    private readonly Dictionary<int, DateTime> _avoidClasses = new();
    private static readonly TimeSpan AvoidClassDuration = TimeSpan.FromMinutes(HuntPolicy.AvoidClassMinutes);
    // Dying to the same class again inside the window means it's no fluke: Bot3 died to Elder Willow on three maps in 4 min.
    private static readonly TimeSpan RepeatKillerAvoidDuration = TimeSpan.FromMinutes(HuntPolicy.RepeatKillerAvoidMinutes);

    private DateTime _nextRespawn = DateTime.MinValue;

    private async Task HandleDeathAsync(Snapshot snap, CancellationToken ct)
    {
        _hpHistory.Clear(); // the pre-death peak made the 1 HP respawn look like an ambush: Bot2 burnt two wings in comodo

        if (Mode != BotMode.Dead)
        {
            var modeBefore = Mode;
            Mode = BotMode.Dead;
            NoteMapDeath(snap.Map);
            // Dying on the way to the hunting map is the hunting map's cost too: the route there is part of the choice.
            if (!OnHomeMap(snap.Map) && _shopPhase == ShopPhase.None)
                NoteMapDeath(_config.HomeMap, onTheWay: true);
            _nextMapCheck = DateTime.MinValue; // re-judge from wherever it respawns
            // Attribute to whoever ACTUALLY hit us last (assist mobs / aggressor pile-ons are usually NOT
            // our committed target). Fall back to the committed target only when the last-attacker entity
            // is gone from view by death-handling time.
            var killerClass = -1;
            var killerName = "";
            var attackerName = _bot.LastAttackerName;
            if (!string.IsNullOrEmpty(attackerName))
            {
                var info = _bot.WithState(w =>
                {
                    foreach (var e in w.Entities.Values)
                        if (e.IsMonster && string.Equals(e.Name, attackerName, StringComparison.Ordinal))
                            return (cls: e.ClassId, name: e.Name);
                    return (cls: -1, name: "");
                });
                if (info.cls >= 0) { killerClass = info.cls; killerName = info.name; }
            }
            if (killerClass < 0 && _targetClass >= 0) { killerClass = _targetClass; killerName = TargetName; }
            if (killerClass >= 0)
            {
                var fresh = !(_avoidClasses.TryGetValue(killerClass, out var prevUntil) && prevUntil > DateTime.UtcNow);
                var expires = DateTime.UtcNow + (fresh ? AvoidClassDuration : RepeatKillerAvoidDuration);
                _avoidClasses[killerClass] = expires;
                OnMemoryChanged?.Invoke();
                _mapDangerLastLevel = -1; // re-score maps now that this class has killed us
                var context = DateTime.UtcNow - _lastFleeAt < TimeSpan.FromSeconds(15)
                    ? $" Was fleeing: {_lastFleeAction}" : "";
                if (fresh)
                    OnLog?.Invoke($"Died to '{killerName}' (class {killerClass}) — avoiding it for the next {AvoidClassDuration.TotalMinutes:F0} min.{context}");
                else
                    OnLog?.Invoke($"Died to '{killerName}' (class {killerClass}) again — avoiding it for the next {RepeatKillerAvoidDuration.TotalMinutes:F0} min.{context}");
            }
            else
                OnLog?.Invoke($"Died (couldn't attribute the kill; last attacker '{attackerName}', committed target '{TargetName}').");
            // Every death is a lost duel, including an ambush while travelling (no Engaging line before it).
            var duel = _duel;
            _duel = null;
            var killer = killerName.Length > 0 ? killerName : attackerName ?? "unknown";
            OnLog?.Invoke(duel != null
                ? $"Duel lost: {duel.Name} engaged {(DateTime.UtcNow - duel.At).TotalSeconds:F1}s ago, killed by {killer} — HP -{duel.Hp}, SP -{Math.Max(0, duel.Sp - snap.SelfSp)}."
                : $"Duel lost: ambushed by {killer} while {modeBefore.ToString().ToLowerInvariant()} — HP -{snap.SelfMaxHp}.");
            ClearTarget();
            await _bot.StopAsync(ct);
        }
        if (DateTime.UtcNow >= _nextRespawn)
        {
            // Quicker respawn retry — 1.5s instead of 3s. The server rejects respawn requests during
            // the death-animation lockout anyway, so retrying sooner just shortens the wait once the
            // server is ready to accept it.
            _nextRespawn = DateTime.UtcNow.AddSeconds(1.5);
            _respawnPending = true;
            await _bot.RespawnAsync(inPlace: false, ct);
        }
    }
}
