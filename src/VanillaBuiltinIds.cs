namespace Amethyst;

/// <summary>Java Edition 26.3 static built-in registry entries, taken from
/// Mojang's generated registries.json report for protocol 777. Unlike the
/// configuration Registry Data packets, potion and mob_effect use fixed
/// builtin network IDs. Do not infer an ID from array ordering in an
/// unrelated Registry Data packet.</summary>
public static class VanillaBuiltinIds
{
    private static readonly IReadOnlyDictionary<string,
        IReadOnlyDictionary<string, int>> Builtin =
        new Dictionary<string, IReadOnlyDictionary<string, int>>(
            StringComparer.Ordinal)
        {
        ["minecraft:potion"] = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["minecraft:water"] = 0,
            ["minecraft:mundane"] = 1,
            ["minecraft:thick"] = 2,
            ["minecraft:awkward"] = 3,
            ["minecraft:night_vision"] = 4,
            ["minecraft:long_night_vision"] = 5,
            ["minecraft:invisibility"] = 6,
            ["minecraft:long_invisibility"] = 7,
            ["minecraft:leaping"] = 8,
            ["minecraft:long_leaping"] = 9,
            ["minecraft:strong_leaping"] = 10,
            ["minecraft:fire_resistance"] = 11,
            ["minecraft:long_fire_resistance"] = 12,
            ["minecraft:swiftness"] = 13,
            ["minecraft:long_swiftness"] = 14,
            ["minecraft:strong_swiftness"] = 15,
            ["minecraft:slowness"] = 16,
            ["minecraft:long_slowness"] = 17,
            ["minecraft:strong_slowness"] = 18,
            ["minecraft:turtle_master"] = 19,
            ["minecraft:long_turtle_master"] = 20,
            ["minecraft:strong_turtle_master"] = 21,
            ["minecraft:water_breathing"] = 22,
            ["minecraft:long_water_breathing"] = 23,
            ["minecraft:healing"] = 24,
            ["minecraft:strong_healing"] = 25,
            ["minecraft:harming"] = 26,
            ["minecraft:strong_harming"] = 27,
            ["minecraft:poison"] = 28,
            ["minecraft:long_poison"] = 29,
            ["minecraft:strong_poison"] = 30,
            ["minecraft:regeneration"] = 31,
            ["minecraft:long_regeneration"] = 32,
            ["minecraft:strong_regeneration"] = 33,
            ["minecraft:strength"] = 34,
            ["minecraft:long_strength"] = 35,
            ["minecraft:strong_strength"] = 36,
            ["minecraft:weakness"] = 37,
            ["minecraft:long_weakness"] = 38,
            ["minecraft:luck"] = 39,
            ["minecraft:slow_falling"] = 40,
            ["minecraft:long_slow_falling"] = 41,
            ["minecraft:wind_charged"] = 42,
            ["minecraft:weaving"] = 43,
            ["minecraft:oozing"] = 44,
            ["minecraft:infested"] = 45,
        },
        ["minecraft:mob_effect"] = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["minecraft:speed"] = 0,
            ["minecraft:slowness"] = 1,
            ["minecraft:haste"] = 2,
            ["minecraft:mining_fatigue"] = 3,
            ["minecraft:strength"] = 4,
            ["minecraft:instant_health"] = 5,
            ["minecraft:instant_damage"] = 6,
            ["minecraft:jump_boost"] = 7,
            ["minecraft:nausea"] = 8,
            ["minecraft:regeneration"] = 9,
            ["minecraft:resistance"] = 10,
            ["minecraft:fire_resistance"] = 11,
            ["minecraft:water_breathing"] = 12,
            ["minecraft:invisibility"] = 13,
            ["minecraft:blindness"] = 14,
            ["minecraft:night_vision"] = 15,
            ["minecraft:hunger"] = 16,
            ["minecraft:weakness"] = 17,
            ["minecraft:poison"] = 18,
            ["minecraft:wither"] = 19,
            ["minecraft:health_boost"] = 20,
            ["minecraft:absorption"] = 21,
            ["minecraft:saturation"] = 22,
            ["minecraft:glowing"] = 23,
            ["minecraft:levitation"] = 24,
            ["minecraft:luck"] = 25,
            ["minecraft:unluck"] = 26,
            ["minecraft:slow_falling"] = 27,
            ["minecraft:conduit_power"] = 28,
            ["minecraft:dolphins_grace"] = 29,
            ["minecraft:bad_omen"] = 30,
            ["minecraft:hero_of_the_village"] = 31,
            ["minecraft:darkness"] = 32,
            ["minecraft:trial_omen"] = 33,
            ["minecraft:raid_omen"] = 34,
            ["minecraft:wind_charged"] = 35,
            ["minecraft:weaving"] = 36,
            ["minecraft:oozing"] = 37,
            ["minecraft:infested"] = 38,
            ["minecraft:breath_of_the_nautilus"] = 39,
        }
        };

    public static bool TryGet(string registry, string identifier, out int id)
    {
        if (Builtin.TryGetValue(registry, out var entries) &&
            entries.TryGetValue(identifier, out id)) return true;
        id = -1;
        return false;
    }

    public static int IndexOf(string registry, string identifier) =>
        TryGet(registry, identifier, out int index) ? index :
            throw new InvalidDataException(
                $"Unknown Java 26.3 builtin registry entry {registry}/{identifier}.");
}
