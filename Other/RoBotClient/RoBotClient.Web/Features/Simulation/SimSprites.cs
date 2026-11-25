using System.Text.Json;
using System.Text.RegularExpressions;
using RoBotClient.Bot.Offline;
using RebuildSharedData.Enum;
using RoBotClient.Simulation;

namespace RoBotClient.Web.Features.Simulation;

/// <summary>
/// What the Simulation tab draws the bot with (visual only; nothing here feeds the simulation). Resolved the way the
/// Unity client does it (ClientDataLoader.LoadAndAttachWeapon / LoadAndAttachEquipmentSprite, EntityBuilder):
/// body and head from the imported sprites (MonsterSprites.GetPlayerAnim), headgears / shield / weapon (+ its swing
/// trail) as raw .spr/.act under Assets/Sprites (most headgear was never imported), decoded in the browser
/// (sim-sprites.js). Attack motion per job + weapon class from jobweaponinfo.json; the caster's motion per skill read
/// from the client's skill handlers.
/// </summary>
public sealed class SimSprites(string assetsDir, MonsterSprites sprites)
{
    private readonly string _sprites = Path.Combine(assetsDir, "Sprites");
    private string Config(string name) => Path.Combine(assetsDir, "StreamingAssets", "ClientConfigGenerated", name);

