using RebuildSharedData.Data;

namespace RoBotClient.Bot.Behavior;

// Stuck-escape escalation: when the bot has been making no progress for substantially longer than the
// normal StuckSeconds threshold (so NudgeAsync's random-walk recovery clearly hasn't worked), reach for a
// Fly Wing (random same-map teleport) and ultimately a Butterfly Wing (return to save point). Both are
// validated against the item DB as Useable, and both are throttled together by a single _nextEscapeItem
// timer so we don't burn through the stack on a single bad tile.
public sealed partial class BotBehavior
{
    private DateTime _nextEscapeItem = DateTime.MinValue;

    // Nudging only proves the bot can move, not that it gets anywhere: Bot2 and Bot4 nudged for hours within a few cells
    // of beach_dun3 (147,47), and every nudge reset the progress clock TryEscapeStuckAsync waits on, so no wing was ever
    // used. Consecutive nudges that never leave ConfineCells of where they started are the signal; it holds across every
    // NudgeAsync caller (travel, hunting, loot, shop, commands).
    private (string map, Position at, DateTime since, DateTime last) _nudgeAnchor;
    private bool _confinedLogged;
    private const int ConfineCells = 10;
    private static readonly TimeSpan ConfinedFlyWing = TimeSpan.FromSeconds(30), ConfinedButterflyWing = TimeSpan.FromSeconds(60);

    /// <summary>True when it used a wing instead of nudging.</summary>
    private async Task<bool> EscapeConfinementAsync(Snapshot snap, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var a = _nudgeAnchor;
        if (a.map != snap.Map || now - a.last > TimeSpan.FromSeconds(15) || Dist(snap.SelfPos, a.at) > ConfineCells)
        {
            _nudgeAnchor = (snap.Map, snap.SelfPos, now, now);
            _confinedLogged = false;
            return false;
        }
        _nudgeAnchor.last = now;
        var confined = now - a.since;
        if (confined < ConfinedFlyWing) return false;
        var (fly, butterfly) = (CountInInventory(_config.FlyWingItemId) > 0 && _data?.IsUsableItem(_config.FlyWingItemId) == true,
            CountInInventory(_config.ButterflyWingItemId) > 0 && _data?.IsUsableItem(_config.ButterflyWingItemId) == true);
        var item = fly ? _config.FlyWingItemId : butterfly && confined >= ConfinedButterflyWing ? _config.ButterflyWingItemId : 0;
        if (!_confinedLogged)
        {
            _confinedLogged = true;
            var planned = PlannedPath is { } p && p.Map == snap.Map ? string.Join(" ", p.Cells.Take(8).Select(c => $"({c.x},{c.y})")) : "none";
            OnLog?.Invoke($"Stuck: nudging for {confined.TotalSeconds:F0}s without leaving {ConfineCells} cells of ({a.at.X},{a.at.Y}) on {snap.Map} "
                + $"(mode {Mode}, planned path {planned}, {HazardZones().Count} hazard zone(s)); "
                + (fly ? "using a Fly Wing." : butterfly ? $"no Fly Wing, Butterfly Wing at {ConfinedButterflyWing.TotalSeconds:F0}s." : "no Fly Wing or Butterfly Wing to escape with."));
        }
        if (item == 0 || now < _nextEscapeItem) return false;
        _nextEscapeItem = now.AddSeconds(5);
        OnLog?.Invoke($"Stuck: using a {(item == _config.FlyWingItemId ? "Fly Wing" : "Butterfly Wing")} after {confined.TotalSeconds:F0}s confined near ({a.at.X},{a.at.Y}).");
        await _bot.UseInventoryItemAsync(item, -1, ct);
        _nudgeAnchor = default;
        return true;
    }

    private async Task<bool> TryEscapeStuckAsync(Snapshot snap, CancellationToken ct)
    {
        if (!_config.AutoEscapeWhenStuck || _data == null) return false;
        if (DateTime.UtcNow < _nextEscapeItem) return false;

        var stuckSecs = (DateTime.UtcNow - _lastProgressAt).TotalSeconds;
        if (stuckSecs < _config.FlyWingStuckSeconds) return false;

        var (hasFly, hasButterfly) = _bot.WithState(w =>
        {
            var fly = false; var bfly = false;
            foreach (var it in w.Self.Inventory)
            {
                if (it.Count <= 0) continue;
                if (it.ItemId == _config.FlyWingItemId) fly = true;
                else if (it.ItemId == _config.ButterflyWingItemId) bfly = true;
            }
            return (fly, bfly);
        });

        // Prefer Fly Wing first — same map, cheap, fast. Fall back to Butterfly Wing only after a longer
        // wait (we've either run out of Fly Wings or none were in the bag in the first place).
        int itemId;
        string itemName;
        if (hasFly && _data.IsUsableItem(_config.FlyWingItemId))
        {
            itemId = _config.FlyWingItemId;
            itemName = "Fly Wing";
        }
        else if (hasButterfly && stuckSecs >= _config.ButterflyWingStuckSeconds && _data.IsUsableItem(_config.ButterflyWingItemId))
        {
            itemId = _config.ButterflyWingItemId;
            itemName = "Butterfly Wing";
        }
        else
        {
            return false;
        }

        OnLog?.Invoke($"Stuck for {stuckSecs:F0}s — using {itemName} to escape.");
        _nextEscapeItem = DateTime.UtcNow.AddSeconds(5); // throttle so we don't blow the whole stack at once
        await _bot.UseInventoryItemAsync(itemId, -1, ct);
        ResetStuck(snap.SelfPos);
        return true;
    }
}
