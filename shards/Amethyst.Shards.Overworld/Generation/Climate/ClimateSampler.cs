using Amethyst.Generation.Noise;

namespace Amethyst.Generation.Climate;

/// <summary>
/// Five separately seeded, multi-octave climate fields. Low-frequency domain
/// warping avoids a rigid noise-grid appearance. Scale changes climate-region
/// size; versions 2/3 also use these fields to shape base terrain height.
/// </summary>
public sealed class ClimateSampler
{
    private readonly double _scale;
    private readonly FractalNoise2D _warpX, _warpZ;
    private readonly FractalNoise2D _temperature, _humidity, _continentalness,
        _erosion, _weirdness;

    public ClimateSampler(long seed, double climateScale = 1)
    {
        if (!double.IsFinite(climateScale) || climateScale is < 0.25 or > 8)
            throw new ArgumentOutOfRangeException(nameof(climateScale));
        _scale = climateScale;
        _warpX = new FractalNoise2D(seed, 0x1010UL, 2);
        _warpZ = new FractalNoise2D(seed, 0x2020UL, 2);
        _temperature = new FractalNoise2D(seed, 0x51001UL, 4);
        _humidity = new FractalNoise2D(seed, 0x51002UL, 4);
        _continentalness = new FractalNoise2D(seed, 0x51003UL, 5);
        _erosion = new FractalNoise2D(seed, 0x51004UL, 4);
        _weirdness = new FractalNoise2D(seed, 0x51005UL, 4);
    }

    public ClimateSample Sample(int worldX, int worldZ)
    {
        // The warp uses absolute world coordinates. Adjacent chunks therefore
        // agree even if generated in a different order or on different threads.
        double x = worldX * _scale, z = worldZ * _scale;
        double warpedX = x + _warpX.Sample(x, z, 3200) * 128;
        double warpedZ = z + _warpZ.Sample(x, z, 3200) * 128;
        static double Normalize(double value) => Math.Clamp(value * 2.4, -1, 1);
        return new ClimateSample(
            Normalize(_temperature.Sample(warpedX, warpedZ, 1100)),
            Normalize(_humidity.Sample(warpedX, warpedZ, 900)),
            Normalize(_continentalness.Sample(warpedX, warpedZ, 2200)),
            Normalize(_erosion.Sample(warpedX, warpedZ, 1350)),
            Normalize(_weirdness.Sample(warpedX, warpedZ, 950)));
    }
}
