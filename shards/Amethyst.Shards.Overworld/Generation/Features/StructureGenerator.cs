using Amethyst.Generation.Terrain;

namespace Amethyst.Generation.Features;

/// <summary>
/// First small world-space landmarks: rocky boulders and occasional ruined
/// stone arches. Region anchors are fixed on a 32-block lattice; each chunk
/// builds only its slice, so generation order never controls a structure.
/// Full villages and dungeons are intentionally out of scope.
/// </summary>
public sealed class StructureGenerator
{
    private readonly long _seed;
    private readonly double _frequency;
    private readonly TerrainGenerator _terrain;
    private readonly DensitySampler _density;
    private readonly int _seaLevel;
    private readonly bool _natural;

    public StructureGenerator(TerrainGenerator terrain, DensitySampler density,
        ServerOptions settings)
    {
        _seed = settings.Seed;
        _frequency = settings.StructureFrequency;
        _terrain = terrain;
        _density = density;
        _seaLevel = settings.SeaLevel;
        _natural = settings.GeneratorVersion >= 6;
    }

    public void Decorate(ChunkColumn column)
    {
        if (_frequency <= 0) return;
        if (_natural) { DecorateNatural(column); return; }
        var pos = column.Position;
        int lowX = FeatureHash.FloorDiv(checked(pos.X * 16 - 4), 32);
        int highX = FeatureHash.FloorDiv(checked(pos.X * 16 + 19), 32);
        int lowZ = FeatureHash.FloorDiv(checked(pos.Z * 16 - 4), 32);
        int highZ = FeatureHash.FloorDiv(checked(pos.Z * 16 + 19), 32);
        for (int cz = lowZ; cz <= highZ; cz++)
        for (int cx = lowX; cx <= highX; cx++)
        {
            ulong h = FeatureHash.At(_seed, cx, 0, cz, 0x5255494e2d534545UL);
            if (FeatureHash.Unit(h) >= Math.Min(1, 0.24 * _frequency)) continue;
            int wx = checked(cx * 32 + 7 + FeatureHash.Index(FeatureHash.Mix(h ^ 9), 18));
            int wz = checked(cz * 32 + 7 + FeatureHash.Index(FeatureHash.Mix(h ^ 11), 18));
            if ((long)wx * wx + (long)wz * wz < 55L * 55) continue;
            TerrainSample terrain = _terrain.Sample(wx, wz);
            if (terrain.IsRiver || terrain.Surface is SurfaceKind.Riverbed or SurfaceKind.Snow)
                continue;
            int top = _density.TopSolidAt(wx, wz);
            if (top <= _seaLevel + 2 || top >= ChunkColumn.MaxY - 9) continue;
            string biome = VanillaRegistries.EntryAt("minecraft:worldgen/biome", terrain.BiomeId);
            if (biome.Contains("ocean", StringComparison.Ordinal) ||
                biome.Contains("beach", StringComparison.Ordinal)) continue;
            bool sandy = terrain.Surface == SurfaceKind.Sand;
            ushort stone = sandy ? TerrainBlocks.Sandstone : TerrainBlocks.Cobblestone;
            ushort trim = sandy ? TerrainBlocks.ChiseledSandstone : TerrainBlocks.MossyCobblestone;
            if ((h & 7UL) == 0)
            {
                // Tiny weathered arch: two 3-block posts and a damaged lintel.
                for (int y = 1; y <= 3; y++)
                {
                    Place(column, wx - 2, top + y, wz, y == 3 ? trim : stone);
                    Place(column, wx + 2, top + y, wz, y == 2 ? trim : stone);
                }
                for (int dx = -2; dx <= 2; dx++)
                    if (dx != 0 || (h & 16UL) != 0)
                        Place(column, wx + dx, top + 4, wz, dx == 0 ? trim : stone);
            }
            else
            {
                // Small rounded boulder, with a deterministic weathered face.
                for (int dy = 0; dy <= 2; dy++)
                for (int dz = -2; dz <= 2; dz++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    if (dx * dx + dz * dz + dy * dy * 1.7 > 5) continue;
                    ulong bit = FeatureHash.At(unchecked((long)h), wx + dx,
                        top + dy, wz + dz, 0x424f554c444552UL);
                    if (dy == 2 && (bit & 3) != 0) continue;
                    Place(column, wx + dx, top + 1 + dy, wz + dz,
                        (bit & 7) == 0 ? trim : stone);
                }
            }
        }
    }

