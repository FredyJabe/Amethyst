using Amethyst.Generation.Noise;

namespace Amethyst.Generation.Climate;

/// <summary>
/// Simple 3D cave-biome selector, isolated from block density and the surface
/// biome resolver. This does not duplicate vanilla's multi-noise parameter map.
/// </summary>
public sealed class CaveBiomeSampler
{
    private readonly FractalNoise2D _wetness, _mineral;
    private readonly FractalNoise3D _deep;
    private readonly int _lush, _dripstone, _deepDark;

    public CaveBiomeSampler(long seed)
    {
        _wetness = new FractalNoise2D(seed, 0x4341564542494f57UL, 3);
        _mineral = new FractalNoise2D(seed, 0x4341564542494f4dUL, 3);
        _deep = new FractalNoise3D(seed, 0x4341564542494f44UL, 2);
        _lush = VanillaRegistries.IndexOf("minecraft:worldgen/biome", "minecraft:lush_caves");
        _dripstone = VanillaRegistries.IndexOf("minecraft:worldgen/biome", "minecraft:dripstone_caves");
        _deepDark = VanillaRegistries.IndexOf("minecraft:worldgen/biome", "minecraft:deep_dark");
    }

    public int Resolve(int x, int y, int z, int surfaceY, int surfaceBiome)
    {
        if (y >= surfaceY - 20 || y > 56)
            return surfaceBiome;
        if (y < -30 && _deep.Sample(x, y, z, 128) > 0.30)
            return _deepDark;
        if (y > -44 && _wetness.Sample(x, z, 310) > 0.17)
            return _lush;
        if (_mineral.Sample(x, z, 260) < -0.18)
            return _dripstone;
        return surfaceBiome;
    }
}
