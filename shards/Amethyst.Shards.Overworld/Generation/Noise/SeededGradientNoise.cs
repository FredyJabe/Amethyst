namespace Amethyst.Generation.Noise;

/// <summary>
/// Platform-independent, seeded gradient noise in 2D and 3D. Coordinates are
/// continuous WORLD coordinates (not chunk-local). No mutable random state:
/// call order and thread scheduling cannot change the results.
/// This is our own noise implementation, not vanilla's Perlin/NormalNoise.
/// </summary>
public sealed class SeededGradientNoise
{
    private readonly ulong _key;

    public SeededGradientNoise(long worldSeed, ulong domain)
    {
        _key = Mix(unchecked((ulong)worldSeed) ^ Mix(domain));
    }

    internal static ulong Mix(ulong n)
    {
        unchecked
        {
            n ^= n >> 30;
            n *= 0xbf58476d1ce4e5b9UL;
            n ^= n >> 27;
            n *= 0x94d049bb133111ebUL;
            return n ^ (n >> 31);
        }
    }

    private ulong Hash(long x, long y, long z)
    {
        unchecked
        {
            return Mix(_key ^
                (unchecked((ulong)x) * 0x9e3779b185ebca87UL) ^
                (unchecked((ulong)y) * 0xd6e8feb86659fd93UL) ^
                (unchecked((ulong)z) * 0xc2b2ae3d27d4eb4fUL));
        }
    }

    private static double Fade(double t) =>
        t * t * t * (t * (t * 6 - 15) + 10);
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double Dot2(ulong hash, double x, double z)
    {
        const double diagonal = 0.70710678118654752440;
        return (hash & 7UL) switch
        {
            0 => x, 1 => -x, 2 => z, 3 => -z,
            4 => diagonal * (x + z), 5 => diagonal * (-x + z),
            6 => diagonal * (x - z), _ => diagonal * (-x - z)
        };
    }

    private static double Dot3(ulong hash, double x, double y, double z) =>
        (hash % 12UL) switch
        {
            0 => x + y, 1 => -x + y, 2 => x - y, 3 => -x - y,
            4 => x + z, 5 => -x + z, 6 => x - z, 7 => -x - z,
            8 => y + z, 9 => -y + z, 10 => y - z, _ => -y - z
        };

    public double Sample2D(double x, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z))
            throw new ArgumentOutOfRangeException(nameof(x), "Noise coordinates must be finite.");
        long ix = (long)Math.Floor(x), iz = (long)Math.Floor(z);
        double dx = x - ix, dz = z - iz;
        double fx = Fade(dx), fz = Fade(dz);
        double a = Lerp(Dot2(Hash(ix, 0, iz), dx, dz),
                        Dot2(Hash(ix + 1, 0, iz), dx - 1, dz), fx);
        double b = Lerp(Dot2(Hash(ix, 0, iz + 1), dx, dz - 1),
                        Dot2(Hash(ix + 1, 0, iz + 1), dx - 1, dz - 1), fx);
        return Math.Clamp(Lerp(a, b, fz) * 1.4142135623730951, -1, 1);
    }

    public double Sample3D(double x, double y, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
            throw new ArgumentOutOfRangeException(nameof(x), "Noise coordinates must be finite.");
        long ix = (long)Math.Floor(x), iy = (long)Math.Floor(y), iz = (long)Math.Floor(z);
        double dx = x - ix, dy = y - iy, dz = z - iz;
        double fx = Fade(dx), fy = Fade(dy), fz = Fade(dz);
        double a00 = Lerp(Dot3(Hash(ix, iy, iz), dx, dy, dz),
                          Dot3(Hash(ix + 1, iy, iz), dx - 1, dy, dz), fx);
        double a10 = Lerp(Dot3(Hash(ix, iy + 1, iz), dx, dy - 1, dz),
                          Dot3(Hash(ix + 1, iy + 1, iz), dx - 1, dy - 1, dz), fx);
        double a01 = Lerp(Dot3(Hash(ix, iy, iz + 1), dx, dy, dz - 1),
                          Dot3(Hash(ix + 1, iy, iz + 1), dx - 1, dy, dz - 1), fx);
        double a11 = Lerp(Dot3(Hash(ix, iy + 1, iz + 1), dx, dy - 1, dz - 1),
                          Dot3(Hash(ix + 1, iy + 1, iz + 1), dx - 1, dy - 1, dz - 1), fx);
        return Math.Clamp(Lerp(Lerp(a00, a10, fy), Lerp(a01, a11, fy), fz), -1, 1);
    }
}
