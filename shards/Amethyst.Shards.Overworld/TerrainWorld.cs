using Amethyst.Generation.Climate;
using Amethyst.Generation.Terrain;
using Amethyst.Generation.Features;
using System.Buffers;

namespace Amethyst;

// In-memory procedural world is composed of 16 x 16 x 16 cuboid sections.
// A chunk column stores 24 sections, covering vanilla Overworld Y=-64..319.
public sealed class WorldSection
{
    public const int Size = 16;
    public readonly ushort[] Blocks = new ushort[4096];
    public readonly int[] Biomes = new int[64]; // 4x4x4 biome samples per section
    public int NonAir { get; private set; }
    public int FluidCount { get; private set; }
    private int _opaqueDark = -1;
    // Immutable after decoration: memoized fast-path for all-stone sections.
    public bool FullyOpaqueAndUnlit
    {
        get
        {
            int cached = Volatile.Read(ref _opaqueDark);
            if (cached >= 0) return cached == 1;
            bool opaque = NonAir == Blocks.Length && FluidCount == 0;
            if (opaque)
                for (int i = 0; i < Blocks.Length; i++)
                    if (!ChunkLightEngine.IsOpaqueAndUnlit(Blocks[i])) { opaque = false; break; }
            Volatile.Write(ref _opaqueDark, opaque ? 1 : 0);
            return opaque;
        }
    }

    public WorldSection(int biomeId) => Array.Fill(Biomes, biomeId);
    public void SetBlock(int x, int y, int z, ushort state)
    {
        int index = (y << 8) | (z << 4) | x;
        if (Blocks[index] == state) return;
        if (Blocks[index] == TerrainBlocks.Air && state != TerrainBlocks.Air) NonAir++;
        else if (Blocks[index] != TerrainBlocks.Air && state == TerrainBlocks.Air) NonAir--;
        if (MaterialRuleEngine.IsFluid(Blocks[index])) FluidCount--;
        if (MaterialRuleEngine.IsFluid(state)) FluidCount++;
        Blocks[index] = state;
        _opaqueDark = -1;
    }
    public ushort GetBlock(int x, int y, int z) => Blocks[(y << 8) | (z << 4) | x];

    // Bulk chunk loads bypass per-block SetBlock; recount once instead of
    // thousands of repeated work and bounds checks.
    internal void RecomputeCounts()
    {
        int nonAir = 0, fluid = 0;
        foreach (ushort state in Blocks)
        {
            if (state != TerrainBlocks.Air) nonAir++;
            if (MaterialRuleEngine.IsFluid(state)) fluid++;
        }
        NonAir = nonAir;
        FluidCount = fluid;
        _opaqueDark = -1;
    }
}

public readonly record struct ChunkPos(int X, int Z);

public sealed class ChunkColumn
{
    public const int MinY = -64;
    public const int SectionCount = 24;
    public const int MaxY = MinY + SectionCount * 16 - 1;
    public ChunkPos Position { get; }
    public WorldSection[] Sections { get; }
    // GroundY excludes the water surface; TopY includes water and spawn pad.
    public int[] GroundY { get; } = new int[256];
    public int[] TopY { get; } = new int[256];
    public ChunkColumn(ChunkPos position, int biome)
    {
        Position = position;
        Sections = Enumerable.Range(0, SectionCount)
            .Select(_ => new WorldSection(biome)).ToArray();
    }
    public void SetBlock(int x, int y, int z, ushort state)
    {
        if (y < MinY || y > MaxY) throw new ArgumentOutOfRangeException(nameof(y));
        SetBlockFast(x, y, z, state);
    }
    // Internal generation hot path already has validated absolute Y and local X/Z.
    internal void SetBlockFast(int x, int y, int z, ushort state) =>
        Sections[(y - MinY) >> 4].SetBlock(x, (y - MinY) & 15, z, state);

    public ushort GetBlock(int x, int y, int z) =>
        y < MinY || y > MaxY ? (ushort)0 :
        Sections[(y - MinY) / 16].GetBlock(x, (y - MinY) % 16, z);
}

