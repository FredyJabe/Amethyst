using Amethyst.Generation.Noise;

namespace Amethyst.Generation.Terrain;

/// <summary>
/// Vanilla-inspired ORIGINAL density function, not Mojang's noise graph.
/// Positive => solid; nonpositive => air. Density is evaluated only at 4x8x4
/// cell corners, then interpolated for every block in a chunk column.
/// </summary>
public sealed class DensitySampler
{
    public const int CellWidth = 4;
    public const int CellHeight = 8;
    private readonly TerrainGenerator _terrain;
    private readonly double _terrainScale;
    private readonly FractalNoise3D _surface, _cavern, _tunnelA, _tunnelB;
    private readonly BoundedLazyCache<(int X, int Z), int> _topSolidCache;

    public DensitySampler(TerrainGenerator terrain, ServerOptions options)
    {
        _terrain = terrain;
        _topSolidCache = new(options.CachedFeatureHeights);
        _terrainScale = options.TerrainScale;
        _surface = new FractalNoise3D(options.Seed, 0x3333444455556666UL, 3);
        _cavern = new FractalNoise3D(options.Seed, 0x43415645524e3333UL, 3);
        _tunnelA = new FractalNoise3D(options.Seed, 0x54554e4e454c4141UL, 3);
        _tunnelB = new FractalNoise3D(options.Seed, 0x54554e4e454c4242UL, 3);
    }

