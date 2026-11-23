using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoBotClient.Simulation;

/// <summary>Append-only duel history, one JSON line per duel in &lt;dir&gt;/&lt;character&gt;.jsonl (same layout as bot-progress).</summary>
public sealed class DuelStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _lock = new();
    public string Directory { get; }

    public DuelStore(string directory)
    {
        Directory = directory;
        try { System.IO.Directory.CreateDirectory(directory); } catch { }
    }

    public void Append(string character, DuelRecord record)
    {
        var line = JsonSerializer.Serialize(record, JsonOptions) + "\n";
        lock (_lock)
        {
            try { File.AppendAllText(PathFor(character), line); }
            catch { }
        }
    }

    /// <summary>Every recorded duel; lines cut short by a crash mid-write are skipped.</summary>
    public static List<DuelRecord> ReadAll(string directory)
    {
        var records = new List<DuelRecord>();
        if (!System.IO.Directory.Exists(directory)) return records;
        foreach (var path in System.IO.Directory.GetFiles(directory, "*.jsonl"))
        foreach (var line in File.ReadLines(path))
        {
            try { if (JsonSerializer.Deserialize<DuelRecord>(line, JsonOptions) is { } record) records.Add(record); }
            catch (JsonException) { }
        }
        return records;
    }

    private string PathFor(string character)
    {
        // Same filename sanitising as BotProgressStore.
        var safe = new string((character ?? "").Where(c => char.IsLetterOrDigit(c) || " _-[]()".Contains(c)).ToArray()).Trim();
        return Path.Combine(Directory, (safe.Length == 0 ? "unnamed" : safe) + ".jsonl");
    }
}