public sealed class TerrainWorld : Amethyst.Shards.IShardWorld, Amethyst.Shards.IShardBlockWorld
{
    public const string Dimension = "minecraft:overworld";
    public const int PlatformY = 79;
    public const double SpawnX = 0.5, SpawnY = 80, SpawnZ = 0.5;
    private readonly BoundedLazyCache<ChunkPos, ChunkColumn> _columns;
    private readonly BoundedLazyCache<(ChunkPos Position, ulong Stamp), ChunkLightEngine.LightData> _lights;
    private readonly BoundedLazyCache<(ChunkPos Position, long Revision), ChunkLightEngine.LightSource> _lightSources;
    private readonly BoundedLazyCache<(ChunkPos Position, ulong Stamp), byte[]> _frames;
    private readonly WorldEdits _edits;
    public ChunkPerformance Performance { get; } = new();
    public ChunkPreparationScheduler Scheduler { get; }
    public int CachedColumns => _columns.Count;
    public int CachedLights => _lights.Count;
    public int CachedLightSources => _lightSources.Count;
    public int CachedPackets => _frames.Count;
    private readonly OreVeinGenerator? _ores;
    private readonly VegetationGenerator? _vegetation;
    private readonly StructureGenerator? _structures;
    private readonly WorldStorage? _storage;
    private readonly TerrainGenerator _terrain;
    private readonly DensitySampler? _density;
    private readonly AquiferSampler? _aquifers;
    private readonly MaterialRuleEngine? _materials;
    private readonly CaveBiomeSampler? _caveBiomes;
    public bool UsesAquifers => _aquifers is not null;
    public bool UsesDensity => _density is not null;
    public long Seed => _terrain.Seed;
    public int SeaLevel => _terrain.SeaLevel;
    public int ViewDistance { get; }
    public string DimensionId => Dimension;
    public int MinY => ChunkColumn.MinY;
    public double SpawnXPosition => SpawnX;
    public double SpawnYPosition => SpawnY;
    public double SpawnZPosition => SpawnZ;
    public Amethyst.Shards.IShardChunkView CreateView() => new PlayerChunkView(this);
    public string GetPerformanceReport() => Performance.Report(Scheduler.WorkerCount);
    public string GetCacheSummary() => $"columns={CachedColumns}, lighting={CachedLights}, lightSources={CachedLightSources}, frames={CachedPackets}";

    public TerrainWorld(ServerOptions settings)
    {
        _columns = new BoundedLazyCache<ChunkPos, ChunkColumn>(settings.MaxCachedColumns);
        _lights = new BoundedLazyCache<(ChunkPos Position, ulong Stamp), ChunkLightEngine.LightData>(settings.MaxCachedLights);
        _lightSources = new BoundedLazyCache<(ChunkPos Position, long Revision), ChunkLightEngine.LightSource>(settings.MaxCachedLightSources);
        _frames = new BoundedLazyCache<(ChunkPos Position, ulong Stamp), byte[]>(settings.MaxCachedPackets);
        _terrain = new TerrainGenerator(settings);
        if (settings.GeneratorVersion >= 3)
            _density = new DensitySampler(_terrain, settings);
        if (settings.GeneratorVersion >= 4)
        {
            _materials = new MaterialRuleEngine(settings);
            _caveBiomes = new CaveBiomeSampler(settings.Seed);
            if (settings.AquifersEnabled)
                _aquifers = new AquiferSampler(settings);
        }
        if (settings.GeneratorVersion >= 5)
        {
            _ores = new OreVeinGenerator(settings);
            _vegetation = new VegetationGenerator(_terrain, _density!, settings);
            _structures = new StructureGenerator(_terrain, _density!, settings);
            if (settings.SaveGeneratedChunks)
                _storage = new WorldStorage(settings, Performance);
        }
        _edits = new WorldEdits(settings.SaveGeneratedChunks && settings.GeneratorVersion >= 5
            ? settings.WorldPath : null);
        ViewDistance = settings.ViewDistance;
        Scheduler = new ChunkPreparationScheduler(this, settings.ChunkWorkers, settings.MaxQueuedChunks);
    }

    public async ValueTask DisposeAsync()
    {
        await Scheduler.DisposeAsync().ConfigureAwait(false);
        if (_storage is not null) await _storage.DisposeAsync().ConfigureAwait(false);
    }

    public static int ChunkCoord(double coordinate) => (int)Math.Floor(coordinate / 16);
    public ChunkColumn GetColumn(ChunkPos pos)
    {
        ChunkColumn column = _columns.GetOrAdd(pos, LoadOrGenerate, out bool hit);
        Performance.CacheHit("column", hit);
        return column;
    }