    private static double Smooth(double lo, double hi, double v)
    {
        double t = Math.Clamp((v - lo) / (hi - lo), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Reference density at an absolute world-space point (before interpolation).</summary>
    public double Sample(int worldX, int y, int worldZ) =>
        SampleWithHeight(worldX, y, worldZ, _terrain.Sample(worldX, worldZ).GroundY);

    // Height/climate is constant over Y, so the grid computes it once per X/Z
    // corner instead of 49 times for each vertical density cell.
    private double SampleWithHeight(int worldX, int y, int worldZ, double height)
    {
        double distance = Math.Sqrt((double)worldX * worldX + (double)worldZ * worldZ);
        // Flatten the immediate spawn region, then gradually introduce 3D shapes.
        double wild = Smooth(12, 48, distance);
        double mountain = Smooth(90, 152, height);
        double x = worldX * _terrainScale, z = worldZ * _terrainScale;
        // Undulation can create steep faces and overhangs where the horizontal
        // elevation shaper changes sharply; this is not a height-only fill.
        double surface = _surface.Sample(x, y, z, 74);
        double density = height - y + surface * (7 + 28 * mountain) * wild;

        // 3D caverns and two intersecting worm-like tunnel fields. Caves are
        // faded away at the surface, near bedrock and around the spawn pad.
        // A negative cavity density replaces the background solid density.
        double depth = height - y;
        double caveFade = Smooth(14, 32, depth)
                        * Smooth(ChunkColumn.MinY + 5, ChunkColumn.MinY + 20, y)
                        * Smooth(24, 62, distance);
        if (caveFade > 0)
        {
            double cheese = _cavern.Sample(x, y, z, 92);
            double a = Math.Abs(_tunnelA.Sample(x, y, z, 58));
            double b = Math.Abs(_tunnelB.Sample(x, y, z, 49));
            double cavernDensity = (0.24 - cheese) * 85;
            double tunnelDensity = Math.Max(a - 0.125, b - 0.165) * 115;
            double voidDensity = Math.Min(cavernDensity, tunnelDensity);
            // Smooth fade also prevents sudden cave cutoffs at depth thresholds.
            density = Math.Min(density, density + Math.Min(0, voidDensity - density) * caveFade);
        }
        return density;
    }

    /// <summary>
    /// Exact top-solid query against the SAME 4x8x4 interpolated density cells
    /// used during chunk filling. This lets a tree rooted in an adjacent chunk
    /// place its canopy at the correct elevation WITHOUT generating that chunk.
    /// </summary>
    public int TopSolidAt(int x, int z) =>
        _topSolidCache.GetOrAdd((x, z), key => ComputeTopSolid(key.X, key.Z), out _);

    private int ComputeTopSolid(int x, int z)
    {
        int gx = FeatureFloor(x, CellWidth) * CellWidth;
        int gz = FeatureFloor(z, CellWidth) * CellWidth;
        double tx = (x - gx) / (double)CellWidth;
        double tz = (z - gz) / (double)CellWidth;
        double h00 = _terrain.Sample(gx, gz).GroundY;
        double h10 = _terrain.Sample(gx + CellWidth, gz).GroundY;
        double h01 = _terrain.Sample(gx, gz + CellWidth).GroundY;
        double h11 = _terrain.Sample(gx + CellWidth, gz + CellWidth).GroundY;
        int approx = _terrain.Sample(x, z).GroundY;
        int start = Math.Min(ChunkColumn.MaxY, approx + 48);
        int firstCell = Math.Min((start - ChunkColumn.MinY) / CellHeight,
            (ChunkColumn.MaxY - ChunkColumn.MinY) / CellHeight);
        for (int cell = firstCell; cell >= 0; cell--)
        {
            int y0 = ChunkColumn.MinY + cell * CellHeight;
            double lo = SampleHorizontal(y0);
            double hi = SampleHorizontal(y0 + CellHeight);
            for (int y = Math.Min(start, y0 + CellHeight - 1); y >= y0; y--)
                if (lo + (hi - lo) * ((y - y0) / (double)CellHeight) > 0)
                    return y;
        }
        return ChunkColumn.MinY;

        double SampleHorizontal(int y)
        {
            double a = SampleWithHeight(gx, y, gz, h00);
            double b = SampleWithHeight(gx + CellWidth, y, gz, h10);
            double c = SampleWithHeight(gx, y, gz + CellWidth, h01);
            double d = SampleWithHeight(gx + CellWidth, y, gz + CellWidth, h11);
            return a + (b - a) * tx +
                ((c + (d - c) * tx) - (a + (b - a) * tx)) * tz;
        }
    }

    private static int FeatureFloor(int n, int size)
    {
        int q = n / size;
        return n % size < 0 ? q - 1 : q;
    }

    public DensityGrid BuildGrid(ChunkPos pos)
    {
        var grid = new DensityGrid();
        for (int gz = 0; gz < DensityGrid.GridWidth; gz++)
        for (int gx = 0; gx < DensityGrid.GridWidth; gx++)
        {
            int worldX = checked(pos.X * 16 + gx * CellWidth);
            int worldZ = checked(pos.Z * 16 + gz * CellWidth);
            double height = _terrain.Sample(worldX, worldZ).GroundY;
            for (int gy = 0; gy < DensityGrid.GridHeight; gy++)
                grid.SetCorner(gx, gy, gz, SampleWithHeight(worldX,
                    ChunkColumn.MinY + gy * CellHeight, worldZ, height));
        }
        return grid;
    }
}

/// <summary>
/// Interpolates a fixed 4x8x4 density lattice. The final corner on each edge
/// is shared by neighboring chunks because the sample uses absolute coordinates.
/// </summary>
public sealed class DensityGrid
{
    public const int GridWidth = 5; // 16/4 + 1
    public const int GridHeight = 49; // 384/8 + 1
    private readonly double[] _values = new double[GridWidth * GridWidth * GridHeight];
    private static int Index(int x, int y, int z) =>
        (y * GridWidth + z) * GridWidth + x;

    internal void SetCorner(int x, int y, int z, double value) =>
        _values[Index(x, y, z)] = value;
    public double Corner(int x, int y, int z) => _values[Index(x, y, z)];

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>
    /// Conservatively classify each 4x8x4 interpolation cell. Trilinear
    /// interpolation is a convex combination of eight corners, so a cell whose
    /// corners all have the same strict sign never crosses the solid/air
    /// threshold. This skips 8-corner interpolation for such cells WITHOUT
    /// changing v7 terrain or the interpolated field itself.
    /// 0=mixed, 1=solid, 2=air. Order: (cellY*4 + cellZ)*4 + cellX.
    /// </summary>
    public byte[] ClassifyCells()
    {
        var result = new byte[4 * 4 * 48];
        for (int cy = 0; cy < 48; cy++)
        for (int cz = 0; cz < 4; cz++)
        for (int cx = 0; cx < 4; cx++)
        {
            bool positive = true, nonpositive = true;
            for (int dy = 0; dy < 2; dy++)
            for (int dz = 0; dz < 2; dz++)
            for (int dx = 0; dx < 2; dx++)
            {
                double v = Corner(cx + dx, cy + dy, cz + dz);
                positive &= v > 0;
                nonpositive &= v <= 0;
            }
            result[(cy * 4 + cz) * 4 + cx] = (byte)(positive ? 1 : nonpositive ? 2 : 0);
        }
        return result;
    }

    /// <param name="localX">Block X, 0..16 (16 is a shared edge).</param>
    /// <param name="y">Absolute world Y, -64..320.</param>
    /// <param name="localZ">Block Z, 0..16 (16 is a shared edge).</param>
    public double Interpolate(int localX, int y, int localZ)
    {
        if (localX is < 0 or > 16 || localZ is < 0 or > 16 ||
            y < ChunkColumn.MinY || y > ChunkColumn.MaxY + 1)
            throw new ArgumentOutOfRangeException(nameof(localX), "Outside density lattice.");
        int cx = Math.Min(localX / 4, 3), cy = Math.Min((y - ChunkColumn.MinY) / 8, 47),
            cz = Math.Min(localZ / 4, 3);
        double tx = (localX - cx * 4) / 4.0,
               ty = (y - ChunkColumn.MinY - cy * 8) / 8.0,
               tz = (localZ - cz * 4) / 4.0;
        double a = Lerp(Corner(cx, cy, cz), Corner(cx + 1, cy, cz), tx);
        double b = Lerp(Corner(cx, cy, cz + 1), Corner(cx + 1, cy, cz + 1), tx);
        double c = Lerp(Corner(cx, cy + 1, cz), Corner(cx + 1, cy + 1, cz), tx);
        double d = Lerp(Corner(cx, cy + 1, cz + 1), Corner(cx + 1, cy + 1, cz + 1), tx);
        return Lerp(Lerp(a, b, tz), Lerp(c, d, tz), ty);
    }
}
