using Amethyst.Generation.Climate;
using Amethyst.Generation.Terrain;

namespace Amethyst;

// Java 26.3 (protocol 777) GLOBAL block-state IDs, not block-registry IDs.
// Source: RosegoldMC/minecraft-data.cr, data/26.3/blocks.json (Mojang reports).
public static class TerrainBlocks
{
    public const ushort Air = 0;
    public const ushort Stone = 1;
    public const ushort Grass = 9; // grass_block[snowy=false]
    public const ushort Dirt = 10;
    public const ushort Bedrock = 88;
    public const ushort Water = 89; // water[level=0]
    public const ushort Lava = 105; // lava[level=0]
    public const ushort RedSand = 126;
    public const ushort Sand = 121;
    public const ushort Gravel = 127;
    public const ushort Sandstone = 677;
    public const ushort SnowBlock = 8598;
    public const ushort Clay = 8616;
    public const ushort Mycelium = 10653; // mycelium[snowy=false]
    public const ushort Terracotta = 14759;
    public const ushort Tuff = 26809;
    public const ushort Dripstone = 33565;
    public const ushort Moss = 33712;
    public const ushort Mud = 33772;
    public const ushort Deepslate = 33774; // deepslate (axis variant; valid 26.3 state)
    // Additional Minecraft Java 26.3 global states, checked against Mojang's
    // generated 26.3 block registry. Logs are axis=y; leaves are persistent,
    // non-waterlogged (distance=1) to prevent future leaf-decay ticks.
    public const ushort OakLog = 140, SpruceLog = 143, BirchLog = 146;
    public const ushort JungleLog = 149, AcaciaLog = 152, DarkOakLog = 158, MangroveLog = 164;
    public const ushort OakLeaves = 268, SpruceLeaves = 296, BirchLeaves = 324;
    public const ushort JungleLeaves = 352, AcaciaLeaves = 380, DarkOakLeaves = 436, MangroveLeaves = 492;
    public const ushort ShortGrass = 2363, Fern = 2364, DeadBush = 2365;
    public const ushort ShortDryGrass = 2368;
    public const ushort AzureBluet = 3819, Allium = 3818, LilyOfTheValley = 3827;
    // 26.3 half ordering: upper (14770), lower (14771).
    public const ushort TallGrassUpper = 14770, TallGrassLower = 14771;
    public const ushort Dandelion = 3813, Poppy = 3816, Cornflower = 3825;
    public const ushort BrownMushroom = 3828, RedMushroom = 3829, Cactus = 8599;
    public const ushort Cobblestone = 14, MossyCobblestone = 4924, ChiseledSandstone = 678;
    public const ushort Andesite = 6, Granite = 2, Diorite = 4;
    public const ushort OakLogAxisX = 139, OakLogAxisZ = 141;
    public const ushort CoalOre = 136, DeepslateCoalOre = 137;
    public const ushort IronOre = 134, DeepslateIronOre = 135;
    public const ushort CopperOre = 31147, DeepslateCopperOre = 31148;
    public const ushort GoldOre = 132, DeepslateGoldOre = 133;
    public const ushort RedstoneOre = 8552, DeepslateRedstoneOre = 8554;
    public const ushort LapisOre = 662, DeepslateLapisOre = 663;
    public const ushort DiamondOre = 6863, DeepslateDiamondOre = 6864;
    public const ushort EmeraldOre = 11315, DeepslateEmeraldOre = 11316;
}

public enum SurfaceKind { Grass, Sand, Snow, Rock, Riverbed }

public readonly record struct TerrainSample(int GroundY, int BiomeId, SurfaceKind Surface, bool IsRiver = false);

/// <summary>
/// Versioned height-field generator. Version 1 preserves the original terrain
/// mapping; version 2 connects continentalness, erosion and folded weirdness
/// to elevation. Version 3 uses this height shape for 3D density; version 4 keeps the
/// geometry but adds aquifers and material rules.
/// </summary>
public sealed class TerrainGenerator
{
    private readonly ulong _seed;
    private readonly int _baseHeight;
    private readonly int _seaLevel;
    private readonly int _variation;
    private readonly double _scale;
    private readonly ClimateSampler _climateSampler;
    private readonly BiomeResolver _biomeResolver;
    private readonly TerrainShaper? _shaper;
    private readonly BoundedLazyCache<(int X, int Z), TerrainSample> _sampleCache;

    public int SeaLevel => _seaLevel;
    public long Seed { get; }
    public int GeneratorVersion { get; }

    public TerrainGenerator(ServerOptions options)
    {
        Seed = options.Seed;
        GeneratorVersion = options.GeneratorVersion;
        _seed = unchecked((ulong)options.Seed);
        _sampleCache = new(options.CachedTerrainSamples);
        _baseHeight = options.BaseHeight;
        _seaLevel = options.SeaLevel;
        _variation = options.HeightVariation;
        _scale = options.TerrainScale;
        if (options.GeneratorVersion is < 1 or > 7)
            throw new NotSupportedException("Only generatorVersion 1 through 7 is implemented.");
        if (options.GeneratorVersion >= 2)
            _shaper = new TerrainShaper(options);
        _climateSampler = new ClimateSampler(options.Seed, options.ClimateScale);
        _biomeResolver = new BiomeResolver(options.Biome);
    }

