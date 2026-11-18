using System.Net;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Amethyst;

public sealed class ServerOptions
{
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 25565;
    public string Motd { get; set; } = "Amethyst | Java 26.3";
    public int MaxPlayers { get; set; } = 20;
    // Applied when a player joins. In-game /gamemode changes are session-only.
    // Stored as a string so networking Core does not depend on the Gameplay Shard.
    public string DefaultGameMode { get; set; } = "creative";
    public bool OnlineMode { get; set; } = true;
    public bool ChatEnabled { get; set; } = true;
    public int ChatMaxLength { get; set; } = 256;
    public int ChatCooldownMilliseconds { get; set; } = 350;
    // YAML path, relative to the folder containing server.yaml.
    public string OperatorsFile { get; set; } = "ops.yaml";

    // Terminal rendering: auto chooses a live prompt only for a real TTY.
    // plain is useful for logs, pipes, Docker, systemd or IDE output panes.
    public string ConsoleMode { get; set; } = "auto";
    public int ConsoleHistorySize { get; set; } = 100;
    // Shard installation directory and writable per-Shard state (YAML paths).
    public string ShardsDirectory { get; set; } = "shards/installed";
    public string ShardsDataDirectory { get; set; } = "shards/data";

    // Performance-only settings: these DO NOT enter the world seed fingerprint.
    public int ChunkWorkers { get; set; } = 4;
    public int MaxQueuedChunks { get; set; } = 1024;
    public int MaxCachedColumns { get; set; } = 1024;
    public int MaxCachedLights { get; set; } = 512;
    public int MaxCachedLightSources { get; set; } = 256;
    public int MaxCachedPackets { get; set; } = 512;
    public int CachedTerrainSamples { get; set; } = 32768;
    public int CachedFeatureHeights { get; set; } = 8192;
    public bool AsyncWorldSaves { get; set; } = true;
    public int SaveQueueCapacity { get; set; } = 128;
    // 0 disables periodic performance logging; 'perf' console command is always available.
    public int PerformanceReportSeconds { get; set; } = 0;

    public int ViewDistance { get; set; } = 10;
    // One day is 24000 world-clock ticks; 20 real minutes matches vanilla.
    public int StartTimeTicks { get; set; } = 1000;
    public double DayLengthMinutes { get; set; } = 20;
    public bool DaylightCycle { get; set; } = true;
    public long Seed { get; set; } = 87420913;
    // Version pins the seed-to-world algorithm. Unsupported versions fail fast.
    public int GeneratorVersion { get; set; } = 7;
    // Climate field frequency; versions 2/3 also use climate for terrain shaping.
    public double ClimateScale { get; set; } = 1.0;
    public int BaseHeight { get; set; } = 70;
    public int SeaLevel { get; set; } = 63;
    public int HeightVariation { get; set; } = 36;
    public double TerrainScale { get; set; } = 1.0;
    // Phase 4 aquifer/material rules; ignored by generatorVersion 1-3.
    public bool AquifersEnabled { get; set; } = true;
    public int AquiferLavaLevel { get; set; } = -48;
    public int AquiferWaterTableOffset { get; set; } = 18;
    public int SurfaceDepth { get; set; } = 4;
    // Phase 5: probabilities relative to the built-in biome/ore distributions.
    public double FeatureDensity { get; set; } = 1.0;
    public double OreFrequency { get; set; } = 1.0;
    public double StructureFrequency { get; set; } = 1.0;
    // Generated chunk snapshots; only version 5 uses persistence.
    public bool SaveGeneratedChunks { get; set; } = true;
    public string WorldPath { get; set; } = "world";
    // "auto" selects a climate-based biome. A specific minecraft: ID forces one biome.
    public string Biome { get; set; } = "auto";

    public IPAddress ListenAddress => IPAddress.Parse(BindAddress);

