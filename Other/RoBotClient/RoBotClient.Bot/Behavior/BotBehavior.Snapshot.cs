using RebuildSharedData.ClientTypes;
using RebuildSharedData.Data;
using RebuildSharedData.Enum;
using RoBotClient.Bot.GameData;
using RoBotClient.Bot.Session;
using RoBotClient.Bot.State;

namespace RoBotClient.Bot.Behavior;

public sealed partial class BotBehavior
{
    private static MonsterInfo? FindMonster(Snapshot snap, int id)
    {
        foreach (var m in snap.Monsters)
            if (m.Id == id) return m;
        return null;
    }

    /// <summary>Nearest NPC within 3 cells of (x, y), or 0: the shop or job NPC we walked to.</summary>
    private static int FindNpcNear(WorldState w, int x, int y)
    {
        var best = 0;
        var bestD = int.MaxValue;
        foreach (var e in w.Entities.Values)
        {
            if (!e.IsNpc) continue;
            var d = Math.Max(Math.Abs(e.Position.X - x), Math.Abs(e.Position.Y - y));
            if (d <= 3 && d < bestD) { bestD = d; best = e.Id; }
        }
        return best;
    }

    private static int Dist(Position a, Position b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    internal sealed record MonsterInfo(int Id, int ClassId, string Name, Position Pos, int Hp, int MaxHp);

    internal sealed class Snapshot
    {
        public Snapshot Clone() => (Snapshot)MemberwiseClone(); // shallow: the lists are read-only after From()

        public int SelfId;
        public Position SelfPos;
        public int SelfHp;
        public int SelfMaxHp;
        public int SelfAtkMin;
        public int SelfAtkMax;
        public int SelfDef;
        public int SelfLevel;
        public int SelfJobId;
        public int SelfJobLevel;
        public int SelfSkillPoints;
        public int SelfVit;
        public int SelfInt;
        public int SelfMDef;
        public int SelfDex;
        public int SelfAgi;
        public int SelfHit;
        public int SelfFlee;
        public int SelfLuk;
        public int SelfAddCrit;     // server units (out of 1000); reflects LUK + gear + skills
        public int SelfMagicAtkMin;
        public int SelfMagicAtkMax;
        public double SelfAttackInterval;
        public int SelfWeaponItemId; // item in the weapon (right hand) slot, 0 = none
        public int SelfAmmoId; // loaded ammunition item id, 0 = none
        public int SelfAttackRange; // server-synced (CharacterStat.Range); 0 when not synced — caller falls back to skill heuristic
        public bool SelfDead;
        public int SelfSp;
        public int SelfMaxSp;
        public bool SelfSitting;
        public string Map = "";
        public readonly List<MonsterInfo> Monsters = new();
        public readonly List<(CharacterSkill Skill, int Level)> KnownSkills = new();
        public readonly HashSet<CharacterStatusEffect> SelfStatuses = new();
        // entityId → active status effects (still-current at snapshot time). Used by the skill DSL so a
        // rule like `use Curse 1 ally if target.status.poison == 0` can check what's on the ALLY/ENEMY.
        public readonly Dictionary<int, HashSet<CharacterStatusEffect>> EntityStatuses = new();

        private static int WeaponItemId(WorldState w)
        {
            var bag = w.Self.EquippedBagIds[(int)EquipSlot.RightHand];
            if (bag == 0) return 0;
            foreach (var item in w.Self.Inventory) if (item.BagId == bag) return item.ItemId;
            return 0;
        }

        public static Snapshot From(WorldState w)
        {
            var hasSelf = w.Entities.TryGetValue(w.Self.EntityId, out var self);
            var s = new Snapshot
            {
                SelfId = w.Self.EntityId,
                SelfPos = w.SelfPosition,
                SelfHp = hasSelf ? self!.Hp : w.Self.Hp,
                SelfMaxHp = hasSelf ? self!.MaxHp : w.Self.MaxHp,
                SelfAtkMin = w.Self.Attack,
                SelfAtkMax = Math.Max(w.Self.Attack, w.Self.AttackMax),
                SelfDef = w.Self.Def,
                SelfLevel = w.Self.Level,
                SelfJobId = w.Self.JobId,
                SelfJobLevel = w.Self.JobLevel,
                SelfSkillPoints = w.Self.SkillPoints,
                SelfVit = w.Self.Vit,
                SelfInt = w.Self.Int,
                SelfMDef = w.Self.MDef,
                SelfDex = w.Self.Dex,
                SelfAgi = w.Self.Agi,
                SelfHit = w.Self.Hit,
                SelfFlee = w.Self.Flee,
                SelfLuk = w.Self.Luk,
                SelfAddCrit = w.Self.AddCrit,
                SelfMagicAtkMin = w.Self.MagicAtkMin,
                SelfMagicAtkMax = w.Self.MagicAtkMax,
                SelfAttackInterval = w.Self.AttackSpeed,
                SelfAttackRange = w.Self.AttackRange,
                SelfWeaponItemId = WeaponItemId(w),
                SelfAmmoId = w.Self.AmmoId,
                SelfDead = hasSelf && self!.State == CharacterState.Dead,
                SelfSp = w.Self.Sp,
                SelfMaxSp = w.Self.MaxSp,
                SelfSitting = hasSelf && self!.State == CharacterState.Sitting,
                Map = w.Self.Map,
            };
            foreach (var e in w.Entities.Values)
                if (e.IsMonster && e.Id != s.SelfId)
                    s.Monsters.Add(new MonsterInfo(e.Id, e.ClassId, e.Name, e.EstimatedCell(), e.Hp, e.MaxHp));
            foreach (var k in w.Self.KnownSkills)
                s.KnownSkills.Add((k.Skill, k.Level));
            // Self status effects, filtered to still-active ones so a stale Apply doesn't haunt the DSL.
            var nowUtc = DateTime.UtcNow;
            if (w.Entities.TryGetValue(w.Self.EntityId, out var selfView))
            {
                foreach (var kv in selfView.Statuses)
                    if (kv.Value > nowUtc) s.SelfStatuses.Add(kv.Key);
            }
            // Per-entity status effects, for `target.status.<name>` lookups in the DSL.
            foreach (var e in w.Entities.Values)
            {
                if (e.Statuses.Count == 0) continue;
                HashSet<CharacterStatusEffect>? active = null;
                foreach (var kv in e.Statuses)
                {
                    if (kv.Value <= nowUtc) continue;
                    (active ??= new HashSet<CharacterStatusEffect>()).Add(kv.Key);
                }
                if (active != null) s.EntityStatuses[e.Id] = active;
            }
            return s;
        }
    }
}
