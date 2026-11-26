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

    /// <summary>Fly Wing first (same map, cheap); a Butterfly Wing (back to the save point) only when the caller allows it.
    /// 0 when neither is usable.</summary>
    // Stuck/confined/trapped isn't danger: like travel shortcuts it only spends Fly Wings above the emergency half of the
    // pool (Bot2 burned every wing unsticking on mjo_dun02, then died cornered at orcsdun02 with none).
    private int PickEscapeWing(bool butterflyOk) =>
        HasUsableItem(_config.FlyWingItemId) && CountInInventory(_config.FlyWingItemId) > EmergencyFlyWings() ? _config.FlyWingItemId
        : butterflyOk && HasUsableItem(_config.ButterflyWingItemId) ? _config.ButterflyWingItemId : 0;

    private string WingName(int itemId) => itemId == _config.FlyWingItemId ? "Fly Wing" : "Butterfly Wing";

    private bool EscapeWingReady => DateTime.UtcNow >= _nextEscapeItem;

    /// <summary>Every wing escape goes through here, so they all share one throttle.</summary>
    private async Task UseEscapeWingAsync(int itemId, double throttleSeconds, CancellationToken ct)
    {
        _nextEscapeItem = DateTime.UtcNow.AddSeconds(throttleSeconds);
        await _bot.UseInventoryItemAsync(itemId, -1, ct);
    }

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
        var (fly, butterfly) = (HasUsableItem(_config.FlyWingItemId), HasUsableItem(_config.ButterflyWingItemId));
        var item = PickEscapeWing(confined >= ConfinedButterflyWing);
        if (!_confinedLogged)
        {
            _confinedLogged = true;
            var planned = PlannedPath is { } p && p.Map == snap.Map ? string.Join(" ", p.Cells.Take(8).Select(c => $"({c.x},{c.y})")) : "none";
            OnLog?.Invoke($"Stuck: nudging for {confined.TotalSeconds:F0}s without leaving {ConfineCells} cells of ({a.at.X},{a.at.Y}) on {snap.Map} "
                + $"(mode {Mode}, planned path {planned}, {HazardZones().Count} hazard zone(s)); "
                + (fly ? "using a Fly Wing." : butterfly ? $"no Fly Wing, Butterfly Wing at {ConfinedButterflyWing.TotalSeconds:F0}s." : "no Fly Wing or Butterfly Wing to escape with."));
        }
        if (item == 0 || !EscapeWingReady) return false;
        OnLog?.Invoke($"Stuck: using a {WingName(item)} after {confined.TotalSeconds:F0}s confined near ({a.at.X},{a.at.Y}).");
        await UseEscapeWingAsync(item, 5, ct);
        _nudgeAnchor = default;
        return true;
    }

    private async Task<bool> TryEscapeStuckAsync(Snapshot snap, CancellationToken ct)
    {
        if (!_config.AutoEscapeWhenStuck || _data == null) return false;
        if (!EscapeWingReady) return false;

        var stuckSecs = (DateTime.UtcNow - _lastProgressAt).TotalSeconds;
        if (stuckSecs < _config.FlyWingStuckSeconds) return false;

        // Prefer Fly Wing first — same map, cheap, fast. Fall back to Butterfly Wing only after a longer
        // wait (we've either run out of Fly Wings or none were in the bag in the first place).
        var itemId = PickEscapeWing(stuckSecs >= _config.ButterflyWingStuckSeconds);
        if (itemId == 0) return false;

        OnLog?.Invoke($"Stuck for {stuckSecs:F0}s — using {WingName(itemId)} to escape.");
        await UseEscapeWingAsync(itemId, 5, ct); // throttle so we don't blow the whole stack at once
        ResetStuck(snap.SelfPos);
        return true;
    }

    // Dropped inside a wall (bad teleport coordinate): the server can't path out of a blocked cell, so
    // every walk is silently ignored. A Fly Wing is the only way out.
    private async Task<bool> TryEscapeWallAsync(Snapshot snap, bool stuck, CancellationToken ct)
    {
        if (stuck && snap.SelfPos is not { X: 0, Y: 0 } // (0,0) = position not received yet after a map change
            && EscapeWingReady && _data?.GetWalkMap(snap.Map) is { } here
            && !here.IsWalkable(snap.SelfPos.X, snap.SelfPos.Y)
            && HasUsableItem(_config.FlyWingItemId))
        {
            OnLog?.Invoke($"Standing in a wall at ({snap.SelfPos.X},{snap.SelfPos.Y}) on {snap.Map} — using a Fly Wing to get out.");
            await UseEscapeWingAsync(_config.FlyWingItemId, 3, ct);
            return true;
        }
        return false;
    }

    /// <summary>Wing-escape regardless of <see cref="BotBehaviorConfig.AutoEscapeWhenStuck"/> — the
    /// trap branch knows the bot has zero forward path and Fly Wing is the only recovery short of a
    /// server restart. Throttled so we don't fire one every tick.</summary>
    private async Task TryTrapWingEscapeAsync(double trapSecs, CancellationToken ct)
    {
        if (!EscapeWingReady) return;
        // Find what we have. Fly Wing first; Butterfly Wing only after the longer threshold so we don't
        // throw the bot back to save point on a 5-second trap that Fly Wing alone could solve.
        var pick = PickEscapeWing(trapSecs >= 15.0);
        if (pick == 0 && trapSecs >= 15.0 && HasUsableItem(_config.FlyWingItemId)) pick = _config.FlyWingItemId; // no walking out: the reserve is all that's left
        if (pick == 0) return;
        OnLog?.Invoke($"Sub-region trap escape: using {WingName(pick)} (trapped {trapSecs:F0}s).");
        await UseEscapeWingAsync(pick, 3, ct);
    }
}
