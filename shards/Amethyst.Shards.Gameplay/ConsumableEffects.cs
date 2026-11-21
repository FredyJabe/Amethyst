using System.Text.Json;
using Amethyst.Shards;

namespace Amethyst;

/// <summary>Java 26.3 potion effect profiles for items which carry the
/// vanilla potion_contents component. The item data supplies the potion ID
/// and any custom effects; duration_scale is read from the same stack.
/// The registry of names and IDs is exact for protocol 777, while unusual
/// potion effects which need additional game systems remain server-state
/// only until their gameplay mechanics are connected.</summary>
public static class ConsumableEffects
{
    // Seconds at normal, extended and enhanced strengths, respectively.
    private sealed record PotionProfile(string Effect, int BaseSeconds,
        int ExtendedSeconds = 0, int StrongSeconds = 0, int StrongAmplifier = 1);

    private static readonly IReadOnlyDictionary<string, PotionProfile> Potions =
        new Dictionary<string, PotionProfile>(StringComparer.Ordinal)
        {
            ["night_vision"] = new("night_vision", 180, 480),
            ["invisibility"] = new("invisibility", 180, 480),
            ["leaping"] = new("jump_boost", 180, 480, 90),
            ["fire_resistance"] = new("fire_resistance", 180, 480),
            ["swiftness"] = new("speed", 180, 480, 90),
            ["slowness"] = new("slowness", 90, 240, 20, 3),
            ["water_breathing"] = new("water_breathing", 180, 480),
            ["healing"] = new("instant_health", 0, 0, 0),
            ["harming"] = new("instant_damage", 0, 0, 0),
            ["poison"] = new("poison", 45, 90, 21),
            ["regeneration"] = new("regeneration", 45, 90, 22),
            ["strength"] = new("strength", 180, 480, 90),
            ["weakness"] = new("weakness", 90, 240),
            ["slow_falling"] = new("slow_falling", 90, 240),
            ["turtle_master"] = new("resistance", 20, 40, 20, 3),
            ["luck"] = new("luck", 300),
            ["wind_charged"] = new("wind_charged", 180),
            ["weaving"] = new("weaving", 180),
            ["oozing"] = new("oozing", 180),
            ["infested"] = new("infested", 180)
        };

    public static IReadOnlyList<ShardConsumeEffect> BasePotion(string name)
    {
        if (!VanillaBuiltinIds.TryGet("minecraft:potion", name, out _))
            throw new InvalidDataException($"Unknown Java 26.3 potion: {name}.");
        if (name is "minecraft:water" or "minecraft:mundane" or
            "minecraft:thick" or "minecraft:awkward") return [];
        string key = name["minecraft:".Length..];
        bool extended = key.StartsWith("long_", StringComparison.Ordinal);
        bool strong = key.StartsWith("strong_", StringComparison.Ordinal);
        if (extended) key = key["long_".Length..];
        if (strong) key = key["strong_".Length..];
        if (!Potions.TryGetValue(key, out var data))
            throw new NotSupportedException(
                $"Unrecognized potion recipe effect mapping {name}.");
        int seconds = strong ? data.StrongSeconds :
            extended ? data.ExtendedSeconds : data.BaseSeconds;
        int amplifier = strong ? data.StrongAmplifier : 0;
        if (key == "turtle_master")
            return [
                new ShardConsumeEffect("minecraft:resistance",
                    seconds * 20, strong ? 3 : 2, 1),
                new ShardConsumeEffect("minecraft:slowness",
                    seconds * 20, strong ? 5 : 3, 1)
            ];
        return [new ShardConsumeEffect(
            "minecraft:" + data.Effect, seconds * 20, amplifier, 1)];
    }

    public static IReadOnlyList<ShardConsumeEffect> ForStack(
        ItemStack stack, IReadOnlyList<ShardConsumeEffect> defaultEffects)
    {
        var effects = new List<ShardConsumeEffect>(defaultEffects);
        var components = stack.Components;
        double scale = 1;
        if (components.TryGetValue("minecraft:potion_duration_scale",
            out string? encodedScale))
        {
            scale = JsonSerializer.Deserialize<double>(encodedScale);
            if (!double.IsFinite(scale) || scale is < 0 or > 16)
                throw new InvalidDataException("Invalid potion duration scale.");
        }
        if (components.TryGetValue("minecraft:potion_contents",
            out string? contents))
        {
            using var doc = JsonDocument.Parse(contents);
            if (doc.RootElement.TryGetProperty("potion", out var basePotion))
                effects.AddRange(BasePotion(basePotion.GetString()!));
            if (doc.RootElement.TryGetProperty("custom_effects",
                out var customEffects))
                foreach (var custom in customEffects.EnumerateArray())
                {
                    string id = custom.GetProperty("id").GetString() ?? "";
                    if (!VanillaBuiltinIds.TryGet("minecraft:mob_effect",
                        id, out _))
                        throw new InvalidDataException($"Unknown custom potion effect {id}.");
                    int duration = custom.TryGetProperty("duration", out var d)
                        ? d.GetInt32() : 0;
                    int amp = custom.TryGetProperty("amplifier", out var a)
                        ? a.GetInt32() : 0;
                    effects.Add(new ShardConsumeEffect(id, duration, amp, 1));
                }
        }
        if (components.TryGetValue("minecraft:suspicious_stew_effects",
            out string? stewContents))
        {
            using var doc = JsonDocument.Parse(stewContents);
            foreach (var custom in doc.RootElement.EnumerateArray())
                effects.Add(new ShardConsumeEffect(
                    custom.GetProperty("id").GetString()!,
                    custom.GetProperty("duration").GetInt32(), 0, 1));
        }
        return effects.Select(effect => effect with
            {
                DurationTicks = effect.DurationTicks <= 0 ? 0 :
                    (int)Math.Clamp(Math.Round(effect.DurationTicks * scale),
                        0, 72000)
            }).ToArray();
    }
}
