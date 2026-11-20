using System.Text.Json;
using Amethyst.Shards;

namespace Amethyst;

/// <summary>Runs the configured ~onItemDamage Skills before each attempted
/// point of wear. Invoked synchronously while inventory transactions lock
/// their stack state; no asynchronous mechanics may suspend here.</summary>
public sealed class EnchantmentDurability(
    IShardItemRegistry items, IShardEnchantmentRegistry enchantments,
    IShardSkillEngine skills)
{
    public bool PreventsWear(ItemStack stack, bool armor, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (stack.Count != 1 || stack.ItemId <= 0 ||
            !items.TryGetById(stack.ItemId, out var definition))
            return false;
        var active = GetLevels(definition, stack);
        var runtime = new ShardSkillRuntime();
        runtime.SetFlag(armor ? "armor" : "non_armor");
        foreach (var (id, level) in active)
        {
            if (level <= 0 || !enchantments.TryGet(id, out _)) continue;
            var evt = new ShardSkillEvent(id, "onItemDamage", Random: random,
                ItemId: stack.ItemId, Count: stack.Count, EnchantmentLevel: level)
                { Runtime = runtime };
            skills.DispatchImmediate(evt);
        }
        // All enchantment skills have run. Cancellation applies only to
        // this single attempted wear point.
        return runtime.IsCancelled;
    }

    /// <summary>Determine whether and how quickly an equipped item mends
    /// from collected XP. This is driven entirely by ~onExperiencePickup
    /// Skills and mending_repair_per_xp Stats, rather than a hard-coded
    /// enchantment check or a fixed two-point multiplier.</summary>
    public int MendingRate(ItemStack stack)
    {
        if (stack.Count != 1 || stack.ItemId <= 0 ||
            !items.TryGetById(stack.ItemId, out var definition))
            return 0;
        var runtime = new ShardSkillRuntime();
        foreach (var (id, level) in GetLevels(definition, stack))
        {
            if (level <= 0 || !enchantments.TryGet(id, out var data)) continue;
            var evt = new ShardSkillEvent(id, "onExperiencePickup",
                ItemId: stack.ItemId, Count: stack.Count, EnchantmentLevel: level)
                { Runtime = runtime };
            skills.DispatchImmediate(evt);
            foreach (var stat in data.Stats)
                stat.Apply(evt, skills);
        }
        return runtime.HasFlag("mending") && !runtime.IsCancelled
            ? (int)Math.Clamp(runtime.Number("mending_repair_per_xp"), 0, 64)
            : 0;
    }

    private static IReadOnlyDictionary<string, int> GetLevels(
        ShardItemDefinition definition, ItemStack stack)
    {
        var active = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        static void ApplyLevels(Dictionary<string, int> active, string? encoded)
        {
            if (encoded is null) return;
            using var json = JsonDocument.Parse(encoded);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return;
            if (json.RootElement.TryGetProperty("Remove", out _))
            {
                active.Clear();
                return;
            }
            if (json.RootElement.TryGetProperty("WireHex", out _)) return;
            foreach (JsonProperty entry in json.RootElement.EnumerateObject())
                active[entry.Name] = entry.Value.GetInt32();
        }
        if (definition.Components is not null &&
            definition.Components.TryGetValue("minecraft:enchantments", out var baseLevels))
            ApplyLevels(active, baseLevels);
        if (stack.Components.TryGetValue("minecraft:enchantments", out var stackLevels))
            ApplyLevels(active, stackLevels);
        foreach (var (id, level) in stack.AllEnchantments())
            active[id] = level;
        return active;
    }
}