    private ChunkColumn LoadOrGenerate(ChunkPos pos)
    {
        ChunkColumn? saved = _storage?.TryLoad(pos);
        if (saved is not null) return saved;
        long start = ChunkPerformance.Start();
        ChunkColumn generated = Generate(pos);
        Performance.Record(ChunkStage.Generate, start);
        _storage?.QueueSave(generated);
        return generated;
    }
    public TerrainSample SampleTerrain(int x, int z) => _terrain.Sample(x, z);
    public ClimateSample SampleClimate(int x, int z) => _terrain.SampleClimate(x, z);
    public int GetGroundHeight(int x, int z)
    {
        if (!UsesDensity) return _terrain.Sample(x, z).GroundY;
        int cx = (int)Math.Floor(x / 16.0), cz = (int)Math.Floor(z / 16.0);
        var column = GetColumn(new ChunkPos(cx, cz));
        return column.GroundY[(x - cx * 16) + (z - cz * 16) * 16];
    }

    private ChunkColumn Generate(ChunkPos pos)
    {
        if (_density is not null) return GenerateDensity(pos);
        var column = new ChunkColumn(pos, VanillaRegistries.IndexOf(
            "minecraft:worldgen/biome", "minecraft:plains"));
        for (int z = 0; z < 16; z++)
        for (int x = 0; x < 16; x++)
        {
            int worldX = pos.X * 16 + x, worldZ = pos.Z * 16 + z;
            TerrainSample sample = _terrain.Sample(worldX, worldZ);
            int ground = sample.GroundY;
            int index = x + z * 16;
            column.GroundY[index] = ground;
            column.TopY[index] = Math.Max(ground, SeaLevel);

            // Chunk sections are cubic, but height noise is sampled in absolute
            // world coordinates. Adjacent chunks therefore match at their edges.
            for (int y = ChunkColumn.MinY; y <= ground; y++)
            {
                ushort state;
                if (y == ChunkColumn.MinY) state = TerrainBlocks.Bedrock;
                else if (sample.Surface == SurfaceKind.Sand)
                    state = y >= ground - 3 ? TerrainBlocks.Sand :
                        y >= ground - 6 ? TerrainBlocks.Sandstone : TerrainBlocks.Stone;
                else if (sample.Surface == SurfaceKind.Riverbed)
                    state = y == ground ? TerrainBlocks.Gravel :
                        y >= ground - 3 ? TerrainBlocks.Dirt : TerrainBlocks.Stone;
                else if (sample.Surface == SurfaceKind.Snow)
                    state = y == ground ? TerrainBlocks.SnowBlock :
                        y >= ground - 3 ? TerrainBlocks.Dirt : TerrainBlocks.Stone;
                else if (sample.Surface == SurfaceKind.Rock) state = TerrainBlocks.Stone;
                else
                    state = y == ground ? TerrainBlocks.Grass :
                        y >= ground - 3 ? TerrainBlocks.Dirt : TerrainBlocks.Stone;
                column.SetBlock(x, y, z, state);
            }
            // Lakes and low-lying coastal water. Section fluid counts are kept
            // in step with water block states for protocol 777.
            for (int y = ground + 1; y <= SeaLevel; y++)
                column.SetBlock(x, y, z, TerrainBlocks.Water);

            // Preserve the 5x5 stone spawn platform. Its underlying terrain is
            // smoothed to Y=79 (via TerrainGenerator) so it is not floating.
            if (worldX is >= -2 and <= 2 && worldZ is >= -2 and <= 2)
            {
                column.SetBlock(x, PlatformY, z, TerrainBlocks.Stone);
                column.TopY[index] = Math.Max(column.TopY[index], PlatformY);
            }
        }

        PopulateBiomes(column, pos);
        return column;
    }

