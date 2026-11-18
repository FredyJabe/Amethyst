using System.Buffers;

namespace Amethyst;

/// <summary>
/// Static light baking for immutable generated columns. A 15-block halo samples
/// adjacent chunks so sun and lava light may cross a chunk boundary. The
/// vanilla client applies day/night attenuation to these fixed sky levels.
/// </summary>
public static class ChunkLightEngine
{
    private const int Halo = 15;
    private const int Width = 16 + Halo * 2;
    private const int Plane = Width * Width;
    private const int Height = ChunkColumn.SectionCount * 16;
    private const int LightSections = ChunkColumn.SectionCount + 2;
    private static readonly byte[] FullSky = Enumerable.Repeat((byte)255, 2048).ToArray();

    public sealed class LightData
    {
        private readonly bool[] _skyMask = new bool[LightSections];
        private readonly bool[] _blockMask = new bool[LightSections];
        private readonly bool[] _emptySkyMask = new bool[LightSections];
        private readonly bool[] _emptyBlockMask = new bool[LightSections];
        private readonly byte[][] _sky = new byte[LightSections][];
        private readonly byte[][] _block = new byte[LightSections][];

        internal LightData() { }

        public void Write(PacketWriter w)
        {
            WriteMask(w, _skyMask);
            WriteMask(w, _blockMask);
            WriteMask(w, _emptySkyMask);
            WriteMask(w, _emptyBlockMask);
            w.WriteVarInt(_skyMask.Count(b => b));
            for (int i = 0; i < LightSections; i++)
                if (_skyMask[i]) w.WriteByteArray(_sky[i]);
            w.WriteVarInt(_blockMask.Count(b => b));
            for (int i = 0; i < LightSections; i++)
                if (_blockMask[i]) w.WriteByteArray(_block[i]);
        }

        public byte SkyAt(int x, int y, int z) => ReadLevel(_skyMask, _sky, x, y, z);
        public byte BlockAt(int x, int y, int z) => ReadLevel(_blockMask, _block, x, y, z);

        private static byte ReadLevel(bool[] mask, byte[][] arrays, int x, int y, int z)
        {
            if (x is < 0 or > 15 || z is < 0 or > 15 ||
                y < ChunkColumn.MinY || y > ChunkColumn.MaxY)
                throw new ArgumentOutOfRangeException(nameof(y));
            int layer = (y - ChunkColumn.MinY) / 16 + 1;
            if (!mask[layer]) return 0;
            int local = ((y - ChunkColumn.MinY) % 16 << 8) | (z << 4) | x;
            byte b = arrays[layer][local >> 1];
            return (byte)((local & 1) == 0 ? b & 15 : b >> 4);
        }

        internal void SetSky(int index, byte[]? array, bool full = false)
        {
            if (array is null) _emptySkyMask[index] = true;
            else { _skyMask[index] = true; _sky[index] = full ? FullSky : array; }
        }
        internal void SetBlock(int index, byte[]? array)
        {
            if (array is null) _emptyBlockMask[index] = true;
            else { _blockMask[index] = true; _block[index] = array; }
        }
    }

    /// <summary>One-time 4-bit input cache per immutable chunk: opacity,
    /// direct vertical skylight and emission. Neighboring light calculations
    /// reuse these samples rather than re-reading/reclassifying blocks.</summary>
    public sealed class LightSource
    {
        private const int SourcePlane = 16 * 16;
        private readonly byte[] _opacity = new byte[Height * SourcePlane / 2];
        private readonly byte[] _directSky = new byte[Height * SourcePlane / 2];
        private readonly byte[] _emission = new byte[Height * SourcePlane / 2];

        private static int Index(int x, int y, int z) => y * SourcePlane + z * 16 + x;
        private static byte Get(byte[] data, int index) =>
            (byte)((index & 1) == 0 ? data[index >> 1] & 15 : data[index >> 1] >> 4);
        private static void Set(byte[] data, int index, byte value) =>
            data[index >> 1] |= (byte)(value << ((index & 1) * 4));

        public byte OpacityAt(int x, int y, int z) => Get(_opacity, Index(x, y, z));
        public byte SkyAt(int x, int y, int z) => Get(_directSky, Index(x, y, z));
        public byte EmissionAt(int x, int y, int z) => Get(_emission, Index(x, y, z));

