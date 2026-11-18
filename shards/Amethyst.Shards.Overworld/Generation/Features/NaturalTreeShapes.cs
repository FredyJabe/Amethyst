namespace Amethyst.Generation.Features;

/// <summary>
/// Generator-v6 deterministic, species-specific silhouettes. Every block is
/// decided from the source tree's world-space anchor and clipped to a target
/// chunk; loading order cannot change branches at chunk boundaries.
/// </summary>
public static class NaturalTreeShapes
{
    public static void Place(ChunkColumn column, int x, int rootY, int z,
        int height, ushort log, ushort leaves, ulong shapeSeed)
    {
        if (rootY < ChunkColumn.MinY + 2 || rootY + height + 6 > ChunkColumn.MaxY)
            return;
        switch (log)
        {
            case TerrainBlocks.SpruceLog:
                Conifer(column, x, rootY, z, height, log, leaves, shapeSeed);
                break;
            case TerrainBlocks.AcaciaLog:
                Acacia(column, x, rootY, z, height, log, leaves, shapeSeed);
                break;
            case TerrainBlocks.DarkOakLog:
                DarkOak(column, x, rootY, z, height, log, leaves, shapeSeed);
                break;
            case TerrainBlocks.JungleLog:
                Jungle(column, x, rootY, z, height, log, leaves, shapeSeed);
                break;
            case TerrainBlocks.MangroveLog:
                Mangrove(column, x, rootY, z, height, log, leaves, shapeSeed);
                break;
            case TerrainBlocks.BirchLog:
                Broadleaf(column, x, rootY, z, height, log, leaves, shapeSeed, 2);
                break;
            default:
                Broadleaf(column, x, rootY, z, height, log, leaves, shapeSeed, 3);
                break;
        }
    }

    private static void Broadleaf(ChunkColumn col, int x, int y, int z, int h,
        ushort log, ushort leaves, ulong seed, int radius)
    {
        int tip = y + h - 1;
        int offsetX = ((seed & 7UL) == 0) ? 1 : 0;
        int offsetZ = ((seed & 15UL) == 1) ? -1 : 0;
        for (int i = 0; i < h; i++)
        {
            int shift = i > h * 2 / 3 ? 1 : 0;
            Put(col, x + offsetX * shift, y + i, z + offsetZ * shift, log, true);
        }
        int cx = x + offsetX, cz = z + offsetZ;
        Blob(col, cx, tip, cz, radius, 2.1, radius, leaves, seed);
        int branches = log == TerrainBlocks.BirchLog ? 1 : 2 + (int)(seed % 2);
        for (int arm = 0; arm < branches; arm++)
        {
            ulong r = FeatureHash.Mix(seed ^ (ulong)(arm + 11) * 0x9e3779b97f4a7c15UL);
            int dx = (int)(r & 3) - 1;
            int dz = (int)((r >> 2) & 3) - 1;
            if (dx == 0 && dz == 0) dx = 1;
            int bx = x + dx * 2, bz = z + dz * 2, by = tip - 2 + (int)((r >> 8) & 1);
            Put(col, x + dx, by, z + dz, log, true);
            Put(col, bx, by + 1, bz, log, true);
            Blob(col, bx, by + 1, bz, Math.Max(1, radius - 1), 1.5,
                Math.Max(1, radius - 1), leaves, r);
        }
    }

    private static void Conifer(ChunkColumn col, int x, int y, int z, int h,
        ushort log, ushort leaves, ulong seed)
    {
        int tip = y + h - 1;
        for (int i = 0; i < h; i++) Put(col, x, y + i, z, log, true);
        int baseY = y + 2 + (int)(seed % 2);
        int maxRadius = h >= 11 ? 3 : 2;
        for (int level = baseY; level <= tip + 1; level++)
        {
            int fromTop = tip - level;
            int radius = level >= tip ? 1 :
                Math.Clamp((fromTop + 3) / 3, 1, maxRadius);
            if (fromTop % 3 == 1) radius = Math.Max(1, radius - 1);
            if (level < tip && (FeatureHash.Mix(seed ^ unchecked((ulong)level)) & 7) == 0)
                radius = Math.Max(1, radius - 1);
            LeafDisk(col, x, level, z, radius, leaves, seed);
        }
        Put(col, x, tip + 2, z, leaves, false);
    }

    private static void Acacia(ChunkColumn col, int x, int y, int z, int h,
        ushort log, ushort leaves, ulong seed)
    {
        int dx = ((seed & 1) == 0) ? 1 : -1;
        int dz = ((seed & 2) == 0) ? 1 : -1;
        int tip = y + h;
        int previousLean = 0;
        for (int i = 0; i <= h; i++)
        {
            int lean = i > h / 2 ? (i - h / 2) / 2 + 1 : 0;
            // Bridge horizontal bends so the leaning trunk stays connected.
            if (lean != previousLean)
            {
                Put(col, x + dx * previousLean, y + i, z + dz * previousLean, log, true);
                Put(col, x + dx * lean, y + i, z + dz * previousLean, log, true);
            }
            Put(col, x + dx * lean, y + i, z + dz * lean, log, true);
            previousLean = lean;
        }
        int topX = x + dx * ((h - h / 2) / 2 + 1);
        int topZ = z + dz * ((h - h / 2) / 2 + 1);
        Blob(col, topX, tip + 1, topZ, 3, 0.9, 3, leaves, seed);
        Blob(col, topX + dx, tip + 1, topZ + dz, 2, 0.8, 2, leaves, seed ^ 10UL);
        int branchY = y + h - 2;
        int bx = x - dx * 2, bz = z + dz * 2;
        Put(col, x - dx, branchY, z + dz, log, true);
        Put(col, bx, branchY + 1, bz, log, true);
        Blob(col, bx, branchY + 2, bz, 2, 0.8, 2, leaves, seed ^ 41UL);
    }

