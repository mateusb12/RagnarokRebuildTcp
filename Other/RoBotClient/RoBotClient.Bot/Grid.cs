global using static RoBotClient.Bot.Grid;
using RebuildSharedData.Data;

namespace RoBotClient.Bot;

/// <summary>Cell distance on the RO grid: a diagonal step costs the same as a straight one (Chebyshev).
/// Imported project-wide (global using static), so callers just write <c>Dist(...)</c>.</summary>
public static class Grid
{
    public static int Dist(int x0, int y0, int x1, int y1) => Math.Max(Math.Abs(x0 - x1), Math.Abs(y0 - y1));
    public static int Dist(Position a, Position b) => Dist(a.X, a.Y, b.X, b.Y);
}
