using RebuildSharedData.ClientTypes;
using RebuildSharedData.Data;
using RebuildSharedData.Enum;
using RoBotClient.Bot.GameData;
using RoBotClient.Bot.Session;
using RoBotClient.Bot.State;

namespace RoBotClient.Bot.Behavior;

public sealed partial class BotBehavior
{
    private DateTime _nextHeal = DateTime.MinValue;
    private bool _resting;
    private DateTime _nextSit = DateTime.MinValue;

    private async Task MaybeHealAsync(Snapshot snap, CancellationToken ct)
    {
        if (_data == null || _config.HealHpPercent <= 0 || snap.SelfMaxHp <= 0) return;
        if ((double)snap.SelfHp / snap.SelfMaxHp > _config.HealHpPercent) return;
        // Out of a fight the bot rests for free; items are for when something is hitting us.
        if (_config.RestWhenNoPotions && _targetId == 0 && !AnyAdjacentMonster(snap) && _hazards.Count == 0) return;
        if (DateTime.UtcNow < _nextHeal) return;
        var itemId = _bot.WithState(FindHealItem);
        if (itemId <= 0) return;
        _nextHeal = DateTime.UtcNow.AddSeconds(_config.HealCooldownSeconds);
        await _bot.UseInventoryItemAsync(itemId, -1, ct);
    }

    // In a fight, keep any worthwhile buff up (Concentration Potion), strongest held first, drops before bought ones.
    private DateTime _nextBuff;
    private async Task MaybeDrinkBuffAsync(Snapshot snap, CancellationToken ct)
    {
        if (_data == null || _targetId == 0 || DateTime.UtcNow < _nextBuff) return;
        var pick = HuntPolicy.BuffToDrink(_data, _config, snap.SelfLevel, snap.SelfJobId, _bot.WithState(w => w.Self.UnequippedCounts()), snap.SelfStatuses);
        if (pick == null) return;
        _nextBuff = DateTime.UtcNow.AddSeconds(5); // the status packet lands after the use
        OnLog?.Invoke($"Buff: drinking {_data.ItemName(pick.Id)} ({pick.Status}).");
        await _bot.UseInventoryItemAsync(pick.Id, -1, ct);
    }

    // HuntPolicy.HealItem: shared with the offline HuntSimulator.
    private int FindHealItem(WorldState w) => _data == null ? 0 : HuntPolicy.HealItem(_data, _config, w.Self.UnequippedCounts());

    // Sit to recover HP/SP when we're hurt and out of usable healing items. Stands back up once nearly full,
    // a monster comes within reach (sitting is cancelled on taking damage anyway), or a heal item appears.
    private async Task<bool> TickRestAsync(Snapshot snap, CancellationToken ct)
    {
        // The sit/stand rules are HuntPolicy's, shared with the offline HuntSimulator.
        var f = HuntPolicy.RestFractions(_config, snap.SelfJobId, snap.SelfHp, snap.SelfMaxHp, snap.SelfSp, snap.SelfMaxSp);
        // Only a monster that will attack ends the rest: a passive Horn wandering by used to stand Bot3 up at low HP.
        var threatened = HuntPolicy.Threatened(_data, snap.Monsters, snap.SelfPos, _bot.HasHitMe, _hazards.Count);

        if (_resting)
        {
            // HP only climbs while sitting, so any drop means something (a provoked passive mob) is hitting us.
            var hit = snap.SelfHp < _restHp;
            _restHp = snap.SelfHp;
            if (HuntPolicy.ShouldStand(_config, f, _restToWin, hit, threatened))
            {
                _resting = false;
                _restToWin = false;
                if (snap.SelfSitting) await _bot.SitAsync(false, ct);
                return false;
            }
            Mode = BotMode.Resting;
            if (!snap.SelfSitting && DateTime.UtcNow >= _nextSit) // server stood us up but we're still safe + low
            {
                _nextSit = DateTime.UtcNow.AddSeconds(1.5);
                await _bot.SitAsync(true, ct);
            }
            return true;
        }

        // Resting to win: something in view is beatable at full HP but not as hurt as we are. Sitting is free, while
        // wandering at 77% HP turned every Spore on pay_fild02 into "too strong" (81 s searching per kill).
        var wantRest = HuntPolicy.LowOnHpSp(_config, f);
        // Never sit with a monster we can't beat in view (Bot3 sat down among Greatest Generals on moc_fild03).
        if (!HuntPolicy.ShouldSit(_config, f, _restToWin, CanSit(snap), threatened)) return false;

        _resting = true;
        _restHp = snap.SelfHp;
        ClearTarget();
        Mode = BotMode.Resting;
        _nextSit = DateTime.UtcNow.AddSeconds(1.5);
        OnLog?.Invoke(wantRest ? "Low on HP/SP — sitting to rest." : "Too hurt to win the fights in view — sitting to recover first.");
        await _bot.SitAsync(true, ct);
        return true;
    }

    private bool _restToWin; // the last target pick found fights we'd win at full HP but not now
    private int _restHp;

    private bool CanSit(Snapshot snap) => snap.SelfJobId != 0 || HuntPolicy.CanSit(0, _bot.WithState(w =>
        w.Self.KnownSkills.Where(k => k.Skill == CharacterSkill.BasicMastery).Select(k => (int)k.Level).DefaultIfEmpty(0).Max()));

    private bool AnyAdjacentMonster(Snapshot snap, int range = 3, bool aggressiveOnly = false) =>
        HuntPolicy.AnyAdjacentMonster(_data, snap.Monsters, snap.SelfPos, range, aggressiveOnly, _bot.HasHitMe);
}
