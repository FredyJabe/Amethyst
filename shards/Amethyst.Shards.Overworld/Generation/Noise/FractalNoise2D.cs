namespace Amethyst.Generation.Noise;

/// <summary>
/// Fractal Brownian Motion over independent, domain-separated gradient octaves.
/// The input wavelength is in world blocks. All instances are immutable.
/// </summary>
public sealed class FractalNoise2D
{
    private readonly SeededGradientNoise[] _octaves;
    private readonly double[] _offsetX, _offsetZ;
    private readonly double _persistence;

    public FractalNoise2D(long worldSeed, ulong domain, int octaves = 4,
                         double persistence = 0.5)
    {
        if (octaves is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(octaves));
        if (!double.IsFinite(persistence) || persistence is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(persistence));
        _persistence = persistence;
        _octaves = new SeededGradientNoise[octaves];
        _offsetX = new double[octaves];
        _offsetZ = new double[octaves];
        for (int i = 0; i < octaves; i++)
        {
            ulong salt = unchecked(domain + (ulong)i * 0x9e3779b97f4a7c15UL);
            _octaves[i] = new SeededGradientNoise(worldSeed, salt);
            // Coordinate offsets matter: raw gradient noise is zero at every
            // integer lattice corner, which would otherwise make all seeds
            // sample the same climate exactly at world (0, 0).
            ulong offsetKey = SeededGradientNoise.Mix(unchecked((ulong)worldSeed) ^ salt);
            const double divisor = 1.0 / 4294967296.0;
            _offsetX[i] = (uint)offsetKey * divisor * 4096;
            _offsetZ[i] = (uint)(offsetKey >> 32) * divisor * 4096;
        }
    }

    public double Sample(double x, double z, double wavelength)
    {
        if (!double.IsFinite(wavelength) || wavelength <= 0)
            throw new ArgumentOutOfRangeException(nameof(wavelength));
        double frequency = 1 / wavelength, amplitude = 1, sum = 0, weight = 0;
        for (int i = 0; i < _octaves.Length; i++)
        {
            sum += _octaves[i].Sample2D(x * frequency + _offsetX[i],
                                        z * frequency + _offsetZ[i]) * amplitude;
            weight += amplitude;
            frequency *= 2;
            amplitude *= _persistence;
        }
        return Math.Clamp(sum / weight, -1, 1);
    }
}
