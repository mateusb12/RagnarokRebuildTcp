namespace RoBotClient.Bot.GameData;

/// <summary>One kafra NPC + its full teleport menu. Matches the kafradatabase.json structure written by
/// the server-side <c>KafraExport</c>. Kept independent of the shared <c>NpcEntry</c> type so adding
/// kafra integration doesn't require rebuilding <c>RebuildSharedData.dll</c>.</summary>
public sealed class KafraEntry
{
    public int Id { get; set; }
    public string Map { get; set; } = "";
    public string Name { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    /// <summary>Option-index of "Teleport Service" in the kafra's first menu. 2 for @Kafra (Save, Use
    /// Storage, Teleport, [cart], Cancel), 1 for @KafraNoSave (Use Storage, Teleport, Cancel).</summary>
    public int TopMenuOption { get; set; }
    public List<KafraWarp> Warps { get; set; } = new();
}

/// <summary>One destination offered by a kafra. <see cref="OptionIndex"/> is the index the bot must
/// pass to <c>NpcSelectOptionAsync</c> in the destination menu (the second menu after picking
/// "Teleport Service" in the kafra's first menu).</summary>
public sealed class KafraWarp
{
    public string DestMap { get; set; } = "";
    public int DestX { get; set; }
    public int DestY { get; set; }
    public int Zeny { get; set; }
    public string OptionLabel { get; set; } = "";
    public int OptionIndex { get; set; }
}

/// <summary>Script NPCs that teleport (MoveTo) after one menu pick, so the world graph can route through them. They ride
/// the Kafra edge and dialog plumbing with WorldGraph.KafraEdgeInfo.NoSecondMenu as the destination option. Hand-copied from
/// RoRebuildServer/GameConfig/ServerData/Script/Npcs; update here when those scripts move an NPC or change a fare.</summary>
public static class NpcLinks
{
    // ponytail: only the links hunting maps need; the Izlude/Alberta boats (Byalan, Turtle Island, Sunken Ship) can join when wanted.
    public static readonly KafraEntry[] All =
    [
        Link("alberta", "Sea Captain", 245, 93, 0, "amatsu", 197, 83, 3000),      // Npcs/Alberta.txt "Overseas Captain"
        Link("ama_in02", "Jyuichiro", 115, 177, 1, "ama_dun01", 229, 10, 0),     // Npcs/Amatsu.txt "Enter the gate"
        Link("ama_dun01", "Jyuichiro", 229, 7, 0, "ama_in02", 119, 181, 0),      // Npcs/Amatsu.txt "Return"
        Link("aldebaran", "Christmas Link", 223, 222, 0, "xmas_fild01", 83, 72, 0), // Npcs/Aldebaran.txt Santa to Lutie
    ];

    private static KafraEntry Link(string map, string name, int x, int y, int option, string dest, int dx, int dy, int zeny) => new()
    {
        Map = map, Name = name, X = x, Y = y, TopMenuOption = option,
        Warps = [new KafraWarp { DestMap = dest, DestX = dx, DestY = dy, Zeny = zeny, OptionLabel = $"{name} to {dest}", OptionIndex = WorldGraph.KafraEdgeInfo.NoSecondMenu }],
    };
}

public sealed class KafraDbFile
{
    public List<KafraEntry> Items { get; set; } = new();
}
