namespace Amethyst.Generation.Features;

/// <summary>
/// Independently seeded, small multi-block ore veins. Anchors live in world-aligned
/// 16³ cells. Each target chunk scans a one-cell halo and CLIPS writes locally,
/// so neighboring columns generate identically regardless of load order.
/// </summary>
public sealed class OreVeinGenerator
{
    private readonly long _seed;
    private readonly double _frequency;

    public OreVeinGenerator(ServerOptions settings)
    {
        _seed = settings.Seed;
        _frequency = settings.OreFrequency;
    }

    // Variant order: stone ore / deepslate ore. These are 26.3 GLOBAL block-state IDs.
    private readonly record struct Ore(ushort Stone, ushort Deep);
    private static readonly Ore Coal = new(TerrainBlocks.CoalOre, TerrainBlocks.DeepslateCoalOre);
    private static readonly Ore Iron = new(TerrainBlocks.IronOre, TerrainBlocks.DeepslateIronOre);
    private static readonly Ore Copper = new(TerrainBlocks.CopperOre, TerrainBlocks.DeepslateCopperOre);
    private static readonly Ore Gold = new(TerrainBlocks.GoldOre, TerrainBlocks.DeepslateGoldOre);
    private static readonly Ore Redstone = new(TerrainBlocks.RedstoneOre, TerrainBlocks.DeepslateRedstoneOre);
    private static readonly Ore Lapis = new(TerrainBlocks.LapisOre, TerrainBlocks.DeepslateLapisOre);
    private static readonly Ore Diamond = new(TerrainBlocks.DiamondOre, TerrainBlocks.DeepslateDiamondOre);
    private static readonly Ore Emerald = new(TerrainBlocks.EmeraldOre, TerrainBlocks.DeepslateEmeraldOre);

    public void Decorate(ChunkColumn column)
    {
        if (_frequency <= 0) return;
        ChunkPos pos = column.Position;
        // +/- one whole cell covers maximum 4-block vein reach across edges.
        for (int section = -1; section <= ChunkColumn.SectionCount; section++)
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int cellX = checked(pos.X + dx), cellZ = checked(pos.Z + dz);
            ulong h = FeatureHash.At(_seed, cellX, section, cellZ, 0x4f52452d5645494eUL);
            if (FeatureHash.Unit(h) >= Math.Min(1, 0.48 * _frequency)) continue;
            int wx = checked(cellX * 16 + FeatureHash.Index(FeatureHash.Mix(h ^ 11), 16));
            int wy = ChunkColumn.MinY + section * 16 + FeatureHash.Index(FeatureHash.Mix(h ^ 13), 16);
            int wz = checked(cellZ * 16 + FeatureHash.Index(FeatureHash.Mix(h ^ 17), 16));
            if (wy <= ChunkColumn.MinY + 1 || wy >= ChunkColumn.MaxY - 1) continue;
            Ore? ore = PickOre(wy, FeatureHash.Mix(h ^ 31));
            if (ore is null) continue;
            int length = 4 + FeatureHash.Index(FeatureHash.Mix(h ^ 41), 8);
            int axis = FeatureHash.Index(FeatureHash.Mix(h ^ 43), 3);
            for (int i = 0; i < length; i++)
            {
                ulong jitter = FeatureHash.Mix(h ^ unchecked((ulong)i * 0x9e3779b97f4a7c15UL));
                int offset = i - length / 2;
                int x = wx + (axis == 0 ? offset / 2 : 0) + FeatureHash.Index(jitter, 3) - 1;
                int y = wy + (axis == 1 ? offset / 2 : 0) + FeatureHash.Index(jitter >> 7, 3) - 1;
                int z = wz + (axis == 2 ? offset / 2 : 0) + FeatureHash.Index(jitter >> 13, 3) - 1;
                TryReplace(column, x, y, z, ore.Value);
                if ((jitter & 3UL) == 0)
                    TryReplace(column, x + (int)((jitter >> 21) & 1UL), y,
                        z + (int)((jitter >> 22) & 1UL), ore.Value);
            }
        }
    }

    private static Ore? PickOre(int y, ulong h)
    {
        // Not vanilla's exact ore distribution; heights and relative rarity are
        // deliberately chosen to give familiar underground progression.
        Span<Ore> ores = stackalloc Ore[8];
        Span<int> weights = stackalloc int[8];
        int n = 0;
        if (y is >= 0 and <= 240) { ores[n] = Coal; weights[n++] = 33; }
        if (y is >= -60 and <= 180) { ores[n] = Iron; weights[n++] = 32; }
        if (y is >= -16 and <= 112) { ores[n] = Copper; weights[n++] = 24; }
        if (y is >= -60 and <= 36) { ores[n] = Gold; weights[n++] = 9; }
        if (y is >= -60 and <= 12) { ores[n] = Redstone; weights[n++] = 11; }
        if (y is >= -52 and <= 72) { ores[n] = Lapis; weights[n++] = 8; }
        if (y is >= -60 and <= 24) { ores[n] = Diamond; weights[n++] = 5; }
        if (y is >= 64 and <= 232) { ores[n] = Emerald; weights[n++] = 3; }
        int total = 0;
        for (int i = 0; i < n; i++) total += weights[i];
        if (total == 0) return null;
        int roll = FeatureHash.Index(h, total);
        for (int i = 0; i < n; i++)
        {
            roll -= weights[i];
            if (roll < 0) return ores[i];
        }
        throw new InvalidOperationException("Unreachable ore selection.");
    }

    private static void TryReplace(ChunkColumn column, int wx, int y, int wz, Ore ore)
    {
        if (y <= ChunkColumn.MinY || y > ChunkColumn.MaxY) return;
        // Reserve the existing protected stone spawn pad/support in every seed.
        if (wx is >= -2 and <= 2 && wz is >= -2 and <= 2 &&
            (y == TerrainWorld.PlatformY || y == TerrainWorld.PlatformY - 1)) return;
        ChunkPos p = column.Position;
        if (FeatureHash.FloorDiv(wx, 16) != p.X || FeatureHash.FloorDiv(wz, 16) != p.Z)
            return;
        int x = wx - p.X * 16, z = wz - p.Z * 16;
        ushort old = column.GetBlock(x, y, z);
        if (old == TerrainBlocks.Stone) column.SetBlock(x, y, z, ore.Stone);
        else if (old == TerrainBlocks.Deepslate) column.SetBlock(x, y, z, ore.Deep);
    }
}