    private sealed record Item(int Id, string Code, int SubType, int ItemClass);
    private sealed record JobWeapon(int Job, int Class, int Class2, int AttackMale, int AttackFemale, string SpriteMale, string EffectMale, string SpriteFemale, string EffectFemale);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private Lazy<Dictionary<int, Item>> Items => _items ??= new(() => Read<Item>("items.json").ToDictionary(i => i.Id));
    private Lazy<Dictionary<int, Item>>? _items;
    private Lazy<List<JobWeapon>> Weapons => _weapons ??= new(() => Read<JobWeapon>("jobweaponinfo.json"));
    private Lazy<List<JobWeapon>>? _weapons;
    private Lazy<Dictionary<int, string>> JobNames => _jobs ??= new(() => Read<JsonElement>("playerclass.json")
        .ToDictionary(j => j.GetProperty("Id").GetInt32(), j => j.GetProperty("Name").GetString() ?? ""));
    private Lazy<Dictionary<int, string>>? _jobs;
    private Lazy<Dictionary<string, string>> Display => _display ??= new(() => File.ReadLines(Config("displaySpriteTable.txt"))
        .Select(l => l.Split('\t')).Where(p => p.Length == 2).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1]));
    private Lazy<Dictionary<string, string>>? _display;
    private Lazy<Dictionary<string, SkillMotion>> Motions => _motions ??= new(ReadSkillMotions);
    private Lazy<Dictionary<string, SkillMotion>>? _motions;

    private List<T> Read<T>(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Config(file)));
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement : doc.RootElement.GetProperty("Items");
        return items.EnumerateArray().Select(e => e.Deserialize<T>(Json)!).ToList();
    }

    /// <summary>Raw sprite part: url without extension (the browser fetches .spr and .act), sort order as the client's SpriteOrder.</summary>
    public sealed record Part(string Url, int Order, string What);
    /// <summary>The caster's motion: on execution ("attack" Attack1/2/3, "skill" Casting, "throw" Throwing, "none" keeps
    /// what it was doing) and while a cast bar runs ("casting" paused Casting, "attack" paused attack, "standby").</summary>
    public sealed record SkillMotion(string Execute, string Cast);

    public object? Player(HuntReplay rp, Appearance? live)
    {
        var look = rp.Look ?? live ?? new Appearance(true, 0, 0);
        var lookFrom = rp.Look != null ? "recorded with the build" : live != null ? "the character as it is logged in now" : "not recorded: default look";
        var job = rp.BuildSpec?.JobId ?? (Regex.Match(rp.Build, @"job (\d+)") is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0);
        if (sprites.GetPlayerAnim(look.IsMale, job, look.HeadType, look.HairColor) is not { } anim) return null;
        IEnumerable<EquippedItem> gear = rp.BuildSpec?.Equipment ?? [];
        int Id(EquipSlot s) => gear.FirstOrDefault(e => e.Slot == s).ItemId;
        var parts = new List<Part>();
        var jobName = JobNames.Value.GetValueOrDefault(job, "Novice");
        var g = look.IsMale ? "Male" : "Female";
        var mf = look.IsMale ? "M" : "F";

        // Headgears: upper 4, mid 3, lower 2 (one item covering two slots is drawn once); shield 1 (EntityBuilder).
        var seen = new HashSet<int>();
        foreach (var (slot, order) in new[] { (EquipSlot.HeadTop, 4), (EquipSlot.HeadMid, 3), (EquipSlot.HeadBottom, 2) })
            if (Id(slot) is > 0 and var id && seen.Add(id) && Sprite(id) is { } s)
                AddPart($"Headgear/{g}/{(look.IsMale ? "남" : "여")}_{s}", order, Items.Value[id].Code);
        var left = Id(EquipSlot.LeftHand);
        var offHand = left > 0 && Items.Value.TryGetValue(left, out var l) && l.ItemClass == 2 ? l.SubType : 0;
        if (left > 0 && offHand == 0 && Sprite(left) is { } sh)
            AddPart($"Shields/{jobName}/{g}/{jobName}_{mf}_{sh}", 1, Items.Value[left].Code);

        // Weapon 8 and its swing trail 5: the item's own sprite if it has one, else the job's default for the weapon class.
        var right = Id(EquipSlot.RightHand);
        var weaponClass = right > 0 && Items.Value.TryGetValue(right, out var w) && w.ItemClass == 2 ? w.SubType : 0;
        if (weaponClass == 0) weaponClass = offHand; else if (offHand > 0) weaponClass += offHand << 8;
        var jw = Weapons.Value.FirstOrDefault(x => x.Job == job && x.Class + (x.Class2 > 0 ? x.Class2 << 8 : 0) == weaponClass);
        if (weaponClass != 0)
        {
            var own = right > 0 && Sprite(right) is { } ws ? $"Weapons/{jobName}/{g}/{jobName}_{mf}_{ws}" : null;
            if (own != null && File.Exists(Path.Combine(_sprites, own + ".spr"))) AddPart(own, 8, Items.Value[right].Code);
            else if (jw != null) AddPart(Strip(look.IsMale ? jw.SpriteMale : jw.SpriteFemale), 8, "weapon class " + weaponClass);
            if (jw != null) AddPart(Strip(look.IsMale ? jw.EffectMale : jw.EffectFemale), 5, "swing trail");
        }
        return new
        {
            body = anim.Body, head = anim.Head, parts,
            attackMotion = jw == null ? 1 : look.IsMale ? jw.AttackMale : jw.AttackFemale, weaponClass,
            skills = Motions.Value, look = lookFrom,
        };

        void AddPart(string rel, int order, string what)
        {
            if (rel.Length > 0 && File.Exists(Path.Combine(_sprites, rel + ".spr")) && File.Exists(Path.Combine(_sprites, rel + ".act")))
                parts.Add(new Part("sim-sprite/" + string.Join('/', rel.Split('/').Select(Uri.EscapeDataString)), order, what));
        }
        string? Sprite(int id) => Items.Value.TryGetValue(id, out var it) && Display.Value.TryGetValue(it.Code, out var s) ? s : null;
        static string Strip(string p) => p.StartsWith("Assets/Sprites/") && p.EndsWith(".spr") ? p[15..^4] : "";
    }

    /// <summary>Raw .spr / .act under Assets/Sprites, for sim-sprites.js.</summary>
    public IResult Serve(string path)
    {
        var full = Path.GetFullPath(Path.Combine(_sprites, path));
        var ext = Path.GetExtension(full);
        return full.StartsWith(_sprites + Path.DirectorySeparatorChar) && ext is ".spr" or ".act" && File.Exists(full)
            ? Results.File(full, "application/octet-stream") : Results.NotFound();
    }

    // The client has no skill -> motion table: each handler under Scripts/SkillHandlers/Handlers calls PerformSkillMotion,
    // PerformBasicAttackMotion or PerformThrowMotion in ExecuteSkillTargeted (SkillHandlerBase's default: basic attack),
    // and may hold a pose in StartSkillCasting (PacketStartCasting's default: Casting, paused). Skills with no handler
    // run DefaultSkillHandler (basic attack; Standby while casting).
    private Dictionary<string, SkillMotion> ReadSkillMotions()
    {
        var map = new Dictionary<string, SkillMotion>();
        var dir = Path.Combine(assetsDir, "Scripts", "SkillHandlers", "Handlers");
        if (!Directory.Exists(dir)) return map;
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var src = File.ReadAllText(file);
            var skills = Regex.Matches(src, @"\[SkillHandler\(CharacterSkill\.(\w+)").Select(x => x.Groups[1].Value).ToList();
            if (skills.Count == 0) continue;
            var exec = Method(src, "ExecuteSkillTargeted") is { } e
                ? Regex.Match(e, "Perform(Skill|BasicAttack|Throw)Motion") is { Success: true } p ? p.Groups[1].Value switch { "Skill" => "skill", "Throw" => "throw", _ => "attack" } : "none"
                : "attack";
            var cast = Method(src, "StartSkillCasting") is { } c
                ? c.Contains("HoldAttackMotionForCast") ? "attack" : c.Contains("SpriteMotion.Standby") ? "standby" : "casting"
                : "casting";
            foreach (var s in skills) map[s] = new SkillMotion(exec, cast);
        }
        map["*"] = new SkillMotion("attack", "standby"); // DefaultSkillHandler
        return map;

        static string? Method(string src, string name) // the method's text up to the next override (or the end)
        {
            var i = src.IndexOf("override void " + name, StringComparison.Ordinal);
            if (i < 0) return null;
            var next = src.IndexOf(" override ", i + 10, StringComparison.Ordinal);
            return next < 0 ? src[i..] : src[i..next];
        }
    }
}