    private ChunkColumn GenerateDensity(ChunkPos pos)
    {
        if (_density is null) throw new InvalidOperationException("No density sampler.");
        var column = new ChunkColumn(pos, VanillaRegistries.IndexOf(
            "minecraft:worldgen/biome", "minecraft:plains"));
        long gridStart = ChunkPerformance.Start();
        DensityGrid grid = _density.BuildGrid(pos);
        byte[] cellSigns = grid.ClassifyCells();
        Performance.Record(ChunkStage.DensityGrid, gridStart);
        long fillStart = ChunkPerformance.Start();
        var terrainSamples = new TerrainSample[256];
        for (int z = 0; z < 16; z++)
        for (int x = 0; x < 16; x++)
        {
            int worldX = checked(pos.X * 16 + x), worldZ = checked(pos.Z * 16 + z);
            TerrainSample terrain = _terrain.Sample(worldX, worldZ);
            terrainSamples[x + z * 16] = terrain;
            int topSolid = ChunkColumn.MinY;
            // Fill the volume first; it can contain air BELOW solid rock.
            for (int y = ChunkColumn.MinY; y <= ChunkColumn.MaxY; y++)
            {
                int cell = (((y - ChunkColumn.MinY) / 8) * 4 + z / 4) * 4 + x / 4;
                byte sign = cellSigns[cell];
                if (y == ChunkColumn.MinY || sign == 1 ||
                    (sign == 0 && grid.Interpolate(x, y, z) > 0))
                {
                    ushort rock = y == ChunkColumn.MinY ? TerrainBlocks.Bedrock :
                        _materials is null ? TerrainBlocks.Stone :
                        _materials.BaseRock(worldX, y, worldZ);
                    column.SetBlockFast(x, y, z, rock);
                    topSolid = y;
                }
                else if (_aquifers is not null)
                {
                    ushort fluid = _aquifers.CaveFluid(worldX, y, worldZ, terrain.GroundY);
                    if (fluid != TerrainBlocks.Air)
                        column.SetBlockFast(x, y, z, fluid);
                }
            }
            // Preserve the unobstructed and supported 5x5 spawn platform.
            bool pad = worldX is >= -2 and <= 2 && worldZ is >= -2 and <= 2;
            if (pad)
            {
                column.SetBlock(x, PlatformY - 1, z, TerrainBlocks.Stone);
                column.SetBlock(x, PlatformY, z, TerrainBlocks.Stone);
                for (int y = PlatformY + 1; y <= ChunkColumn.MaxY; y++)
                    column.SetBlock(x, y, z, TerrainBlocks.Air);
                topSolid = PlatformY;
            }
            // Exposed top faces use surface rules. Version 4 additionally
            // decorates selected underground cave floors and ceilings.
            if (!pad && topSolid > ChunkColumn.MinY + 3)
            {
                if (_materials is not null)
                {
                    _materials.ApplySurface(column, x, z, topSolid,
                        terrain.Surface, terrain.BiomeId);
                    _materials.ApplyCaveFaces(column, x, z, worldX, worldZ, topSolid);
                }
                else
                    ApplySurface(column, x, z, topSolid, terrain.Surface);
            }

            int index = x + z * 16;
            column.GroundY[index] = topSolid;
            int top = topSolid;
            // Surface ocean/river water is independent of subterranean aquifers.
            // In versions 1-3, underground cavities remain dry.
            for (int y = topSolid + 1; y <= SeaLevel; y++)
            {
                column.SetBlock(x, y, z, TerrainBlocks.Water);
                top = y;
            }
            column.TopY[index] = top;
        }
        Performance.Record(ChunkStage.VolumeFill, fillStart);
        long biomeStart = ChunkPerformance.Start();
        PopulateBiomes(column, pos, terrainSamples);
        Performance.Record(ChunkStage.Biomes, biomeStart);
        long decorationStart = ChunkPerformance.Start();
        // Only v5 adds features. Ores first, then trees and small plants.
        // Neither pass requests neighbor chunks; both operate in world space.
        _ores?.Decorate(column);
        _structures?.Decorate(column);
        _vegetation?.Decorate(column);
        Performance.Record(ChunkStage.Decoration, decorationStart);
        if (_ores is not null || _vegetation is not null)
        {
            long mapStart = ChunkPerformance.Start();
            RecomputeHeightmaps(column);
            Performance.Record(ChunkStage.Heightmaps, mapStart);
        }
        return column;
    }

    /// <summary>Rebuild both 256-cell heightmaps from persisted or decorated blocks.</summary>
    public static void RecomputeHeightmaps(ChunkColumn column)
    {
        for (int z = 0; z < 16; z++)
        for (int x = 0; x < 16; x++)
        {
            int top = ChunkColumn.MinY, solid = ChunkColumn.MinY;
            bool foundTop = false, foundSolid = false;
            for (int y = ChunkColumn.MaxY; y >= ChunkColumn.MinY; y--)
            {
                ushort block = column.GetBlock(x, y, z);
                if (block == TerrainBlocks.Air) continue;
                if (!foundTop) { top = y; foundTop = true; }
                if (!foundSolid && !MaterialRuleEngine.IsFluid(block))
                {
                    solid = y;
                    foundSolid = true;
                }
                if (foundTop && foundSolid) break;
            }
            column.GroundY[x + z * 16] = solid;
            column.TopY[x + z * 16] = top;
        }
    }