    public static ServerOptions Load(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".yaml", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.GetExtension(path), ".yml", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Server config must be YAML (.yaml or .yml).");
        if (!File.Exists(path)) throw new FileNotFoundException("Missing YAML configuration.", path);
        ServerOptions options;
        try
        {
            using var reader = File.OpenText(path);
            options = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build().Deserialize<ServerOptions>(reader)
                ?? throw new InvalidDataException("Empty YAML configuration.");
        }
        catch (YamlException ex)
        {
            throw new InvalidDataException($"Invalid YAML configuration '{path}': {ex.Message}", ex);
        }
        if (string.IsNullOrWhiteSpace(options.BindAddress) ||
            !IPAddress.TryParse(options.BindAddress, out _))
            throw new ArgumentException("bindAddress must be a numeric IPv4 or IPv6 address.");
        if (options.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(options.Port));
        if (options.MaxPlayers is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(options.MaxPlayers));
        // Validate and canonicalize the YAML mode before it reaches the Gameplay Shard.
        string? defaultMode = options.DefaultGameMode?.Trim().ToLowerInvariant();
        if (defaultMode is not ("survival" or "creative" or "adventure" or "spectator"))
            throw new ArgumentException("defaultGameMode must be survival, creative, adventure or spectator.");
        options.DefaultGameMode = defaultMode;
        if (options.ChatMaxLength is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(options.ChatMaxLength));
        if (options.ChatCooldownMilliseconds is < 0 or > 5000)
            throw new ArgumentOutOfRangeException(nameof(options.ChatCooldownMilliseconds));
        if (string.IsNullOrWhiteSpace(options.OperatorsFile) ||
            !(options.OperatorsFile.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
              options.OperatorsFile.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("operatorsFile must name a YAML file.");
        string configDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        options.OperatorsFile = Path.GetFullPath(options.OperatorsFile, configDirectory);
        if (string.IsNullOrWhiteSpace(options.ShardsDirectory) ||
            string.IsNullOrWhiteSpace(options.ShardsDataDirectory))
            throw new ArgumentException("Shard directories must not be empty.");
        options.ShardsDirectory = Path.GetFullPath(options.ShardsDirectory, configDirectory);
        options.ShardsDataDirectory = Path.GetFullPath(options.ShardsDataDirectory, configDirectory);

        if (options.ConsoleMode is not ("auto" or "interactive" or "plain"))
            throw new ArgumentException("consoleMode must be auto, interactive or plain.");
        if (options.ConsoleHistorySize is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(options.ConsoleHistorySize),
                "consoleHistorySize must be 1..1000.");

        if (options.ChunkWorkers is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(options.ChunkWorkers), "chunkWorkers must be 1..32.");
        if (options.MaxQueuedChunks < options.ChunkWorkers || options.MaxQueuedChunks > 16384)
            throw new ArgumentOutOfRangeException(nameof(options.MaxQueuedChunks));
        if (options.MaxCachedColumns is < 32 or > 32768 ||
            options.MaxCachedLights is < 16 or > 32768 ||
            options.MaxCachedLightSources is < 16 or > 32768 ||
            options.MaxCachedPackets is < 16 or > 32768)
            throw new ArgumentException("Chunk/light/packet cache limits are out of range.");
        if (options.CachedTerrainSamples is < 64 or > 1048576 ||
            options.CachedFeatureHeights is < 64 or > 1048576)
            throw new ArgumentException("Terrain sample cache limits are out of range.");
        if (options.SaveQueueCapacity is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(options.SaveQueueCapacity));
        if (options.PerformanceReportSeconds is < 0 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(options.PerformanceReportSeconds));

        if (options.ViewDistance is < 2 or > 16) throw new ArgumentOutOfRangeException(nameof(options.ViewDistance));
        if (options.StartTimeTicks is < 0 or >= 24000)
            throw new ArgumentOutOfRangeException(nameof(options.StartTimeTicks), "startTimeTicks must be 0..23999.");
        if (!double.IsFinite(options.DayLengthMinutes) || options.DayLengthMinutes is < 2 or > 120)
            throw new ArgumentOutOfRangeException(nameof(options.DayLengthMinutes), "dayLengthMinutes must be 2..120.");
        if (options.BaseHeight is < 0 or > 180)
            throw new ArgumentOutOfRangeException(nameof(options.BaseHeight), "baseHeight must be 0..180.");
        if (options.SeaLevel is < -32 or > 76)
            throw new ArgumentOutOfRangeException(nameof(options.SeaLevel), "seaLevel must be -32..76 to keep the spawn platform above water.");
        if (options.HeightVariation is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(options.HeightVariation), "heightVariation must be 0..100.");
        if (!double.IsFinite(options.TerrainScale) || options.TerrainScale is < 0.1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(options.TerrainScale), "terrainScale must be finite and in 0.1..8.");
        if (options.GeneratorVersion is < 1 or > 7)
            throw new NotSupportedException("Unsupported generatorVersion. Available versions: 1–7.");
        if (options.AquiferLavaLevel is < -62 or > 0 || options.AquiferLavaLevel > options.SeaLevel - 14)
            throw new ArgumentOutOfRangeException(nameof(options.AquiferLavaLevel),
                "aquiferLavaLevel must be at least -62 and at least 14 below seaLevel.");
        if (options.AquiferWaterTableOffset is < 4 or > 64)
            throw new ArgumentOutOfRangeException(nameof(options.AquiferWaterTableOffset),
                "aquiferWaterTableOffset must be 4..64.");
        if (options.SurfaceDepth is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(options.SurfaceDepth),
                "surfaceDepth must be 1..8.");
        if (!double.IsFinite(options.FeatureDensity) || options.FeatureDensity is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(options.FeatureDensity),
                "featureDensity must be finite and in 0..2.");
        if (!double.IsFinite(options.OreFrequency) || options.OreFrequency is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(options.OreFrequency),
                "oreFrequency must be finite and in 0..2.");
        if (!double.IsFinite(options.StructureFrequency) || options.StructureFrequency is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(options.StructureFrequency),
                "structureFrequency must be finite and in 0..2.");
        if (string.IsNullOrWhiteSpace(options.WorldPath))
            throw new ArgumentException("worldPath must be a non-empty directory.");
        if (!double.IsFinite(options.ClimateScale) || options.ClimateScale is < 0.25 or > 8)
            throw new ArgumentOutOfRangeException(nameof(options.ClimateScale), "climateScale must be finite and in 0.25..8.");
        if (string.IsNullOrWhiteSpace(options.Motd) || options.Motd.Length > 1000)
            throw new ArgumentException("motd must be 1..1000 characters.");
        if (!string.Equals(options.Biome, "auto", StringComparison.Ordinal) &&
            !options.Biome.StartsWith("minecraft:", StringComparison.Ordinal))
            throw new ArgumentException("biome must be 'auto' or a minecraft: biome ID.");
        if (options.Biome != "auto")
            _ = VanillaRegistries.IndexOf("minecraft:worldgen/biome", options.Biome);
        return options;
    }
}