    private static void DarkOak(ChunkColumn col, int x, int y, int z, int h,
        ushort log, ushort leaves, ulong seed)
    {
        int tip = y + h;
        for (int i = 0; i < h; i++)
        for (int dx = 0; dx <= 1; dx++)
        for (int dz = 0; dz <= 1; dz++)
            Put(col, x + dx, y + i, z + dz, log, true);
        Blob(col, x + 1, tip, z, 4, 2.0, 3, leaves, seed);
        Blob(col, x - 1, tip - 1, z + 2, 3, 2.0, 3, leaves, seed ^ 5UL);
        Blob(col, x + 2, tip, z + 2, 3, 2.0, 3, leaves, seed ^ 9UL);
        for (int a = 0; a < 3; a++)
        {
            int dx = a - 1, dz = a == 1 ? -1 : 1;
            Put(col, x + dx * 2, tip - 1, z + dz * 2, log, true);
        }
    }

    private static void Jungle(ChunkColumn col, int x, int y, int z, int h,
        ushort log, ushort leaves, ulong seed)
    {
        int tip = y + h - 1;
        for (int i = 0; i < h; i++) Put(col, x, y + i, z, log, true);
        Blob(col, x, tip + 1, z, 4, 2.0, 4, leaves, seed);
        for (int a = 0; a < 3; a++)
        {
            ulong r = FeatureHash.Mix(seed ^ (ulong)(a + 1) * 1099511628211UL);
            int dx = ((r & 1UL) == 0) ? 1 : -1;
            int dz = ((r & 2UL) == 0) ? 1 : -1;
            int by = tip - 4 - a * 2;
            if (by < y + 2) continue;
            int bx = x + dx * (2 + a % 2), bz = z + dz * 2;
            Put(col, x + dx, by, z + dz, log, true);
            Put(col, bx, by + 1, bz, log, true);
            Blob(col, bx, by + 2, bz, 2, 1.5, 2, leaves, r);
        }
    }

    private static void Mangrove(ChunkColumn col, int x, int y, int z, int h,
        ushort log, ushort leaves, ulong seed)
    {
        int tip = y + h;
        for (int i = 0; i < h; i++) Put(col, x, y + i, z, log, true);
        for (int a = 0; a < 4; a++)
        {
            int dx = (a == 0 ? 1 : a == 1 ? -1 : 0);
            int dz = (a == 2 ? 1 : a == 3 ? -1 : 0);
            Put(col, x + dx, y + 2, z + dz, log, true);
            Put(col, x + 2 * dx, y + 1, z + 2 * dz, log, true);
        }
        Blob(col, x, tip, z, 3, 2.0, 3, leaves, seed);
        Blob(col, x + 2, tip - 1, z - 1, 2, 2, 2, leaves, seed ^ 21UL);
    }

    private static void LeafDisk(ChunkColumn col, int x, int y, int z,
        int r, ushort leaves, ulong seed)
    {
        for (int dz = -r; dz <= r; dz++)
        for (int dx = -r; dx <= r; dx++)
        {
            int squared = dx * dx + dz * dz;
            if (squared > r * r + 1) continue;
            ulong h = FeatureHash.At(unchecked((long)seed), x + dx, y, z + dz, 0x4c454146UL);
            if (squared >= r * r && (h & 7UL) < 2) continue;
            Put(col, x + dx, y, z + dz, leaves, false);
        }
    }

    private static void Blob(ChunkColumn col, int x, int y, int z,
        int rx, double ry, int rz, ushort leaves, ulong seed)
    {
        int maxY = (int)Math.Ceiling(ry);
        for (int dz = -rz; dz <= rz; dz++)
        for (int dx = -rx; dx <= rx; dx++)
        for (int dy = -maxY; dy <= maxY; dy++)
        {
            double ellipsoid = dx * dx / (double)(rx * rx) +
                dz * dz / (double)(rz * rz) + dy * dy / (ry * ry);
            ulong h = FeatureHash.At(unchecked((long)seed), x + dx, y + dy,
                z + dz, 0x4e41545552414cUL);
            double edge = 0.8 + FeatureHash.Unit(h) * 0.32;
            if (ellipsoid > edge) continue;
            Put(col, x + dx, y + dy, z + dz, leaves, false);
        }
    }

    private static void Put(ChunkColumn target, int wx, int y, int wz, ushort block, bool wood)
    {
        if (y < ChunkColumn.MinY || y > ChunkColumn.MaxY) return;
        var pos = target.Position;
        if (FeatureHash.FloorDiv(wx, 16) != pos.X ||
            FeatureHash.FloorDiv(wz, 16) != pos.Z) return;
        int lx = wx - pos.X * 16, lz = wz - pos.Z * 16;
        ushort old = target.GetBlock(lx, y, lz);
        bool plant = old is TerrainBlocks.Air or TerrainBlocks.ShortGrass or
            TerrainBlocks.Fern or TerrainBlocks.Dandelion or TerrainBlocks.Poppy or
            TerrainBlocks.Cornflower;
        bool leaf = old is TerrainBlocks.OakLeaves or TerrainBlocks.BirchLeaves or
            TerrainBlocks.SpruceLeaves or TerrainBlocks.JungleLeaves or
            TerrainBlocks.AcaciaLeaves or TerrainBlocks.DarkOakLeaves or
            TerrainBlocks.MangroveLeaves;
        if (plant || (wood && leaf)) target.SetBlock(lx, y, lz, block);
    }
}
