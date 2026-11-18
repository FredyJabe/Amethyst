namespace Amethyst.Generation.Noise;

/// <summary>Domain-separated seeded 3D fBm, sampled in absolute world coordinates.</summary>
public sealed class FractalNoise3D
{
    private readonly SeededGradientNoise[] _octaves;
    private readonly double[] _offsetX, _offsetY, _offsetZ;
    private readonly double _persistence;

    public FractalNoise3D(long seed, ulong domain, int octaves = 3, double persistence = 0.5)
    {
        if (octaves is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(octaves));
        if (!double.IsFinite(persistence) || persistence is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(persistence));
        _persistence = persistence;
        _octaves = new SeededGradientNoise[octaves];
        _offsetX = new double[octaves];
        _offsetY = new double[octaves];
        _offsetZ = new double[octaves];
        for (int i = 0; i < octaves; i++)
        {
            ulong salt = unchecked(domain + (ulong)i * 0x9e3779b97f4a7c15UL);
            _octaves[i] = new SeededGradientNoise(seed, salt);
            ulong key = SeededGradientNoise.Mix(unchecked((ulong)seed) ^ salt);
            // Avoid every seed evaluating to the same value at lattice origin.
            const double unit = 1.0 / 4294967296.0;
            _offsetX[i] = (uint)key * unit * 4096;
            key = SeededGradientNoise.Mix(key ^ 0xD3A5D3A5UL);
            _offsetY[i] = (uint)key * unit * 4096;
            _offsetZ[i] = (uint)(key >> 32) * unit * 4096;
        }
    }

    public double Sample(double x, double y, double z, double wavelength)
    {
        if (!double.IsFinite(wavelength) || wavelength <= 0)
            throw new ArgumentOutOfRangeException(nameof(wavelength));
        double frequency = 1 / wavelength, amplitude = 1, sum = 0, total = 0;
        for (int i = 0; i < _octaves.Length; i++)
        {
            sum += _octaves[i].Sample3D(x * frequency + _offsetX[i],
                    y * frequency + _offsetY[i], z * frequency + _offsetZ[i]) * amplitude;
            total += amplitude;
            frequency *= 2;
            amplitude *= _persistence;
        }
        return Math.Clamp(sum / total, -1, 1);
    }
}
