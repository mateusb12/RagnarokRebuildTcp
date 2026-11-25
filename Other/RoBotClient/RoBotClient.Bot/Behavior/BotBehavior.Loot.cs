using RebuildSharedData.ClientTypes;
using RebuildSharedData.Data;
using RebuildSharedData.Enum;
using RoBotClient.Bot.GameData;
using RoBotClient.Bot.Session;
using RoBotClient.Bot.State;

namespace RoBotClient.Bot.Behavior;

public sealed partial class BotBehavior
{
    private DateTime _nextLoot = DateTime.MinValue;
    private int _lootTargetId;
    private readonly HashSet<int> _skipDrops = new();

    private async Task<bool> TickLootAsync(Snapshot snap, bool stuck, CancellationToken ct)
    {
        if (!_config.AutoLoot) return false;
        var drop = _bot.WithState(w => NearestLoot(w, snap.Map, snap.SelfPos));
        if (drop != 0)
        {
            Mode = BotMode.Looting;
            if (drop != _lootTargetId) { _lootTargetId = drop; ResetStuck(snap.SelfPos); _nextLoot = DateTime.MinValue; }
            else if (stuck) { _skipDrops.Add(drop); _lootTargetId = 0; ResetStuck(snap.SelfPos); await NudgeAsync(snap, ct); return true; }
            if (DateTime.UtcNow >= _nextLoot)
            {
                _nextLoot = DateTime.UtcNow.AddSeconds(_config.LootCooldownSeconds);
                await _bot.PickUpAsync(drop, ct);
            }
            return true;
        }
        _lootTargetId = 0;
        return false;
    }

    private int NearestLoot(WorldState w, string map, Position self)
    {
        var bestId = 0;
        var bestDist = int.MaxValue;
        foreach (var g in w.GroundItems.Values)
        {
            if (_config.LootBlacklist.Contains(g.ItemId)) continue;
            if (_skipDrops.Contains(g.DropId)) continue;
            if (CellNearHazard(g.X, g.Y)) continue; // don't walk into danger for loot
            // Nor into a warp: a Zenorc's drop by orcsdun02's landing cell bounced Bot1 orcsdun02 <-> in_orcs01, the pickup
            // walk crossing the warp each time it came back.
            if (_data != null && (_data.World.IsInPortalFootprint(map, g.X, g.Y, margin: 1) || PathCrossesPortal(map, self, g.X, g.Y))) continue;
            var d = Dist(g.X, g.Y, self.X, self.Y);
            if (d > _config.LootRange || d >= bestDist) continue;
            bestDist = d;
            bestId = g.DropId;
        }
        return bestId;
    }
}
