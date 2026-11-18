using Amethyst;

namespace Amethyst.Generation.Climate;

public readonly record struct BiomeSelection(string Name, int BiomeId, SurfaceKind Surface);

/// <summary>
/// Climate/elevation-to-biome lookup. Terrain geometry comes from an independent
/// TerrainShaper when generatorVersion is 2; version 1 retains legacy heights.
/// This is intentionally NOT Mojang's exact multi-noise parameter table.
/// </summary>
public sealed class BiomeResolver
{
    private readonly Dictionary<string, int> _ids;
    private readonly string? _forcedName;

    public BiomeResolver(string biomeSetting = "auto")
    {
        string[] supported =
        [
            "plains", "sunflower_plains", "forest", "flower_forest", "dappled_forest",
            "jungle", "sparse_jungle", "desert", "savanna", "savanna_plateau",
            "taiga", "snowy_taiga", "old_growth_pine_taiga", "snowy_plains",
            "snowy_slopes", "stony_peaks", "jagged_peaks", "frozen_peaks",
            "windswept_hills", "windswept_forest", "meadow", "grove",
            "swamp", "mangrove_swamp", "badlands", "beach", "snowy_beach",
            "stony_shore", "ocean", "warm_ocean", "cold_ocean", "frozen_ocean",
            "deep_ocean", "deep_cold_ocean", "deep_frozen_ocean",
            "river", "frozen_river"
        ];
        _ids = supported.ToDictionary(name => name,
            name => VanillaRegistries.IndexOf("minecraft:worldgen/biome", "minecraft:" + name));
        if (biomeSetting != "auto")
        {
            if (!biomeSetting.StartsWith("minecraft:", StringComparison.Ordinal))
                throw new ArgumentException("Use 'auto' or a minecraft: biome identifier.", nameof(biomeSetting));
            // A forced biome may be any valid 26.3 biome, not only those in the
            // automatic surface-biome table.
            _forcedName = biomeSetting["minecraft:".Length..];
            _ = VanillaRegistries.IndexOf("minecraft:worldgen/biome", biomeSetting);
        }
    }

    public BiomeSelection Resolve(ClimateSample climate, int groundY,
        int seaLevel, int baseHeight, int heightVariation,
        bool spawnProtected = false, bool river = false)
    {
        if (_forcedName is { } forced)
            return Select(forced, Surface(forced));
        if (spawnProtected) return Select("plains", SurfaceKind.Grass);
        if (river)
            return Select(climate.Temperature < -0.53 ? "frozen_river" : "river",
                SurfaceKind.Riverbed);

        // We only have 2D geometry so far. Use actual ground vs. sea level for
        // beaches and oceans to avoid an ocean biome on an exposed mountaintop.
        double temp = climate.Temperature - Math.Max(0, groundY - 88) * 0.006;
        double wet = climate.Humidity;
        double cont = climate.Continentalness;
        double erosion = climate.Erosion;
        double peaks = climate.PeaksAndValleys;

        if (groundY < seaLevel - 4)
        {
            bool deep = groundY < seaLevel - 20;
            if (temp < -0.55) return Select(deep ? "deep_frozen_ocean" : "frozen_ocean");
            if (temp < -0.2) return Select(deep ? "deep_cold_ocean" : "cold_ocean");
            // Continentalness also shapes coastlines in generatorVersion 2.
            if (!deep && temp > 0.48 && cont < 0.12) return Select("warm_ocean");
            return Select(deep ? "deep_ocean" : "ocean");
        }
        if (groundY <= seaLevel + 2)
        {
            if (temp < -0.45) return Select("snowy_beach");
            if (erosion < -0.55) return Select("stony_shore");
            return Select("beach");
        }

        double mountainThreshold = baseHeight + Math.Max(22, heightVariation * 0.7);
        if (groundY > mountainThreshold && (erosion < 0.36 || peaks > 0.1))
        {
            if (groundY > mountainThreshold + 18 && erosion < -0.12)
                return Select(temp < -0.2 ? "frozen_peaks" : "jagged_peaks");
            if (temp < -0.28) return Select("snowy_slopes");
            if (temp < 0.02) return Select("grove");
            return Select("stony_peaks");
        }
        if (groundY > baseHeight + 14 && erosion < -0.15)
            return Select(temp < -0.30 ? "grove" : wet > 0.1 ? "windswept_forest" : "windswept_hills");
        if (groundY > baseHeight + 7 && peaks > 0.55 && wet > -0.1)
            return Select("meadow");

        if (temp < -0.44)
            return Select(wet > 0.05 ? "snowy_taiga" : "snowy_plains");
        if (temp < -0.20)
            return Select(wet > 0.31 ? "old_growth_pine_taiga" : "taiga");
        if (temp > 0.46 && wet < -0.15)
            return Select(wet < -0.43 ? "badlands" : "desert");
        if (temp > 0.38 && wet > 0.35)
            return Select(wet > 0.57 ? "jungle" : "sparse_jungle");
        if (temp > 0.20 && wet < 0.13)
            return Select(peaks > 0.4 ? "savanna_plateau" : "savanna");
        if (wet > 0.53 && groundY < seaLevel + 8)
            return Select(temp > 0.32 ? "mangrove_swamp" : "swamp");
        if (wet > 0.32)
            return Select(temp < 0 ? "dappled_forest" : "forest");
        if (wet > 0.16)
            return Select(wet > 0.25 ? "flower_forest" : "forest");
        return Select(peaks > 0.55 ? "sunflower_plains" : "plains");
    }

    private BiomeSelection Select(string name, SurfaceKind? overrideSurface = null)
    {
        int id = _ids.TryGetValue(name, out int cached) ? cached :
            VanillaRegistries.IndexOf("minecraft:worldgen/biome", "minecraft:" + name);
        return new BiomeSelection("minecraft:" + name, id,
            overrideSurface ?? Surface(name));
    }

    private static SurfaceKind Surface(string name) => name switch
    {
        "desert" or "badlands" or "beach" or "snowy_beach" or "ocean" or
        "cold_ocean" or "frozen_ocean" or "warm_ocean" or "deep_ocean" or
        "deep_cold_ocean" or "deep_frozen_ocean" => SurfaceKind.Sand,
        "snowy_plains" or "snowy_taiga" or "snowy_slopes" or "grove" or
        "frozen_peaks" => SurfaceKind.Snow,
        "stony_peaks" or "jagged_peaks" or "stony_shore" => SurfaceKind.Rock,
        _ => SurfaceKind.Grass
    };
}
