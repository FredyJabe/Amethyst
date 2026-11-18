using Amethyst.Generation.Terrain;

namespace Amethyst.Generation.Features;

/// <summary>
/// Deterministic 2D feature anchors. Every column scans the same 3x3 anchor
/// halo and clips each tree's blocks to its own 16x16 column. No neighbor chunk
/// is read or mutated, avoiding load-order-dependent half trees and deadlocks.
/// </summary>
public sealed class VegetationGenerator
{
    private readonly TerrainGenerator _terrain;
    private readonly DensitySampler _density;
    private readonly long _seed;
    private readonly double _frequency;
    private readonly int _seaLevel;
    private readonly bool _natural;

    private readonly record struct Tree(ushort Log, ushort Leaves, int MinHeight, int Variation, bool Conifer);
    private static readonly Tree Oak = new(TerrainBlocks.OakLog, TerrainBlocks.OakLeaves, 4, 3, false);
    private static readonly Tree Birch = new(TerrainBlocks.BirchLog, TerrainBlocks.BirchLeaves, 5, 3, false);
    private static readonly Tree Spruce = new(TerrainBlocks.SpruceLog, TerrainBlocks.SpruceLeaves, 7, 4, true);
    private static readonly Tree Jungle = new(TerrainBlocks.JungleLog, TerrainBlocks.JungleLeaves, 7, 5, false);
    private static readonly Tree Acacia = new(TerrainBlocks.AcaciaLog, TerrainBlocks.AcaciaLeaves, 5, 3, false);
    private static readonly Tree DarkOak = new(TerrainBlocks.DarkOakLog, TerrainBlocks.DarkOakLeaves, 5, 3, false);
    private static readonly Tree Mangrove = new(TerrainBlocks.MangroveLog, TerrainBlocks.MangroveLeaves, 5, 3, false);

    public VegetationGenerator(TerrainGenerator terrain, DensitySampler density, ServerOptions settings)
    {
        _terrain = terrain;
        _density = density;
        _seed = settings.Seed;
        _frequency = settings.FeatureDensity;
        _seaLevel = settings.SeaLevel;
        _natural = settings.GeneratorVersion >= 6;
    }

    public void Decorate(ChunkColumn column)
    {
        if (_frequency <= 0) return;
        PlaceTrees(column);
        PlacePlants(column);
    }

    private void PlaceTrees(ChunkColumn target)
    {
        ChunkPos pos = target.Position;
        // A tree's canopy extends at most four blocks horizontally.
        for (int cz = pos.Z - 1; cz <= pos.Z + 1; cz++)
        for (int cx = pos.X - 1; cx <= pos.X + 1; cx++)
        for (int candidate = 0; candidate < 2; candidate++)
        {
            ulong h = FeatureHash.At(_seed, cx, candidate, cz, 0x545245452d534545UL);
            int wx = checked(cx * 16 + FeatureHash.Index(FeatureHash.Mix(h ^ 1), 16));
            int wz = checked(cz * 16 + FeatureHash.Index(FeatureHash.Mix(h ^ 2), 16));
            if ((long)wx * wx + (long)wz * wz < 42L * 42) continue;
            TerrainSample terrain = _terrain.Sample(wx, wz);
            if (terrain.Surface != SurfaceKind.Grass || terrain.IsRiver) continue;
            string biome = VanillaRegistries.EntryAt("minecraft:worldgen/biome", terrain.BiomeId);
            (Tree tree, double chance) = TreeForBiome(biome, h);
            if (FeatureHash.Unit(FeatureHash.Mix(h ^ 3)) >=
                Math.Min(1, chance * _frequency)) continue;
            int top = _density.TopSolidAt(wx, wz);
            if (top <= _seaLevel + 1 || top >= ChunkColumn.MaxY - 17) continue;
            int height = tree.MinHeight + FeatureHash.Index(FeatureHash.Mix(h ^ 4), tree.Variation);
            if (_natural)
            {
                int extra = tree.Log == TerrainBlocks.JungleLog ? 4 :
                    tree.Log == TerrainBlocks.SpruceLog ? 2 : 0;
                NaturalTreeShapes.Place(target, wx, top + 1, wz, height + extra,
                    tree.Log, tree.Leaves, h);
            }
            else PlaceTreeClipped(target, wx, top + 1, wz, height, tree, h);
        }
    }

