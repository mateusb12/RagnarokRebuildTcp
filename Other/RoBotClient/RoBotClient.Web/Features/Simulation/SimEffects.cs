using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RoBotClient.Web.Features.Simulation;

/// <summary>
/// The client's converted STR effects (Assets/Effects/{name}.asset, a StrAnimationFile ScriptableObject in Unity YAML) as
/// JSON for sim-effects.js: the keyframes as-is, the embedded atlas (mip 0) as top-down RGBA in base64, AtlasRects in
/// atlas pixels. Visual only.
/// </summary>
public sealed class SimEffects(string assetsDir)
{
    private readonly string _dir = Path.Combine(assetsDir, "Effects");
    private readonly ConcurrentDictionary<string, object?> _cache = new();

    public IResult Serve(string name) =>
        Regex.IsMatch(name, @"^[\w-]+$") && _cache.GetOrAdd(name, Load) is { } fx ? Results.Json(fx) : Results.NotFound();

    private object? Load(string name)
    {
        var path = Path.Combine(_dir, name + ".asset");
        if (!File.Exists(path)) return null;
        int w = 0, h = 0, fmt = 0, fr = 60, maxKey = 0;
        string? hex = null;
        var layers = new List<(List<int> tex, List<object> anims)>();
        var rects = new List<float[]>();
        Dictionary<string, object>? anim = null;
        string? list = null; var vecs = new List<float>(); // UVs / XY: four "- {x, y}" lines
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
        static float[] Nums(string s) => Regex.Matches(s, @"-?[\d.]+(?:[eE][-+]?\d+)?").Select(m => F(m.Value)).ToArray();
        string section = "";
        foreach (var raw in File.ReadLines(path))
        {
            if (!raw.StartsWith(' ')) { if (raw.EndsWith(':')) section = raw; continue; }
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            var key = colon > 0 ? line[..colon].TrimStart('-', ' ') : line;
            var val = colon > 0 ? line[(colon + 1)..].Trim() : "";
            if (section == "Texture2D:")
            {
                if (key == "m_Width") w = int.Parse(val); else if (key == "m_Height") h = int.Parse(val);
                else if (key == "m_TextureFormat") fmt = int.Parse(val); else if (key == "_typelessdata") hex = val;
                continue;
            }
            if (section != "MonoBehaviour:") continue;
            if (list != null && line.StartsWith("- {")) { vecs.AddRange(Nums(line)); if (vecs.Count == 8) { anim![list] = vecs.ToArray(); list = null; vecs.Clear(); } continue; }
            switch (key)
            {
                case "FrameRate": fr = int.Parse(val); break;
                case "MaxKey": maxKey = int.Parse(val); break;
                case "TextureCount" when line.StartsWith('-'): layers.Add(([], [])); break;
                case "Textures": // int32 little-endian, 8 hex chars each
                    for (var i = 0; i + 8 <= val.Length; i += 8) layers[^1].tex.Add(BitConverter.ToInt32(Convert.FromHexString(val.Substring(i, 8))));
                    break;
                case "Frame" when line.StartsWith('-'): layers[^1].anims.Add(anim = new() { ["frame"] = int.Parse(val) }); break;
                case "UVs" or "XY": list = key == "UVs" ? "uv" : "xy"; break;
                case "Type" or "Anitype" or "Aniframe" or "Delay" or "Angle" or "SrcAlpha" or "DstAlpha" when anim != null: anim[char.ToLower(key[0]) + key[1..]] = F(val); break;
                case "Position" or "Color" when anim != null: anim[key == "Position" ? "pos" : "color"] = Nums(val); break;
                case "serializedVersion" when line.StartsWith('-'): rects.Add([]); break; // an AtlasRects entry: x, y, width, height follow
                case "x" or "y" or "width" or "height" when rects.Count > 0 && rects[^1].Length < 4: rects[^1] = [.. rects[^1], F(val)]; break;
            }
        }
        if (hex == null || w == 0 || h == 0) return null;
        var bpp = fmt switch { 4 or 5 => 4, 3 => 3, _ => 0 };
        if (bpp == 0) return null;
        var src = Convert.FromHexString(hex.AsSpan(0, w * h * bpp * 2));
        var rgba = new byte[w * h * 4];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) // Unity rows are bottom-up
        {
            int s = ((h - 1 - y) * w + x) * bpp, d = (y * w + x) * 4;
            if (fmt == 5) { rgba[d] = src[s + 1]; rgba[d + 1] = src[s + 2]; rgba[d + 2] = src[s + 3]; rgba[d + 3] = src[s]; }
            else { rgba[d] = src[s]; rgba[d + 1] = src[s + 1]; rgba[d + 2] = src[s + 2]; rgba[d + 3] = bpp == 4 ? src[s + 3] : (byte)255; }
        }
        return new
        {
            frameRate = fr, maxKey, width = w, height = h, rgba = Convert.ToBase64String(rgba),
            layers = layers.Select(l => new { textures = l.tex, animations = l.anims }),
            rects = rects.Where(r => r.Length == 4).Select(r => new[] { r[0] * w, (1 - r[1] - r[3]) * h, r[2] * w, r[3] * h }), // atlas px, top-down
        };
    }
}