    private static void ApplySurface(ChunkColumn column, int x, int z,
        int top, SurfaceKind kind)
    {
        for (int depth = 0; depth < 4 && top - depth > ChunkColumn.MinY; depth++)
        {
            int y = top - depth;
            if (column.GetBlock(x, y, z) != TerrainBlocks.Stone) break;
            ushort block = kind switch
            {
                SurfaceKind.Sand => depth < 3 ? TerrainBlocks.Sand : TerrainBlocks.Sandstone,
                SurfaceKind.Riverbed => depth == 0 ? TerrainBlocks.Gravel : TerrainBlocks.Dirt,
                SurfaceKind.Snow => depth == 0 ? TerrainBlocks.SnowBlock : TerrainBlocks.Dirt,
                SurfaceKind.Rock => TerrainBlocks.Stone,
                _ => depth == 0 ? TerrainBlocks.Grass : TerrainBlocks.Dirt
            };
            column.SetBlock(x, y, z, block);
        }
    }

    private void PopulateBiomes(ChunkColumn column, ChunkPos pos, TerrainSample[]? cached = null)
    {
        // Biomes are sampled in a 4x4 horizontal lattice for every 16-high
        // section (4x4x4 samples). No separate biome-disk boundary artifacts.
        for (int bz = 0; bz < 4; bz++)
        for (int bx = 0; bx < 4; bx++)
        {
            int wx = checked(pos.X * 16 + bx * 4 + 2);
            int wz = checked(pos.Z * 16 + bz * 4 + 2);
            TerrainSample sample = cached is null ? _terrain.Sample(wx, wz) :
                cached[(bz * 4 + 2) * 16 + bx * 4 + 2];
            for (int sectionIndex = 0; sectionIndex < column.Sections.Length; sectionIndex++)
            for (int by = 0; by < 4; by++)
            {
                int y = ChunkColumn.MinY + sectionIndex * 16 + by * 4 + 2;
                int biome = _caveBiomes?.Resolve(wx, y, wz, sample.GroundY,
                    sample.BiomeId) ?? sample.BiomeId;
                column.Sections[sectionIndex].Biomes[(by << 4) | (bz << 2) | bx] = biome;
            }
        }
    }


    private static (ChunkPos Position, int X, int Z) Locate(int x, int z)
    {
        int cx = Math.DivRem(x, 16, out int localX);
        int cz = Math.DivRem(z, 16, out int localZ);
        if (localX < 0) { cx--; localX += 16; }
        if (localZ < 0) { cz--; localZ += 16; }
        return (new ChunkPos(cx, cz), localX, localZ);
    }

    public ushort GetBlockState(int x, int y, int z)
    {
        if (y < ChunkColumn.MinY || y > ChunkColumn.MaxY) return TerrainBlocks.Air;
        var (pos, localX, localZ) = Locate(x, z);
        ushort original = GetColumn(pos).GetBlock(localX, y, localZ);
        return _edits.Get(pos, localX, y, localZ, original);
    }

    // Original test/terrain helper, now including the persisted edit overlay.
    public ushort GetBlock(int x, int y, int z) => GetBlockState(x, y, z);

    public bool TrySetBlockState(int x, int y, int z, ushort state, out ushort previousState)
    {
        previousState = TerrainBlocks.Air;
        if (y < ChunkColumn.MinY || y > ChunkColumn.MaxY ||
            x is < -29_999_984 or > 29_999_984 ||
            z is < -29_999_984 or > 29_999_984 ||
            state >= 35723) return false;
        var (pos, localX, localZ) = Locate(x, z);
        ushort original = GetColumn(pos).GetBlock(localX, y, localZ);
        return _edits.TrySet(pos, localX, y, localZ, original, state, out previousState);
    }

    private ChunkColumn ColumnWithEdits(ChunkPos pos)
    {
        ChunkColumn source = GetColumn(pos);
        var (_, changes) = _edits.Snapshot(pos);
        if (changes.Length == 0) return source;
        var copy = new ChunkColumn(pos, 0);
        for (int i = 0; i < ChunkColumn.SectionCount; i++)
        {
            Array.Copy(source.Sections[i].Blocks, copy.Sections[i].Blocks, 4096);
            Array.Copy(source.Sections[i].Biomes, copy.Sections[i].Biomes, 64);
            copy.Sections[i].RecomputeCounts();
        }
        foreach (var (index, state) in changes)
        {
            int y = ChunkColumn.MinY + index / 256;
            int localZ = (index % 256) >> 4, localX = index & 15;
            copy.SetBlock(localX, y, localZ, state);
        }
        RecomputeHeightmaps(copy);
        return copy;
    }

    // Lighting uses a 15-block halo. A revision in any of these 3x3 chunks
    // changes the frame/cache identity, avoiding stale cached chunk packets.
    private ulong LightingStamp(ChunkPos pos)
    {
        ulong hash = 14695981039346656037UL;
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
            hash = unchecked((hash ^ (ulong)_edits.Revision(new ChunkPos(pos.X + dx, pos.Z + dz)))
                * 1099511628211UL);
        return hash;
    }