    private static (Tree Tree, double Chance) TreeForBiome(string biome, ulong h)
    {
        return biome switch
        {
            "minecraft:forest" or "minecraft:flower_forest" or
            "minecraft:dappled_forest" or "minecraft:windswept_forest" =>
                ((h & 3UL) == 0 ? Birch : Oak, 0.72),
            "minecraft:birch_forest" or "minecraft:old_growth_birch_forest" => (Birch, 0.78),
            "minecraft:dark_forest" or "minecraft:pale_garden" => (DarkOak, 0.88),
            "minecraft:taiga" or "minecraft:old_growth_pine_taiga" or
            "minecraft:old_growth_spruce_taiga" or "minecraft:snowy_taiga" or
            "minecraft:grove" => (Spruce, 0.77),
            "minecraft:jungle" or "minecraft:sparse_jungle" or
            "minecraft:bamboo_jungle" => (Jungle, 0.85),
            "minecraft:savanna" or "minecraft:savanna_plateau" or
            "minecraft:windswept_savanna" => (Acacia, 0.35),
            "minecraft:swamp" or "minecraft:mangrove_swamp" => (Mangrove, 0.55),
            "minecraft:cherry_grove" => (Birch, 0.74), // cherry wood comes in a later feature pass
            "minecraft:wooded_badlands" => (Oak, 0.26),
            "minecraft:plains" or "minecraft:meadow" or
            "minecraft:sunflower_plains" => (Oak, 0.085),
            _ => (Oak, 0.0)
        };
    }