        internal static LightSource FromColumn(ChunkColumn column)
        {
            var source = new LightSource();
            for (int z = 0; z < 16; z++)
            for (int x = 0; x < 16; x++)
            {
                byte sky = 15;
                for (int section = ChunkColumn.SectionCount - 1; section >= 0; section--)
                {
                    WorldSection cube = column.Sections[section];
                    if (cube.FullyOpaqueAndUnlit)
                    {
                        for (int ly = 15; ly >= 0; ly--)
                        {
                            int idx = Index(x, section * 16 + ly, z);
                            Set(source._opacity, idx, 15);
                        }
                        sky = 0;
                        continue;
                    }
                    for (int ly = 15; ly >= 0; ly--)
                    {
                        int idx = Index(x, section * 16 + ly, z);
                        ushort block = cube.GetBlock(x, ly, z);
                        byte opacity = ChunkLightEngine.Opacity(block);
                        sky = opacity == 15 ? (byte)0 : (byte)Math.Max(0, sky - opacity);
                        Set(source._opacity, idx, opacity);
                        Set(source._directSky, idx, sky);
                        if (block == TerrainBlocks.Lava) Set(source._emission, idx, 15);
                    }
                }
            }
            return source;
        }
    }

    public static LightSource PrepareSource(ChunkColumn column) => LightSource.FromColumn(column);

    public static LightData Calculate(TerrainWorld world, ChunkPos center) =>
        CalculateSources((dx, dz) => world.GetLightingSource(new ChunkPos(center.X + dx, center.Z + dz)));

    /// <summary>For exact synthetic lighting tests, independently prepares
    /// input sources from the supplied 3x3 chunk-column lookup.</summary>
    public static LightData Calculate(Func<int, int, ChunkColumn> lookup) =>
        CalculateSources((dx, dz) => PrepareSource(lookup(dx, dz)));

    private static LightData CalculateSources(Func<int, int, LightSource> lookup)
    {
        var sources = new LightSource[3, 3];
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
            sources[dx + 1, dz + 1] = lookup(dx, dz);

        int total = Height * Plane;
        // Reuse the three ~0.8 MiB scratch volumes instead of allocating
        // 2.3 MiB of short-lived arrays every time a light cache entry misses.
        byte[] opacity = ArrayPool<byte>.Shared.Rent(total);
        byte[] sky = ArrayPool<byte>.Shared.Rent(total);
        byte[] emitted = ArrayPool<byte>.Shared.Rent(total);
        Array.Clear(emitted, 0, total);
        try
        {
        var glow = new Queue<int>();

        // Direct vertical sky exposure. Leaves dim the incoming light one
        // level per layer; solid blocks completely block it. For the whole
        // region, include all 384 layers to allow cave passages to receive
        // lateral light (not just surface voxels).
        for (int z = 0; z < Width; z++)
        for (int x = 0; x < Width; x++)
        {
            int worldLocalX = x - Halo, worldLocalZ = z - Halo;
            int chunkX = worldLocalX < 0 ? 0 : worldLocalX >= 16 ? 2 : 1;
            int chunkZ = worldLocalZ < 0 ? 0 : worldLocalZ >= 16 ? 2 : 1;
            int bx = worldLocalX < 0 ? worldLocalX + 16 : worldLocalX >= 16 ? worldLocalX - 16 : worldLocalX;
            int bz = worldLocalZ < 0 ? worldLocalZ + 16 : worldLocalZ >= 16 ? worldLocalZ - 16 : worldLocalZ;
            LightSource source = sources[chunkX, chunkZ];
            for (int y = Height - 1; y >= 0; y--)
            {
                int index = y * Plane + z * Width + x;
                opacity[index] = source.OpacityAt(bx, y, bz);
                sky[index] = source.SkyAt(bx, y, bz);
                byte emission = source.EmissionAt(bx, y, bz);
                if (emission != 0)
                {
                    emitted[index] = emission;
                    glow.Enqueue(index);
                }
            }
        }

        // Put only *frontier* cells into the queue: sunlit open sky is already
        // 15 everywhere. Bordering shaded cells are the actual sources for
        // sideways light, overhangs and cave openings.
        var daylight = new Queue<int>();
        for (int y = 0; y < Height; y++)
        for (int z = 0; z < Width; z++)
        for (int x = 0; x < Width; x++)
        {
            int idx = y * Plane + z * Width + x;
            byte value = sky[idx];
            if (value < 2 || opacity[idx] >= 15) continue;
            if ((x > 0 && CanImprove(idx - 1)) ||
                (x + 1 < Width && CanImprove(idx + 1)) ||
                (z > 0 && CanImprove(idx - Width)) ||
                (z + 1 < Width && CanImprove(idx + Width)) ||
                (y > 0 && CanImprove(idx - Plane)) ||
                (y + 1 < Height && CanImprove(idx + Plane)))
                daylight.Enqueue(idx);

            bool CanImprove(int other) => opacity[other] < 15 &&
                value - Math.Max(1, (int)opacity[other]) > sky[other];
        }

        Flood(sky, daylight, opacity);
        Flood(emitted, glow, opacity);

        var result = new LightData();
        // One light layer below the world is always dark; one above the world
        // receives unobstructed sky light.
        result.SetSky(0, null);
        result.SetBlock(0, null);
        for (int layer = 0; layer < ChunkColumn.SectionCount; layer++)
        {
            byte[] skyArray = new byte[2048], blockArray = new byte[2048];
            bool anySky = false, fullSky = true, anyBlock = false;
            for (int localY = 0; localY < 16; localY++)
            for (int localZ = 0; localZ < 16; localZ++)
            for (int localX = 0; localX < 16; localX++)
            {
                int i = ((layer * 16 + localY) * Plane) +
                    (localZ + Halo) * Width + localX + Halo;
                byte s = sky[i], b = emitted[i];
                anySky |= s != 0;
                fullSky &= s == 15;
                anyBlock |= b != 0;
                int local = (localY << 8) | (localZ << 4) | localX;
                skyArray[local >> 1] |= (byte)(s << ((local & 1) * 4));
                blockArray[local >> 1] |= (byte)(b << ((local & 1) * 4));
            }
            result.SetSky(layer + 1, !anySky ? null : skyArray, fullSky);
            result.SetBlock(layer + 1, anyBlock ? blockArray : null);
        }
        result.SetSky(LightSections - 1, FullSky, full: true);
        result.SetBlock(LightSections - 1, null);
        return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(opacity);
            ArrayPool<byte>.Shared.Return(sky);
            ArrayPool<byte>.Shared.Return(emitted);
        }
    }