    /// <summary>Cache chunk frames by edit/light revision, preserving old immutable
    /// frames for already running sends without mixing post-edit snapshots.</summary>
    public byte[] PrepareChunkFrame(ChunkPos pos)
    {
        byte[] frame = _frames.GetOrAdd((pos, LightingStamp(pos)),
            key => BuildFrame(key.Position), out bool hit);
        Performance.CacheHit("packet", hit);
        return frame;
    }

    public Task SendColumnAsync(PacketChannel channel, ChunkPos pos, CancellationToken ct) =>
        channel.WriteAsync(PrepareChunkFrame(pos), ct).AsTask();

    internal ChunkLightEngine.LightSource GetLightingSource(ChunkPos pos)
    {
        var source = _lightSources.GetOrAdd((pos, _edits.Revision(pos)), key =>
        {
            long started = ChunkPerformance.Start();
            try { return ChunkLightEngine.PrepareSource(ColumnWithEdits(key.Position)); }
            finally { Performance.Record(ChunkStage.LightSources, started); }
        }, out bool hit);
        Performance.CacheHit("source", hit);
        return source;
    }

    private byte[] BuildFrame(ChunkPos pos)
    {
        long start = ChunkPerformance.Start();
        ChunkColumn column = ColumnWithEdits(pos);
        byte[] sections = EncodeSections(column);
        long[] heights = BuildHeightmap(column);
        var lighting = _lights.GetOrAdd((pos, LightingStamp(pos)), key =>
        {
            long lightStart = ChunkPerformance.Start();
            try { return ChunkLightEngine.Calculate(this, key.Position); }
            finally { Performance.Record(ChunkStage.Lighting, lightStart); }
        }, out bool lightHit);
        Performance.CacheHit("light", lightHit);
        var writer = new PacketWriter();
        writer.WriteVarInt(0x2E); // Clientbound Level Chunk With Light
        writer.WriteInt32(pos.X);
        writer.WriteInt32(pos.Z);
        writer.WriteVarInt(3);
        foreach (int type in new[] { 1, 4, 5 })
        {
            writer.WriteVarInt(type);
            writer.WriteVarInt(heights.Length);
            foreach (long height in heights) writer.WriteInt64(height);
        }
        writer.WriteByteArray(sections);
        writer.WriteVarInt(0); // no block entities yet
        lighting.Write(writer);
        byte[] body = writer.ToArray();
        if (body.Length > Protocol.MaxPacketBytes)
            throw new InvalidDataException("Chunk packet exceeds the protocol limit.");
        var prefix = new PacketWriter();
        prefix.WriteVarInt(body.Length);
        byte[] header = prefix.ToArray();
        byte[] frame = new byte[header.Length + body.Length];
        header.CopyTo(frame, 0);
        body.CopyTo(frame, header.Length);
        Performance.Record(ChunkStage.Encode, start);
        return frame;
    }

    private static long[] BuildHeightmap(ChunkColumn column)
    {
        // 384-block Overworld => 9 bits per height, packed 7 entries per long.
        var heights = new ulong[37];
        for (int z = 0; z < 16; z++)
        for (int x = 0; x < 16; x++)
        {
            int index = x + z * 16;
            ulong height = (ulong)(column.TopY[index] + 1 - ChunkColumn.MinY);
            heights[index / 7] |= height << ((index % 7) * 9);
        }
        var result = new long[heights.Length];
        for (int i = 0; i < heights.Length; i++) result[i] = unchecked((long)heights[i]);
        return result;
    }

    // Exposed for byte-level regression tests and future chunk storage.
    public byte[] SerializeSections(ChunkPos pos) => EncodeSections(ColumnWithEdits(pos));

    private static byte[] EncodeSections(ChunkColumn column)
    {
        var p = new PacketWriter();
        foreach (WorldSection section in column.Sections)
        {
            p.WriteInt16((short)section.NonAir);
            p.WriteInt16((short)section.FluidCount); // 26.3 fluid state count
            WritePalette(p, section.Blocks, 4);
            WritePalette(p, section.Biomes, 1);
        }
        return p.ToArray();
    }

    private static void WritePalette(PacketWriter p, ushort[] values, int minBits)
    {
        int[] rented = ArrayPool<int>.Shared.Rent(values.Length);
        try
        {
            for (int i = 0; i < values.Length; i++) rented[i] = values[i];
            WritePalette(p, rented.AsSpan(0, values.Length), minBits);
        }
        finally { ArrayPool<int>.Shared.Return(rented); }
    }