    private static void PlaceTreeClipped(ChunkColumn target,
        int x, int rootY, int z, int height, Tree tree, ulong h)
    {
        // Canopy below trunk tip: round crown for broadleaf, conical for spruce.
        int tip = rootY + height - 1;
        int bottom = tree.Conifer ? rootY + 2 : tip - 2;
        for (int y = bottom; y <= tip + 2; y++)
        {
            int radius;
            if (tree.Conifer)
                radius = y >= tip + 1 ? 1 : y >= tip - 1 ? 2 :
                    Math.Clamp((tip - y) / 2 + 1, 1, 3);
            else
                radius = y == tip + 2 ? 1 : y == tip + 1 ? 2 : y == tip - 2 ? 2 : 3;
            for (int dz = -radius; dz <= radius; dz++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dz * dz > radius * radius + 1) continue;
                if (Math.Abs(dx) == radius && Math.Abs(dz) == radius &&
                    (FeatureHash.At(unchecked((long)h), x + dx, y, z + dz, 0x4c454146UL) & 1) == 0)
                    continue;
                PlaceIfReplaceable(target, x + dx, y, z + dz, tree.Leaves);
            }
        }
        for (int i = 0; i < height; i++)
            PlaceIfReplaceable(target, x, rootY + i, z, tree.Log);
    }

    private static void PlaceIfReplaceable(ChunkColumn column, int wx, int y, int wz, ushort state)
    {
        if (y < ChunkColumn.MinY || y > ChunkColumn.MaxY) return;
        ChunkPos pos = column.Position;
        if (FeatureHash.FloorDiv(wx, 16) != pos.X ||
            FeatureHash.FloorDiv(wz, 16) != pos.Z) return;
        int x = wx - pos.X * 16, z = wz - pos.Z * 16;
        ushort old = column.GetBlock(x, y, z);
        if (old is TerrainBlocks.Air or TerrainBlocks.ShortGrass or TerrainBlocks.Fern or
            TerrainBlocks.Poppy or TerrainBlocks.Dandelion)
            column.SetBlock(x, y, z, state);
    }

    private void PlacePlants(ChunkColumn column)
    {
        ChunkPos pos = column.Position;
        for (int z = 0; z < 16; z++)
        for (int x = 0; x < 16; x++)
        {
            int wx = checked(pos.X * 16 + x), wz = checked(pos.Z * 16 + z);
            if ((long)wx * wx + (long)wz * wz < 18L * 18) continue;
            int groundY = column.GroundY[x + z * 16];
            if (groundY <= _seaLevel || groundY >= ChunkColumn.MaxY - 4) continue;
            if (column.GetBlock(x, groundY + 1, z) != TerrainBlocks.Air) continue;
            ushort ground = column.GetBlock(x, groundY, z);
            TerrainSample sample = _terrain.Sample(wx, wz);
            string biome = VanillaRegistries.EntryAt("minecraft:worldgen/biome", sample.BiomeId);
            ulong h = FeatureHash.At(_seed, wx, groundY, wz, 0x504c414e542d3031UL);
            double chance = PlantChance(biome, ground);
            if (_natural)
            {
                // Group meadows, ferns, flowers and sparse grass into broad
                // climate patches instead of placing each tuft uniformly.
                double patch = PatchValue(wx, wz, 18, 0x47524f554e445041UL);
                chance *= 0.28 + patch * 1.65;
                if (biome is "minecraft:plains" or "minecraft:meadow")
                    chance *= 0.75 + patch * 0.7;
                if (!IsGentleGround(column, x, z, groundY)) chance *= 0.12;
            }
            if (FeatureHash.Unit(h) >= Math.Min(1, chance * _frequency)) continue;
            ushort plant = _natural ? SelectNaturalPlant(biome, ground, wx, wz, h) :
                SelectPlant(biome, ground, h);
            if (plant == TerrainBlocks.Air) continue;
            if (plant == TerrainBlocks.Cactus)
            {
                // Keep cactus away from uncertain neighbor collision checks at chunk edges.
                if (x is 0 or 15 || z is 0 or 15) continue;
                int height = 1 + FeatureHash.Index(FeatureHash.Mix(h ^ 6), 3);
                for (int i = 1; i <= height; i++)
                    if (column.GetBlock(x, groundY + i, z) == TerrainBlocks.Air)
                        column.SetBlock(x, groundY + i, z, plant);
            }
            else if (_natural && plant == TerrainBlocks.TallGrassLower)
            {
                if (groundY + 2 <= ChunkColumn.MaxY &&
                    column.GetBlock(x, groundY + 2, z) == TerrainBlocks.Air)
                {
                    column.SetBlock(x, groundY + 1, z, TerrainBlocks.TallGrassLower);
                    column.SetBlock(x, groundY + 2, z, TerrainBlocks.TallGrassUpper);
                }
            }
            else column.SetBlock(x, groundY + 1, z, plant);
        }
    }

    private static bool IsGentleGround(ChunkColumn col, int x, int z, int y)
    {
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int nx = x + dx, nz = z + dz;
            if (nx is < 0 or > 15 || nz is < 0 or > 15) continue;
            if (Math.Abs(col.GroundY[nx + nz * 16] - y) > 2) return false;
        }
        return true;
    }

    private double PatchValue(int x, int z, int wavelength, ulong salt)
    {
        double px = x / (double)wavelength, pz = z / (double)wavelength;
        int ix = (int)Math.Floor(px), iz = (int)Math.Floor(pz);
        double fx = px - ix, fz = pz - iz;
        fx = fx * fx * (3 - 2 * fx);
        fz = fz * fz * (3 - 2 * fz);
        double a = FeatureHash.Unit(FeatureHash.At(_seed, ix, 0, iz, salt));
        double b = FeatureHash.Unit(FeatureHash.At(_seed, ix + 1, 0, iz, salt));
        double c = FeatureHash.Unit(FeatureHash.At(_seed, ix, 0, iz + 1, salt));
        double d = FeatureHash.Unit(FeatureHash.At(_seed, ix + 1, 0, iz + 1, salt));
        return (a + (b - a) * fx) * (1 - fz) + (c + (d - c) * fx) * fz;
    }

    private ushort SelectNaturalPlant(string biome, ushort ground, int x, int z, ulong h)
    {
        if (ground == TerrainBlocks.Sand) return TerrainBlocks.Cactus;
        if (ground == TerrainBlocks.Mycelium)
            return (h & 1UL) == 0 ? TerrainBlocks.BrownMushroom : TerrainBlocks.RedMushroom;
        double flowerPatch = PatchValue(x, z, 13, 0x464c4f5745525041UL);
        int choice = FeatureHash.Index(FeatureHash.Mix(h ^ 7), 9);
        if (biome is "minecraft:taiga" or "minecraft:snowy_taiga" or
            "minecraft:jungle" or "minecraft:sparse_jungle")
            return choice <= 3 ? TerrainBlocks.Fern : TerrainBlocks.ShortGrass;
        if (biome is "minecraft:savanna" or "minecraft:savanna_plateau")
            return choice <= 4 ? TerrainBlocks.ShortDryGrass : TerrainBlocks.ShortGrass;
        if (flowerPatch > 0.59 && (biome is "minecraft:meadow" or
            "minecraft:flower_forest" or "minecraft:plains" or "minecraft:forest"))
            return choice switch
            {
                0 => TerrainBlocks.Dandelion,
                1 => TerrainBlocks.Poppy,
                2 => TerrainBlocks.Cornflower,
                3 => TerrainBlocks.Allium,
                4 => TerrainBlocks.AzureBluet,
                5 => TerrainBlocks.LilyOfTheValley,
                _ => TerrainBlocks.ShortGrass
            };
        if (choice == 0 && flowerPatch > 0.52) return TerrainBlocks.TallGrassLower;
        return TerrainBlocks.ShortGrass;
    }

    private static double PlantChance(string biome, ushort ground) => ground switch
    {
        TerrainBlocks.Sand when biome is "minecraft:desert" or "minecraft:badlands" => 0.02,
        TerrainBlocks.Grass when biome is "minecraft:plains" or "minecraft:meadow" or
            "minecraft:sunflower_plains" or "minecraft:flower_forest" => 0.28,
        TerrainBlocks.Grass when biome is "minecraft:forest" or "minecraft:birch_forest" or
            "minecraft:jungle" or "minecraft:sparse_jungle" or "minecraft:taiga" => 0.18,
        TerrainBlocks.Grass => 0.11,
        TerrainBlocks.Mycelium => 0.10,
        _ => 0
    };

    private static ushort SelectPlant(string biome, ushort ground, ulong h)
    {
        if (ground == TerrainBlocks.Sand) return TerrainBlocks.Cactus;
        if (ground == TerrainBlocks.Mycelium)
            return (h & 1UL) == 0 ? TerrainBlocks.BrownMushroom : TerrainBlocks.RedMushroom;
        if (biome is "minecraft:taiga" or "minecraft:snowy_taiga" or
            "minecraft:jungle" or "minecraft:sparse_jungle")
            return (h & 3UL) == 0 ? TerrainBlocks.Fern : TerrainBlocks.ShortGrass;
        return FeatureHash.Index(FeatureHash.Mix(h ^ 7), 12) switch
        {
            0 => TerrainBlocks.Dandelion,
            1 => TerrainBlocks.Poppy,
            2 when biome == "minecraft:flower_forest" => TerrainBlocks.Cornflower,
            _ => TerrainBlocks.ShortGrass
        };
    }
}
