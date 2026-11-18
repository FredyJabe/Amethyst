using Amethyst.Generation.Noise;

namespace Amethyst.Generation.Terrain;

/// <summary>
/// Original, seed-deterministic approximation of underground aquifers. It does
/// not reproduce Mojang's aquifer cell graph or simulate fluid flow. A smooth
/// local water table is combined with a floodedness field; a low, seeded lava
/// table fills some of the deepest cavities. Sampling is world-coordinate-based
/// and independent of the order in which columns are generated.
/// </summary>
public sealed class AquiferSampler
{
    private readonly int _seaLevel, _lavaLevel, _waterOffset;
    private readonly bool _sparseAquifers;
    private readonly FractalNoise2D _waterTable, _floodedness, _lavaVariation;
    private readonly FractalNoise3D _waterBasins, _lavaBasins;

    public AquiferSampler(ServerOptions options)
    {
        _seaLevel = options.SeaLevel;
        _lavaLevel = options.AquiferLavaLevel;
        _waterOffset = options.AquiferWaterTableOffset;
        _sparseAquifers = options.GeneratorVersion >= 7;
        _waterBasins = new FractalNoise3D(options.Seed, 0x4151554946455237UL, 3);
        _lavaBasins = new FractalNoise3D(options.Seed, 0x4C41564142415337UL, 2);
        _waterTable = new FractalNoise2D(options.Seed, 0x4151554946455254UL, 3);
        _floodedness = new FractalNoise2D(options.Seed, 0x4151554946455246UL, 3);
        _lavaVariation = new FractalNoise2D(options.Seed, 0x415155494645524CUL, 2);
    }

    public int WaterTable(int x, int z)
    {
        // A world-space field rather than one random value per chunk prevents
        // step changes where two neighboring columns meet.
        double local = _waterTable.Sample(x, z, 190) * 20;
        return Math.Clamp((int)Math.Round(_seaLevel - _waterOffset + local),
            ChunkColumn.MinY + 12, _seaLevel - 2);
    }

    public int LavaTable(int x, int z) => Math.Clamp(
        _lavaLevel + (int)Math.Round(_lavaVariation.Sample(x, z, 230) * 5),
        ChunkColumn.MinY + 2, _seaLevel - 14);

    /// <summary>Returns air/water/lava for a cave voxel known to have negative density.</summary>
    public ushort CaveFluid(int x, int y, int z, int surfaceY)
    {
        // Leave exposed caves and the top few meters of the landscape dry.
        // The overworld sea fill is handled separately after density generation.
        if (y >= surfaceY - 12 || y > _seaLevel - 5)
            return TerrainBlocks.Air;

        double wet = _floodedness.Sample(x, z, 300);
        int lava = LavaTable(x, z);
        if (!_sparseAquifers)
        {
            // Frozen legacy mapping for saved worlds generated with v4..v6.
            if (y <= lava && wet > -0.55) return TerrainBlocks.Lava;
            if (y <= WaterTable(x, z) && wet > -0.16) return TerrainBlocks.Water;
            return TerrainBlocks.Air;
        }

        // v7: local 3D aquifer basins, gated by broad horizontal wet regions.
        // Most negative-density cave voxels are AIR. Wet areas are intentionally
        // uncommon; the water table still gives fluid a consistent upper level.
        // Distinct lava noise prevents the entire deep cave system flooding.
        double wetCell = _waterBasins.Sample(x, y, z, 110);
        double wetness = wet * 0.55 + wetCell * 0.8;
        if (y <= lava && wetness > 0.11 && _lavaBasins.Sample(x, y, z, 96) > 0.09)
            return TerrainBlocks.Lava;
        if (y <= WaterTable(x, z) && wetness > 0.20)
            return TerrainBlocks.Water;
        return TerrainBlocks.Air;
    }
}
