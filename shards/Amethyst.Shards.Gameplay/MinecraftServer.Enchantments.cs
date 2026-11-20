using System.Text.Json;
using Amethyst.Shards;

namespace Amethyst;

/// <summary>Shared gameplay adapter for enchantment effects. Only the server
/// creates trigger events, and it consumes the returned numbers/effect intents
/// after packet/reach/world validations have succeeded.</summary>
public sealed partial class MinecraftServer
{
    internal async Task<ShardSkillRuntime> EnchantmentEventAsync(
        ItemStack item, string trigger, CancellationToken ct,
        ShardSkillRuntime? runtime = null, string? targetKind = null,
        string? damageType = null, Guid? actor = null)
    {
        runtime ??= new ShardSkillRuntime();
        if (_skills is null || _enchantments is null || item.Count == 0)
            return runtime;

        var active = new Dictionary<string, int>(StringComparer.Ordinal);
        // The definition's Enchantments component provides base levels.
        // Instance enchantments override those defaults.
        if (_items is not null && _items.TryGetById(item.ItemId, out var definition) &&
            definition.Components is not null &&
            definition.Components.TryGetValue("minecraft:enchantments", out string? encoded))
        {
            using var data = JsonDocument.Parse(encoded);
            if (data.RootElement.ValueKind == JsonValueKind.Object &&
                !data.RootElement.TryGetProperty("WireHex", out _) &&
                !data.RootElement.TryGetProperty("Remove", out _))
                foreach (var property in data.RootElement.EnumerateObject())
                    active[property.Name] = property.Value.GetInt32();
        }
        foreach (var (id, level) in item.AllEnchantments())
            active[id] = level;
        foreach (var (id, level) in active)
        {
            if (level <= 0 || !_enchantments.TryGet(id, out _)) continue;
            var evt = new ShardSkillEvent(id, trigger, ActorUuid: actor,
                ItemId: item.ItemId, Count: item.Count,
                EnchantmentLevel: level, TargetKind: targetKind,
                DamageType: damageType) { Runtime = runtime };
            await _skills.DispatchAsync(evt, ct);
        }
        return runtime;
    }

    internal static string EnchantmentMobCategory(string? kind)
    {
        if (kind is null) return "player";
        string key = kind.StartsWith("minecraft:", StringComparison.Ordinal) ?
            kind["minecraft:".Length..] : kind;
        return key switch
        {
            "spider" or "cave_spider" or "silverfish" or "endermite" or "bee" =>
                "arthropod",
            "zombie" or "husk" or "drowned" or "skeleton" or
            "stray" or "wither_skeleton" or "bogged" or "wither" or
            "zombified_piglin" or "phantom" => "undead",
            _ => "other"
        };
    }
}