    // A smooth, reproducible 2D field formed from lattice values. Coordinates are
    // absolute world positions, never local chunk coordinates.
    private static ulong Scramble(ulong n)
    {
        unchecked
        {
            n ^= n >> 30; n *= 0xbf58476d1ce4e5b9UL;
            n ^= n >> 27; n *= 0x94d049bb133111ebUL;
            return n ^ (n >> 31);
        }
    }

    private double Hash(long x, long z, ulong salt)
    {
        unchecked
        {
            ulong bits = Scramble(_seed ^ salt ^
                (unchecked((ulong)x) * 0x9e3779b185ebca87UL) ^
                (unchecked((ulong)z) * 0xc2b2ae3d27d4eb4fUL));
            return (bits >> 11) * (1.0 / (1UL << 53)) * 2 - 1;
        }
    }

    private static double Smooth(double t) => t * t * (3 - 2 * t);
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private double ValueNoise(double x, double z, ulong salt)
    {
        long ix = (long)Math.Floor(x), iz = (long)Math.Floor(z);
        double tx = Smooth(x - ix), tz = Smooth(z - iz);
        return Lerp(Lerp(Hash(ix, iz, salt), Hash(ix + 1, iz, salt), tx),
                    Lerp(Hash(ix, iz + 1, salt), Hash(ix + 1, iz + 1, salt), tx), tz);
    }

    private double Fbm(double x, double z, double wavelength, int octaves, ulong salt)
    {
        double amplitude = 1, sum = 0, norm = 0;
        x *= _scale / wavelength;
        z *= _scale / wavelength;
        for (int octave = 0; octave < octaves; octave++)
        {
            sum += ValueNoise(x, z, salt + unchecked((ulong)octave) * 0x9e3779b97f4a7c15UL)
                * amplitude;
            norm += amplitude;
            x *= 2; z *= 2; amplitude *= 0.5;
        }
        return sum / norm;
    }

    public TerrainSample Sample(int x, int z) =>
        _sampleCache.GetOrAdd((x, z), key => ComputeSample(key.X, key.Z), out _);

    private TerrainSample ComputeSample(int x, int z)
    {
        ClimateSample climate = _climateSampler.Sample(x, z);
        TerrainShape? shape = _shaper?.Shape(x, z, climate);
        double height = shape?.Height ?? LegacyHeight(x, z);

        // Both generator versions maintain the original 5x5 stone platform
        // at Y=79 with a smoothly blended approach, rather than a floating pad.
        double distance = Math.Sqrt((double)x * x + (double)z * z);
        double blend = Smooth(Math.Clamp((distance - 6) / 42, 0, 1));
        height = Lerp(79, height, blend);
        int ground = Math.Clamp((int)Math.Round(height), ChunkColumn.MinY + 4, ChunkColumn.MaxY - 4);

        bool river = shape is { } candidate && distance >= 44
            && candidate.RiverInfluence > 0.78 && ground <= _seaLevel - 1;
        BiomeSelection biome = _biomeResolver.Resolve(
            climate, ground, _seaLevel, _baseHeight, _variation,
            spawnProtected: distance < 44, river: river);
        return new TerrainSample(ground, biome.BiomeId, biome.Surface, river);
    }

    // The original Phase-1 elevation code stays intact for worlds explicitly
    // configured with generatorVersion: 1. Existing seed mappings remain stable.
    private double LegacyHeight(int x, int z)
    {
        double continental = Fbm(x, z, 1200, 4, 0x100UL);
        double hills = Fbm(x, z, 220, 4, 0x200UL);
        double mountainField = Fbm(x, z, 750, 3, 0x300UL);
        double ridges = 1 - Math.Abs(Fbm(x, z, 115, 4, 0x400UL));
        double mountainStrength = Smooth(Math.Clamp((mountainField - 0.04) / 0.48, 0, 1));
        return _baseHeight + _variation *
            (continental * 0.9 + hills * 0.40 + mountainStrength * ridges * 1.35);
    }

    /// <summary>Unblended Phase-2 elevation and river/relief diagnostics.</summary>
    public TerrainShape SampleShape(int x, int z)
    {
        if (_shaper is null)
            throw new NotSupportedException("Terrain shaping requires generatorVersion >= 2.");
        return _shaper.Shape(x, z, _climateSampler.Sample(x, z));
    }

    /// <summary>Raw five-field climate sample for diagnostics and Phase 2 terrain shaping.</summary>
    public ClimateSample SampleClimate(int x, int z) => _climateSampler.Sample(x, z);
}
