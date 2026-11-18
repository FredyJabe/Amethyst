namespace Amethyst.Generation.Climate;

/// <summary>
/// Five independent horizontal climate fields, normalized to [-1, 1].
/// Depth is intentionally not part of Phase 1; vertical cave biomes come later.
/// </summary>
public readonly record struct ClimateSample(
    double Temperature,
    double Humidity,
    double Continentalness,
    double Erosion,
    double Weirdness)
{
    /// <summary>Folded weirdness field, useful for later peak/valley shaping.</summary>
    public double PeaksAndValleys =>
        1 - 2 * Math.Abs(2 * Math.Abs(Weirdness) - 1);
}
