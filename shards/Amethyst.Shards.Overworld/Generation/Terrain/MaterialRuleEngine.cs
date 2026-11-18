using Amethyst.Generation.Noise;

namespace Amethyst.Generation.Terrain;

/// <summary>
/// Ordered, original material rules applied after density and aquifers. This
/// deliberately keeps terrain geometry and block composition separate. The
/// rules are C# code; they are NOT Mojang data-pack material-rule JSON.
/// </summary>
public sealed class MaterialRuleEngine
{
    private readonly int _surfaceDepth;
    private readonly uint _seed;
    private readonly FractalNoise3D _caveTexture;

    public MaterialRuleEngine(ServerOptions options)
    {
        _surfaceDepth = options.SurfaceDepth;
        _seed = unchecked((uint)options.Seed) ^ unchecked((uint)(options.Seed >> 32));
        _caveTexture = new FractalNoise3D(options.Seed, 0x4d4154455249414cUL, 2);
    }

    public static bool IsFluid(ushort state) =>
        state is TerrainBlocks.Water or TerrainBlocks.Lava;

    // No noise evaluation required for the common-case deep bedrock/stone fill.
    public ushort BaseRock(int x, int y, int z)
    {
        if (y <= -8) return TerrainBlocks.Deepslate;
        if (y >= 8) return TerrainBlocks.Stone;
        // Discrete, deterministic stone-to-deepslate transition in -7..+7.
        unchecked
        {
            uint h = _seed ^ ((uint)x * 374761393u) ^ ((uint)z * 668265263u) ^ ((uint)y * 2246822519u);
            h = (h ^ (h >> 13)) * 1274126177u;
            double t = (h >> 8) / 16777216.0;
            return t < (8 - y) / 16.0 ? TerrainBlocks.Deepslate : TerrainBlocks.Stone;
        }
    }

    /// <summary>Replace top-exposed stone, and layers below it, using biome materials.</summary>
    public void ApplySurface(ChunkColumn column, int localX, int localZ,
        int surfaceY, SurfaceKind surface, int biomeId)
    {
        string biome = VanillaRegistries.EntryAt("minecraft:worldgen/biome", biomeId);
        // Bedrock and spawn protection are not overwritten by these rules.
        for (int depth = 0; depth < _surfaceDepth; depth++)
        {
            int y = surfaceY - depth;
            if (y <= ChunkColumn.MinY) break;
            ushort current = column.GetBlock(localX, y, localZ);
            if (current is not (TerrainBlocks.Stone or TerrainBlocks.Deepslate)) break;
            ushort state = SelectSurface(surface, biome, depth, current);
            column.SetBlock(localX, y, localZ, state);
        }
    }

    public static ushort SelectSurface(SurfaceKind kind, string biome, int depth, ushort rock)
    {
        // First matching biome-specific rule wins.
        if (biome is "minecraft:badlands")
            return depth == 0 ? TerrainBlocks.RedSand : TerrainBlocks.Terracotta;
        if (biome is "minecraft:swamp" or "minecraft:mangrove_swamp")
            return depth <= 1 ? TerrainBlocks.Mud : TerrainBlocks.Dirt;
        if (biome is "minecraft:mushroom_fields")
            return depth == 0 ? TerrainBlocks.Mycelium : TerrainBlocks.Dirt;
        return kind switch
        {
            SurfaceKind.Sand => depth <= 2 ? TerrainBlocks.Sand : TerrainBlocks.Sandstone,
            SurfaceKind.Riverbed => depth == 0 ? TerrainBlocks.Gravel : TerrainBlocks.Dirt,
            SurfaceKind.Snow => depth == 0 ? TerrainBlocks.SnowBlock : TerrainBlocks.Dirt,
            SurfaceKind.Rock => rock,
            _ => depth == 0 ? TerrainBlocks.Grass : TerrainBlocks.Dirt
        };
    }

    /// <summary>Decorate exposed underground floors and ceilings without placing features.</summary>
    public void ApplyCaveFaces(ChunkColumn column, int lx, int lz, int worldX,
        int worldZ, int topSolid)
    {
        // Apply to exposed rock only; leave large, contiguous rock masses alone.
        // Sampling only this column avoids cross-chunk generation recursion.
        for (int y = Math.Min(topSolid - 13, ChunkColumn.MaxY - 1);
             y > ChunkColumn.MinY + 1; y--)
        {
            ushort block = column.GetBlock(lx, y, lz);
            if (block is not (TerrainBlocks.Stone or TerrainBlocks.Deepslate)) continue;
            ushort above = column.GetBlock(lx, y + 1, lz);
            ushort below = column.GetBlock(lx, y - 1, lz);
            bool floor = above is TerrainBlocks.Air or TerrainBlocks.Water;
            bool ceiling = below == TerrainBlocks.Air;
            if (!floor && !ceiling) continue;
            double n = _caveTexture.Sample(worldX, y, worldZ, 36);
            if (floor && above == TerrainBlocks.Water && n > -0.25)
                column.SetBlock(lx, y, lz, TerrainBlocks.Clay);
            else if (floor && above == TerrainBlocks.Air && y > -32 && n > 0.23)
                column.SetBlock(lx, y, lz, TerrainBlocks.Moss);
            else if (ceiling && n < -0.32)
                column.SetBlock(lx, y, lz, TerrainBlocks.Dripstone);
            else if (n < -0.51)
                column.SetBlock(lx, y, lz, TerrainBlocks.Tuff);
        }
    }
}
