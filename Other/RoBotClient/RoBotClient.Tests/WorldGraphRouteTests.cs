using RoBotClient.Bot.GameData;
using Xunit.Abstractions;

namespace RoBotClient.Tests;

public class WorldGraphRouteTests(ITestOutputHelper output)
{
    private static readonly string Repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", ".."));

    private static readonly Lazy<GameDatabase> Db = new(() => GameDatabase.Load(
        Path.Combine(Repo, "RebuildClient", "Assets", "StreamingAssets", "ClientConfigGenerated"),
        Path.Combine(Repo, "RebuildClient", "Assets", "Maps", "exportdata"),
        Path.Combine(Repo, "RoRebuildServer", "GameConfig", "ServerData", "Script", "Warps")));

    [Theory]
    [InlineData("prt_fild01")]
    [InlineData("prt_maze01")]
    [InlineData("prt_maze02")]
    [InlineData("prt_maze03")]
    [InlineData("xmas_dun02")]
    [InlineData("ama_dun01")]
    public void MapIsReachableFromProntera(string map)
    {
        var world = Db.Value.World;
        var entry = world.EntryCell(map);
        Assert.NotNull(entry);
        var plan = world.Plan("prontera", 156, 191, map, entry.Value.X, entry.Value.Y);
        output.WriteLine($"{map} entry {entry}: " + (plan == null ? "no route" : string.Join(" -> ", plan.Edges.Select(e => $"{e.SrcMap}({e.Sx},{e.Sy})>{e.DstMap}"))));
        Assert.NotNull(plan);
    }
}
