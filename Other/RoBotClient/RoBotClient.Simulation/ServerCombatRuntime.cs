using System.Diagnostics;
using System.Reflection;
using Lidgren.Network;
using Microsoft.Extensions.ObjectPool;
using RebuildZoneServer.Networking;
using RoRebuildServer.Networking;
using System.Runtime.CompilerServices;
using RoRebuildServer.Data;
using RoRebuildServer.EntitySystem;
using RoRebuildServer.Logging;
using RoRebuildServer.Simulation;
using Serilog;

namespace RoBotClient.Simulation;

/// <summary>
/// Hosts the game server's own data and combat code inside this process, once, for every simulation to share.
/// It loads the same files the live server loaded: the server's appsettings, its ServerData folder, and the
/// Script.dll the live server compiled at startup (item and card effects live there), so offline entities get
/// exactly the bonuses they get in game.
///
/// Concurrency: the server code keeps its state in unsynchronised statics (EntityManager.Entities, the entity
/// component pools, status-effect pools, Time). <see cref="Gate"/> serialises every touch of that state.
/// </summary>
public static class ServerCombatRuntime
{
    // ponytail: one process-wide lock. Ceiling: one simulating core. Upgrade path: per-thread entity stores would
    // need the server's statics made thread-local, which the server source does not offer today.
    public static readonly object Gate = new();

    /// <summary>Stand-in map the duel entities are placed on. CombatEntity.IsValidTarget (asserted by the
    /// Debug server build before every attack) only checks that attacker and target share a non-null map; the
    /// object is never initialised, so any combat path that really needs map data (Safety Wall, knockback, ground
    /// effects) throws instead of silently computing something else.</summary>
    internal static Map Arena { get; private set; } = null!;

    public static bool IsInitialized { get; private set; }
    public static TimeSpan InitializationTime { get; private set; }
    public static string ServerProjectDirectory { get; private set; } = "";

    /// <summary>Write time and size of the Script.dll loaded at initialisation (item and card effects): part of
    /// every cached forecast's key.</summary>
    public static string ScriptVersion { get; private set; } = "";

    /// <summary>The live server recompiled its scripts after this process loaded them: the loaded copy is stale
    /// and only a restart reloads it.</summary>
    public static bool ScriptsChangedSinceLoad() => ScriptStamp() != ScriptVersion;

    private static string ScriptStamp()
    {
        var script = new FileInfo(Path.Combine(ServerConfig.DataConfig.CachePath, "Script.dll"));
        return script.Exists ? $"{script.LastWriteTimeUtc:O}/{script.Length}" : "missing";
    }

    /// <param name="serverProjectDirectory">RoRebuildServer/RoRebuildServer: the live server's working directory,
    /// which its relative data paths are resolved from.</param>
    public static void EnsureInitialized(string serverProjectDirectory)
    {
        lock (Gate)
        {
            if (IsInitialized) return;
            var stopwatch = Stopwatch.StartNew();
            serverProjectDirectory = Path.GetFullPath(serverProjectDirectory);
            ServerConfig.LoadConfigFromPath(Path.Combine(serverProjectDirectory, "bin", "Debug", "net9.0"));
            var dataConfig = ServerConfig.DataConfig;
            dataConfig.DataPath = Path.GetFullPath(Path.Combine(serverProjectDirectory, dataConfig.DataPath));
            dataConfig.CachePath = Path.GetFullPath(Path.Combine(serverProjectDirectory, dataConfig.CachePath));
            ServerLogger.SetLogger(new LoggerConfiguration().MinimumLevel.Warning().WriteTo.Console().CreateLogger());

            ScriptVersion = ScriptStamp();
            DataManager.Initialize(loadExisting: true); // Cache/Script.dll as compiled by the running server
            EntityManager.Initialize(64);
            ResetPacketQueue();
            SetNetworkField("outboundPool", new DefaultObjectPool<OutboundMessage>(new OutboundMessagePooledObjectPolicy(), 10));
            SetNetworkField("inboundPool", new DefaultObjectPool<InboundMessage>(new DefaultPooledObjectPolicy<InboundMessage>(), 10));
            Arena = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
            // Status effects look their source up through World.Instance (poison credits its damage to the
            // attacker); an empty world finds nobody, and the server code then falls back to the target itself.
            if (World.Instance == null)
            {
                var world = (World)RuntimeHelpers.GetUninitializedObject(typeof(World));
                typeof(World).GetField("entityList", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(world, new Dictionary<int, Entity>());
                World.Instance = world;
            }

            ServerProjectDirectory = serverProjectDirectory;
            InitializationTime = stopwatch.Elapsed;
            IsInitialized = true;
        }
    }

    /// <summary>Server code sends packets as a side effect (equipping ammunition does). NetworkManager.Init is
    /// never run here, so its message pools and outbound queue are created directly; nothing drains the queue, so
    /// each simulation replaces it, dropping the packets. Under <see cref="Gate"/>.</summary>
    internal static void ResetPacketQueue() => SetNetworkField("outboundChannel", new NetQueue<OutboundMessage>(100));

    private static void SetNetworkField(string name, object value) =>
        typeof(NetworkManager).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, value);

    /// <summary>Walks up from <paramref name="start"/> to the repository and returns its server project directory.</summary>
    public static string? FindServerProjectDirectory(string start)
    {
        for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "RoRebuildServer", "RoRebuildServer");
            if (File.Exists(Path.Combine(candidate, "RoRebuildServer.csproj"))) return candidate;
        }
        return null;
    }
}
