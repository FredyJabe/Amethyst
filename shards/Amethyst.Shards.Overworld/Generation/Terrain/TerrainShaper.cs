using Amethyst.Generation.Climate;
using Amethyst.Generation.Noise;

namespace Amethyst.Generation.Terrain;

/// <summary>
/// Output of the Phase 2 height-field shaper. Height is a continuous world-space
/// elevation; the chunk generator still owns integer rounding and block placement.
/// </summary>
public readonly record struct TerrainShape(
    double Height,
    double ContinentalBaseY,
    double MountainRelief,
    double RiverInfluence);

/// <summary>
/// Original, vanilla-inspired continental and relief shaping, NOT a copy of
/// Mojang's density functions. All inputs are immutable, deterministic and
/// sampled in absolute world coordinates so chunk generation order cannot
/// affect terrain. Version 3 uses this height field as the base for its 3D density.
/// </summary>
public sealed class TerrainShaper
{
    private readonly int _seaLevel;
    private readonly int _baseHeight;
    private readonly int _heightVariation;
    private readonly double _terrainScale;
    private readonly FractalNoise2D _relief, _ridgeTexture, _riverCourse;

    private readonly record struct CurvePoint(double X, double Y, double Slope);

    // Offsets from sea level. A shape-preserving cubic curve avoids visible
    // shelves at the transition from deep ocean -> coast -> inland plateaus.
    // Temperatures/humidity select biomes, not the rock elevation.
    private static readonly CurvePoint[] ContinentalCurve = BuildCurve(
    [
        (-1.00, -55.0), (-0.76, -46.0), (-0.56, -36.0),
        (-0.39, -25.0), (-0.25, -10.0), (-0.15, -2.0),
        (-0.06, 4.0), (0.10, 11.0), (0.28, 19.0),
        (0.50, 27.0), (0.78, 37.0), (1.00, 44.0)
    ]);

    public TerrainShaper(ServerOptions options)
    {
        _seaLevel = options.SeaLevel;
        _baseHeight = options.BaseHeight;
        _heightVariation = options.HeightVariation;
        _terrainScale = options.TerrainScale;
        _relief = new FractalNoise2D(options.Seed, 0x52736C696566UL, 4);
        _ridgeTexture = new FractalNoise2D(options.Seed, 0x526964676573UL, 3);
        _riverCourse = new FractalNoise2D(options.Seed, 0x526976657273UL, 3);
    }

    private static double SmoothStep(double lo, double hi, double value)
    {
        double t = Math.Clamp((value - lo) / (hi - lo), 0, 1);
        return t * t * (3 - 2 * t);
    }

    public TerrainShape Shape(int worldX, int worldZ, ClimateSample climate)
    {
        double c = climate.Continentalness;
        double erosion = climate.Erosion;
        double peaks = climate.PeaksAndValleys;
        double ruggedness = 1 - SmoothStep(-0.45, 0.70, erosion);
        double inland = SmoothStep(-0.22, 0.20, c);

        // The base-height setting changes inland elevations, but not the
        // location of the ocean surface and sea-level coastlines.
        double continentalBase = _seaLevel + Interpolate(ContinentalCurve, c)
            + (_baseHeight - 78) * SmoothStep(-0.15, 0.30, c);

        double reliefNoise = _relief.Sample(worldX * _terrainScale,
                                            worldZ * _terrainScale, 260);
        double rolling = reliefNoise * _heightVariation
            * (0.10 + 0.33 * ruggedness) * inland;

        // Erosion suppresses peaks and smooths plains. Folded weirdness
        // organizes mountain chains and valleys across large distances.
        double mountainFactor = SmoothStep(0.12, 0.51, c)
            * SmoothStep(0.18, 0.78, ruggedness)
            * SmoothStep(0.03, 0.84, peaks);
        double ridgeTexture = Math.Abs(_ridgeTexture.Sample(
            worldX * _terrainScale, worldZ * _terrainScale, 240));
        double mountainRelief = _heightVariation * 3.5 * mountainFactor
            * (0.65 + 0.35 * ridgeTexture);
        double valleyDepth = _heightVariation * 0.30 * inland
            * (0.4 + 0.6 * ruggedness) * (1 - SmoothStep(-0.65, -0.06, peaks));

        double height = continentalBase + rolling + mountainRelief - valleyDepth;

        // Zero contours of this independent, low-frequency field form long,
        // meandering drainage valleys (not yet a hydrological river network).
        // We carve shallow, sea-connected lowlands into water-bearing channels;
        // higher valleys can remain dry until aquifers and fluid flow exist.
        double channel = 1 - SmoothStep(0.012, 0.087,
            Math.Abs(_riverCourse.Sample(worldX * _terrainScale,
                                         worldZ * _terrainScale, 670)));
        double lowland = SmoothStep(-0.17, 0.025, c)
            * (1 - SmoothStep(0.35, 0.61, c));
        double influence = channel * lowland;
        double maxDepth = _heightVariation * 0.55;
        double depth = Math.Min(maxDepth, Math.Max(0, height - (_seaLevel - 3)));
        height -= influence * depth;

        return new TerrainShape(height, continentalBase, mountainRelief, influence);
    }

    private static CurvePoint[] BuildCurve((double X, double Y)[] points)
    {
        int n = points.Length;
        if (n < 2) throw new ArgumentException("A curve needs at least two knots.");
        var secants = new double[n - 1];
        var slopes = new double[n];
        for (int i = 0; i < n - 1; i++)
        {
            double dx = points[i + 1].X - points[i].X;
            if (dx <= 0) throw new ArgumentException("Curve knots must be sorted.");
            secants[i] = (points[i + 1].Y - points[i].Y) / dx;
        }
        slopes[0] = secants[0];
        slopes[n - 1] = secants[n - 2];
        for (int i = 1; i < n - 1; i++)
        {
            if (secants[i - 1] * secants[i] <= 0)
            {
                slopes[i] = 0;
                continue;
            }
            double dx0 = points[i].X - points[i - 1].X;
            double dx1 = points[i + 1].X - points[i].X;
            double w0 = 2 * dx1 + dx0, w1 = dx1 + 2 * dx0;
            slopes[i] = (w0 + w1) /
                (w0 / secants[i - 1] + w1 / secants[i]);
        }
        return Enumerable.Range(0, n)
            .Select(i => new CurvePoint(points[i].X, points[i].Y, slopes[i]))
            .ToArray();
    }

    private static double Interpolate(CurvePoint[] curve, double x)
    {
        if (x <= curve[0].X) return curve[0].Y;
        if (x >= curve[^1].X) return curve[^1].Y;
        for (int i = 0; i < curve.Length - 1; i++)
        {
            CurvePoint a = curve[i], b = curve[i + 1];
            if (x > b.X) continue;
            double dx = b.X - a.X, t = (x - a.X) / dx;
            double t2 = t * t, t3 = t2 * t;
            return (2 * t3 - 3 * t2 + 1) * a.Y
                + (t3 - 2 * t2 + t) * dx * a.Slope
                + (-2 * t3 + 3 * t2) * b.Y
                + (t3 - t2) * dx * b.Slope;
        }
        return curve[^1].Y;
    }
}