    private void DecorateNatural(ChunkColumn column)
    {
        // One biome-aware anchor per 32x32 region. All candidate world-space
        // coordinates and details are independent of target chunk load order.
        ChunkPos pos = column.Position;
        int loX = FeatureHash.FloorDiv(checked(pos.X * 16 - 7), 32);
        int hiX = FeatureHash.FloorDiv(checked(pos.X * 16 + 22), 32);
        int loZ = FeatureHash.FloorDiv(checked(pos.Z * 16 - 7), 32);
        int hiZ = FeatureHash.FloorDiv(checked(pos.Z * 16 + 22), 32);
        for (int cellZ = loZ; cellZ <= hiZ; cellZ++)
        for (int cellX = loX; cellX <= hiX; cellX++)
        {
            ulong h = FeatureHash.At(_seed, cellX, 0, cellZ, 0x4e41545552454c4dUL);
            if (FeatureHash.Unit(h) >= Math.Min(1, 0.34 * _frequency)) continue;
            int wx = checked(cellX * 32 + 7 + FeatureHash.Index(FeatureHash.Mix(h ^ 9), 18));
            int wz = checked(cellZ * 32 + 7 + FeatureHash.Index(FeatureHash.Mix(h ^ 11), 18));
            if ((long)wx * wx + (long)wz * wz < 52L * 52) continue;
            var terrain = _terrain.Sample(wx, wz);
            if (terrain.IsRiver || terrain.Surface == SurfaceKind.Riverbed) continue;
            int top = _density.TopSolidAt(wx, wz);
            if (top <= _seaLevel + 2 || top >= ChunkColumn.MaxY - 12) continue;
            string biome = VanillaRegistries.EntryAt("minecraft:worldgen/biome", terrain.BiomeId);
            if (biome.Contains("ocean", StringComparison.Ordinal) ||
                biome.Contains("beach", StringComparison.Ordinal)) continue;
            bool sand = terrain.Surface == SurfaceKind.Sand;
            int kind = FeatureHash.Index(FeatureHash.Mix(h ^ 77), 100);
            if (kind >= 86)
            {
                // Extremely occasional weathered ruin: retain Phase 5's
                // structure idea, but make each arch asymmetrical.
                ushort stone = sand ? TerrainBlocks.Sandstone : TerrainBlocks.Cobblestone;
                for (int dy = 1; dy <= 3; dy++)
                {
                    Place(column, wx - 2, top + dy, wz, stone);
                    if (dy != 3 || (h & 1UL) == 0)
                        Place(column, wx + 2, top + dy, wz, stone);
                }
                for (int dx = -2; dx <= 2; dx++)
                    if (dx != 1 || (h & 4UL) == 0)
                        Place(column, wx + dx, top + 4, wz,
                            dx == 0 && !sand ? TerrainBlocks.MossyCobblestone : stone);
            }
            else if (kind >= 57 && !sand &&
                (biome.Contains("forest", StringComparison.Ordinal) ||
                 biome.Contains("taiga", StringComparison.Ordinal) ||
                 biome.Contains("jungle", StringComparison.Ordinal)))
            {
                // Fallen trunks lie on the natural surface. Axis-specific 26.3
                // block states avoid a vertical-log texture on horizontal wood.
                bool eastWest = ((h >> 15) & 1) == 0;
                ushort log = eastWest ? TerrainBlocks.OakLogAxisX : TerrainBlocks.OakLogAxisZ;
                int length = 2 + FeatureHash.Index(h >> 17, 4);
                for (int i = -1; i < length; i++)
                {
                    int tx = wx + (eastWest ? i : 0);
                    int tz = wz + (eastWest ? 0 : i);
                    if (Math.Abs(_density.TopSolidAt(tx, tz) - top) > 1) continue;
                    Place(column, tx, top + 1, tz, log);
                    if (i == 0 && (h & 3) == 0)
                        Place(column, tx, top + 2, tz, TerrainBlocks.Moss);
                }
            }
            else
            {
                // Boulder clusters have irregular ellipsoid silhouettes;
                // grasslands often contain small single-block fragments.
                int radius = kind < 25 ? 1 : 2 + (int)((h >> 7) & 1);
                double rx = radius + 0.6, rz = radius + (((h >> 20) & 1) != 0 ? 0.9 : 0);
                for (int dy = 0; dy <= radius; dy++)
                for (int dz = -radius - 1; dz <= radius + 1; dz++)
                for (int dx = -radius - 1; dx <= radius + 1; dx++)
                {
                    ulong bit = FeatureHash.At(unchecked((long)h), wx + dx,
                        top + dy, wz + dz, 0x524f434b434c5553UL);
                    double shape = dx * dx / (rx * rx) + dz * dz / (rz * rz)
                        + dy * dy / ((radius + 0.3) * (radius + 0.3));
                    if (shape > 0.76 + FeatureHash.Unit(bit) * 0.44) continue;
                    ushort state = sand ? TerrainBlocks.Sandstone :
                        (bit & 15) == 0 ? TerrainBlocks.MossyCobblestone :
                        (bit & 7) == 0 ? TerrainBlocks.Andesite : TerrainBlocks.Cobblestone;
                    Place(column, wx + dx, top + dy + 1, wz + dz, state);
                }
            }
        }
    }

    private static void Place(ChunkColumn column, int wx, int y, int wz, ushort state)
    {
        if (y < ChunkColumn.MinY || y > ChunkColumn.MaxY) return;
        ChunkPos pos = column.Position;
        if (FeatureHash.FloorDiv(wx, 16) != pos.X ||
            FeatureHash.FloorDiv(wz, 16) != pos.Z) return;
        int x = wx - pos.X * 16, z = wz - pos.Z * 16;
        ushort old = column.GetBlock(x, y, z);
        if (old is TerrainBlocks.Air or TerrainBlocks.ShortGrass or TerrainBlocks.Fern)
            column.SetBlock(x, y, z, state);
    }
}