    private static void WritePalette(PacketWriter p, int[] values, int minBits) =>
        WritePalette(p, values.AsSpan(), minBits);

    private static void WritePalette(PacketWriter p, ReadOnlySpan<int> values, int minBits)
    {
        // First-occurrence palette order is the existing v7 wire layout.
        // Avoid Distinct(), Select(), ToDictionary(), per-value boxing and
        // conversion arrays in the hot serialization loop.
        var indexes = new Dictionary<int, int>();
        var palette = new List<int>(Math.Min(256, values.Length));
        for (int i = 0; i < values.Length; i++)
        {
            int value = values[i];
            if (indexes.TryAdd(value, palette.Count)) palette.Add(value);
        }
        if (palette.Count == 1)
        {
            p.WriteByte(0);
            p.WriteVarInt(palette[0]);
            return;
        }
        bool blocks = minBits == 4;
        bool directPalette = palette.Count > (blocks ? 256 : 8);
        int bits = directPalette ? (blocks ? 16 : 7) :
            Math.Max(minBits, (int)Math.Ceiling(Math.Log2(palette.Count)));
        int perLong = 64 / bits;
        p.WriteByte((byte)bits);
        if (!directPalette)
        {
            p.WriteVarInt(palette.Count);
            foreach (int value in palette) p.WriteVarInt(value);
        }
        // 26.3 containers do not encode an array length for packed longs.
        int offset = 0;
        while (offset < values.Length)
        {
            ulong word = 0;
            int count = Math.Min(perLong, values.Length - offset);
            for (int i = 0; i < count; i++)
            {
                int state = values[offset + i];
                int index = directPalette ? state : indexes[state];
                word |= (ulong)index << (i * bits);
            }
            p.WriteInt64(unchecked((long)word));
            offset += count;
        }
    }

}

// Client-driven chunk-batch flow control. Start with a nearby 3x3 square,
// then fill the configured radius in batches as the client acknowledges each
// completed batch. This prevents large bursts of chunks delaying spawn or
// leaving the client with a partially loaded view.
public sealed class PlayerChunkView : Amethyst.Shards.IShardChunkView, Amethyst.Shards.IShardChunkSubscriptions
{
    private readonly TerrainWorld _world;
    private const int RenderHaloChunks = 1;
    private static readonly bool TraceChunks =
        Environment.GetEnvironmentVariable("AMETHYST_TRACE_CHUNKS") == "1";
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly HashSet<ChunkPos> _loaded = new();
    private HashSet<ChunkPos> _wanted = new();
    private ChunkPos? _center;
    private bool _awaitingBatchAck;
    private bool _batchInProgress;
    private CancellationTokenSource? _batchCancellation;
    private int _batchSize = 9;
    public PlayerChunkView(TerrainWorld world) => _world = world;

    public async Task<bool> HasLoadedChunkAsync(int chunkX, int chunkZ, CancellationToken ct)
    {
        await _sync.WaitAsync(ct);
        try { return _loaded.Contains(new ChunkPos(chunkX, chunkZ)); }
        finally { _sync.Release(); }
    }

    public async Task UpdateAsync(PacketChannel channel, PlayerPose pose, CancellationToken ct)
    {
        await _sync.WaitAsync(ct);
        try
        {
            var center = new ChunkPos(TerrainWorld.ChunkCoord(pose.X), TerrainWorld.ChunkCoord(pose.Z));
            if (_center != center)
            {
                // Cancel obsolete queued preparation while keeping request
                // deduplication for other connected players intact.
                _batchCancellation?.Cancel();
                int radius = _world.ViewDistance + RenderHaloChunks;
                var wanted = new HashSet<ChunkPos>();
                for (int dx = -radius; dx <= radius; dx++)
                for (int dz = -radius; dz <= radius; dz++)
                    wanted.Add(new ChunkPos(center.X + dx, center.Z + dz));

                int unloaded = 0;
                // Unload BEFORE changing center. 26.3 unload fields are Z then X.
                foreach (ChunkPos old in _loaded.Except(wanted).ToArray())
                {
                    await channel.SendAsync(0x26, p =>
                    {
                        p.WriteInt32(old.Z);
                        p.WriteInt32(old.X);
                    }, ct);
                    _loaded.Remove(old);
                    unloaded++;
                }
                _center = center;
                _wanted = wanted;
                await channel.SendAsync(0x60, p =>
                {
                    p.WriteVarInt(center.X);
                    p.WriteVarInt(center.Z);
                }, ct);
                if (TraceChunks)
                    Console.WriteLine($"[chunks] center=({center.X},{center.Z}), " +
                        $"loaded={_loaded.Count}, wanted={_wanted.Count}, unloaded={unloaded}");
            }
            ScheduleBatchLocked(channel, ct);
        }
        finally { _sync.Release(); }
    }

