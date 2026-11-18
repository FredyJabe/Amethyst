using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using System.Threading.Channels;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Amethyst;

/// <summary>
/// Phase-5 generated-chunk snapshot storage. A YAML world manifest identifies
/// the EXACT generation settings; blocks and 4x4x4 biomes live in compressed
/// binary chunk files (not JSON). Saves are written atomically.
/// </summary>
public sealed class WorldStorage : IAsyncDisposable
{
    private const int FormatVersion = 1;
    private static readonly byte[] Magic = "MC26CH5!"u8.ToArray();
    private readonly string _chunkDirectory;
    private readonly string _fingerprint;
    private readonly Channel<ChunkColumn>? _saves;
    private readonly Task? _saveWorker;
    private readonly ChunkPerformance? _performance;
    private ExceptionDispatchInfo? _saveFailure;

    public sealed class WorldManifest
    {
        public string Format { get; set; } = "Amethyst/phase5";
        public long Seed { get; set; }
        public int GeneratorVersion { get; set; }
        public string GeneratorFingerprint { get; set; } = "";
    }

    public WorldStorage(ServerOptions settings, ChunkPerformance? performance = null)
    {
        _performance = performance;
        if (settings.GeneratorVersion is < 5 or > 7)
            throw new ArgumentException("Chunk persistence is enabled for versions 5, 6 and 7 only.");
        string directory = Path.GetFullPath(settings.WorldPath);
        _chunkDirectory = Path.Combine(directory, "chunks");
        Directory.CreateDirectory(directory);
        _fingerprint = ComputeFingerprint(settings);
        string manifestPath = Path.Combine(directory, "world.yaml");
        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
        if (File.Exists(manifestPath))
        {
            WorldManifest saved;
            try
            {
                using var reader = File.OpenText(manifestPath);
                saved = deserializer.Deserialize<WorldManifest>(reader) ??
                    throw new InvalidDataException("Empty world manifest.");
            }
            catch (YamlDotNet.Core.YamlException ex)
            {
                throw new InvalidDataException("Invalid world.yaml manifest.", ex);
            }
            if (saved.Format != "Amethyst/phase5" || saved.Seed != settings.Seed ||
                saved.GeneratorVersion != settings.GeneratorVersion || saved.GeneratorFingerprint != _fingerprint)
                throw new InvalidDataException(
                    $"World '{directory}' was generated with different seed/settings. " +
                    "Restore the previous YAML settings or choose a NEW worldPath. " +
                    "Refusing to mix incompatible chunks.");
        }
        else
        {
            if (Directory.Exists(_chunkDirectory) &&
                Directory.EnumerateFiles(_chunkDirectory, "*.mc26").Any())
                throw new InvalidDataException("Chunk files exist without world.yaml; refusing data loss.");
            string yaml = serializer.Serialize(new WorldManifest
            {
                Seed = settings.Seed,
                GeneratorVersion = settings.GeneratorVersion,
                GeneratorFingerprint = _fingerprint
            });
            File.WriteAllText(manifestPath, yaml, Encoding.UTF8);
        }
        Directory.CreateDirectory(_chunkDirectory);
        if (settings.AsyncWorldSaves)
        {
            // Bounded queue: backpressure instead of accumulating unbounded
            // compressed writes while players explore at high speed.
            _saves = Channel.CreateBounded<ChunkColumn>(new BoundedChannelOptions(settings.SaveQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            _saveWorker = Task.Run(ProcessSavesAsync);
        }
    }

    public void QueueSave(ChunkColumn column)
    {
        _saveFailure?.Throw();
        if (_saves is null) { Save(column); return; }
        // Generation already executes on bounded background workers.
        // Backpressure here never blocks the network read loop.
        _saves.Writer.WriteAsync(column).AsTask().GetAwaiter().GetResult();
        _saveFailure?.Throw();
    }

    private async Task ProcessSavesAsync()
    {
        if (_saves is null) return;
        try
        {
            while (await _saves.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                // Drain multiple ready chunks per wake-up. Files remain in the
                // original atomic .mc26 format; no world migration required.
                for (int batch = 0; batch < 32 && _saves.Reader.TryRead(out var column); batch++)
                    Save(column);
            }
        }
        catch (Exception ex)
        {
            _saveFailure = ExceptionDispatchInfo.Capture(ex);
            _saves.Writer.TryComplete(ex);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_saves is null || _saveWorker is null) return;
        _saves.Writer.TryComplete();
        await _saveWorker.ConfigureAwait(false); // drains all accepted saves
        _saveFailure?.Throw();
    }

    private static string ComputeFingerprint(ServerOptions s)
    {
        // Invariant formatting avoids locale-dependent world identities.
        string joined = string.Join("|", new[]
        {
            "phase5",
            s.Seed.ToString(CultureInfo.InvariantCulture),
            s.GeneratorVersion.ToString(CultureInfo.InvariantCulture),
            s.ClimateScale.ToString("R", CultureInfo.InvariantCulture),
            s.BaseHeight.ToString(CultureInfo.InvariantCulture),
            s.SeaLevel.ToString(CultureInfo.InvariantCulture),
            s.HeightVariation.ToString(CultureInfo.InvariantCulture),
            s.TerrainScale.ToString("R", CultureInfo.InvariantCulture),
            s.Biome,
            s.AquifersEnabled ? "true" : "false",
            s.AquiferLavaLevel.ToString(CultureInfo.InvariantCulture),
            s.AquiferWaterTableOffset.ToString(CultureInfo.InvariantCulture),
            s.SurfaceDepth.ToString(CultureInfo.InvariantCulture),
            s.FeatureDensity.ToString("R", CultureInfo.InvariantCulture),
            s.OreFrequency.ToString("R", CultureInfo.InvariantCulture),
            s.StructureFrequency.ToString("R", CultureInfo.InvariantCulture)
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
    }

    private string FileFor(ChunkPos pos) => Path.Combine(_chunkDirectory, $"{pos.X}.{pos.Z}.mc26");

    public ChunkColumn? TryLoad(ChunkPos pos)
    {
        long start = ChunkPerformance.Start();
        string path = FileFor(pos);
        if (!File.Exists(path)) return null;
        try
        {
            using var file = File.OpenRead(path);
            using var compressed = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new BinaryReader(compressed, Encoding.UTF8);
            if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic) ||
                reader.ReadInt32() != FormatVersion ||
                reader.ReadInt32() != pos.X || reader.ReadInt32() != pos.Z ||
                reader.ReadInt32() != ChunkColumn.SectionCount)
                throw new InvalidDataException("Invalid chunk header or position.");
            var column = new ChunkColumn(pos, 0);
            foreach (var section in column.Sections)
            {
                // Each section stores exactly 4096 global block-state IDs and
                // 64 biome-registry IDs. The strict lengths prevent allocation
                // based on corrupt/untrusted file data.
                if (BitConverter.IsLittleEndian)
                {
                    compressed.ReadExactly(MemoryMarshal.AsBytes(section.Blocks.AsSpan()));
                    compressed.ReadExactly(MemoryMarshal.AsBytes(section.Biomes.AsSpan()));
                    section.RecomputeCounts();
                }
                else
                {
                    // Legacy .mc26 files store little-endian integer arrays.
                    for (int i = 0; i < section.Blocks.Length; i++)
                        section.Blocks[i] = reader.ReadUInt16();
                    for (int i = 0; i < section.Biomes.Length; i++)
                        section.Biomes[i] = reader.ReadInt32();
                    section.RecomputeCounts();
                }
            }
            if (compressed.ReadByte() != -1)
                throw new InvalidDataException("Extra data after chunk payload.");
            TerrainWorld.RecomputeHeightmaps(column);
            return column;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            throw new InvalidDataException(
                $"Could not read saved chunk {pos.X},{pos.Z} from '{path}'. " +
                "File was NOT overwritten. Restore a backup or inspect it.", ex);
        }
        finally { _performance?.Record(ChunkStage.DiskLoad, start); }
    }

    public void Save(ChunkColumn column)
    {
        long start = ChunkPerformance.Start();
        try { SaveCore(column); }
        finally { _performance?.Record(ChunkStage.DiskSave, start); }
    }

    private void SaveCore(ChunkColumn column)
    {
        string destination = FileFor(column.Position);
        if (File.Exists(destination)) return;
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var compressed = new GZipStream(file, CompressionLevel.Fastest))
            using (var writer = new BinaryWriter(compressed, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(column.Position.X);
                writer.Write(column.Position.Z);
                writer.Write(ChunkColumn.SectionCount);
                foreach (var section in column.Sections)
                {
                    if (BitConverter.IsLittleEndian)
                    {
                        writer.Write(MemoryMarshal.AsBytes(section.Blocks.AsSpan()));
                        writer.Write(MemoryMarshal.AsBytes(section.Biomes.AsSpan()));
                    }
                    else
                    {
                        foreach (ushort state in section.Blocks) writer.Write(state);
                        foreach (int biome in section.Biomes) writer.Write(biome);
                    }
                }
            }
            // Never overwrite an existing chunk from another process.
            if (!File.Exists(destination)) File.Move(temp, destination);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