    private static void Flood(byte[] levels, Queue<int> queue, byte[] opacity)
    {
        while (queue.TryDequeue(out int src))
        {
            byte value = levels[src];
            if (value <= 1) continue;
            int y = src / Plane;
            int remainder = src - y * Plane;
            int z = remainder / Width, x = remainder % Width;
            if (x > 0) Try(src - 1, value);
            if (x + 1 < Width) Try(src + 1, value);
            if (z > 0) Try(src - Width, value);
            if (z + 1 < Width) Try(src + Width, value);
            if (y > 0) Try(src - Plane, value);
            if (y + 1 < Height) Try(src + Plane, value);
        }

        void Try(int target, byte source)
        {
            byte loss = opacity[target];
            if (loss >= 15) return;
            byte next = (byte)Math.Max(0, (int)source - Math.Max(1, (int)loss));
            if (next <= levels[target]) return;
            levels[target] = next;
            if (next > 1) queue.Enqueue(target);
        }
    }

    internal static bool IsOpaqueAndUnlit(ushort state) =>
        state != TerrainBlocks.Lava && Opacity(state) == 15;

    private static byte Opacity(ushort state)
    {
        if (state is TerrainBlocks.Air or TerrainBlocks.ShortGrass or TerrainBlocks.Fern or
            TerrainBlocks.DeadBush or TerrainBlocks.Dandelion or TerrainBlocks.Poppy or
            TerrainBlocks.Cornflower or TerrainBlocks.BrownMushroom or TerrainBlocks.RedMushroom or
            TerrainBlocks.ShortDryGrass or TerrainBlocks.AzureBluet or TerrainBlocks.Allium or
            TerrainBlocks.LilyOfTheValley or TerrainBlocks.TallGrassUpper or TerrainBlocks.TallGrassLower)
            return 0;
        if (state == TerrainBlocks.Water) return 2;
        if (state is TerrainBlocks.OakLeaves or TerrainBlocks.BirchLeaves or
            TerrainBlocks.SpruceLeaves or TerrainBlocks.JungleLeaves or
            TerrainBlocks.AcaciaLeaves or TerrainBlocks.DarkOakLeaves or
            TerrainBlocks.MangroveLeaves)
            return 1;
        return 15;
    }

    private static void WriteMask(PacketWriter writer, bool[] bits)
    {
        byte[] bytes = new byte[(bits.Length + 7) / 8];
        for (int i = 0; i < bits.Length; i++)
            if (bits[i]) bytes[i >> 3] |= (byte)(1 << (i & 7));
        int n = bytes.Length;
        while (n > 0 && bytes[n - 1] == 0) n--;
        writer.WriteByteArray(bytes.AsSpan(0, n));
    }
}