    public async Task OnBatchReceivedAsync(PacketChannel channel, float requestedChunksPerTick, CancellationToken ct)
    {
        await _sync.WaitAsync(ct);
        try
        {
            if (!_awaitingBatchAck) return;
            _awaitingBatchAck = false;
            if (float.IsFinite(requestedChunksPerTick) && requestedChunksPerTick > 0)
                _batchSize = Math.Clamp((int)Math.Ceiling(requestedChunksPerTick), 1, 16);
            ScheduleBatchLocked(channel, ct);
        }
        finally { _sync.Release(); }
    }

    private void ScheduleBatchLocked(PacketChannel channel, CancellationToken connectionStop)
    {
        if (_awaitingBatchAck || _batchInProgress || _center is not { } center ||
            connectionStop.IsCancellationRequested) return;
        ChunkPos[] next = _wanted.Except(_loaded)
            .OrderBy(c => Math.Max(Math.Abs(c.X - center.X), Math.Abs(c.Z - center.Z)))
            .ThenBy(c => (long)(c.X - center.X) * (c.X - center.X) +
                         (long)(c.Z - center.Z) * (c.Z - center.Z))
            .Take(_batchSize).ToArray();
        if (next.Length == 0) return;
        _batchInProgress = true;
        _batchCancellation = CancellationTokenSource.CreateLinkedTokenSource(connectionStop);
        CancellationToken batchToken = _batchCancellation.Token;
        // Intentionally not awaited: the game packet receiver must remain
        // responsive while bounded workers prepare terrain and light.
        _ = Task.Run(() => PrepareAndSendBatchAsync(channel, center, next, connectionStop, batchToken));
    }

    private async Task PrepareAndSendBatchAsync(PacketChannel channel, ChunkPos originalCenter,
        ChunkPos[] next, CancellationToken connectionStop, CancellationToken batchToken)
    {
        bool failed = false;
        try
        {
            Task<byte[]>[] requests = next.Select(pos => _world.Scheduler.PrepareAsync(pos,
                Math.Max(Math.Abs(pos.X - originalCenter.X), Math.Abs(pos.Z - originalCenter.Z)),
                batchToken)).ToArray();
            byte[][] frames = await Task.WhenAll(requests).ConfigureAwait(false);
            await _sync.WaitAsync(batchToken).ConfigureAwait(false);
            try
            {
                if (_center != originalCenter || batchToken.IsCancellationRequested) return;
                await channel.SendAsync(0x0C, null, batchToken); // Chunk Batch Start
                for (int i = 0; i < next.Length; i++)
                {
                    long start = ChunkPerformance.Start();
                    await channel.WriteAsync(frames[i], batchToken);
                    _world.Performance.Record(ChunkStage.Send, start);
                    _loaded.Add(next[i]);
                }
                await channel.SendAsync(0x0B, p => p.WriteVarInt(next.Length), batchToken);
                _awaitingBatchAck = true;
                if (TraceChunks)
                    Console.WriteLine($"[chunks] sent batch={next.Length}, " +
                        $"loaded={_loaded.Count}/{_wanted.Count}, center=({originalCenter.X},{originalCenter.Z})");
            }
            finally { _sync.Release(); }
        }
        catch (OperationCanceledException) when (batchToken.IsCancellationRequested) { }
        catch (IOException) when (connectionStop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failed = true;
            // A failed preparation is visible rather than silently leaving
            // an invisible hole while the client waits forever for a batch.
            Console.Error.WriteLine($"[chunks] Unable to send batch around {originalCenter}: {ex}");
            if (!connectionStop.IsCancellationRequested)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await channel.SendAsync(0x20, writer => writer.WritePlainTextComponent(
                        "Server could not prepare world chunks. Check the server console."), timeout.Token);
                }
                catch (Exception) { /* connection is already closing */ }
            }
        }
        finally
        {
            await _sync.WaitAsync(CancellationToken.None);
            try
            {
                _batchInProgress = false;
                _batchCancellation?.Dispose();
                _batchCancellation = null;
                if (!failed && !connectionStop.IsCancellationRequested && !_awaitingBatchAck)
                    ScheduleBatchLocked(channel, connectionStop);
            }
            finally { _sync.Release(); }
        }
    }
}
