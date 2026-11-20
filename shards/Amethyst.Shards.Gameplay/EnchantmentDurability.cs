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
        var active = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Definition Enchantments are defaults; stack-level values override.
        if (definition.Components is not null &&
            definition.Components.TryGetValue("minecraft:enchantments", out string? encoded))
        {
            using var data = JsonDocument.Parse(encoded);
            if (data.RootElement.ValueKind == JsonValueKind.Object &&
                !data.RootElement.TryGetProperty("WireHex", out _) &&
                !data.RootElement.TryGetProperty("Remove", out _))
                foreach (var property in data.RootElement.EnumerateObject())
                    active[property.Name] = property.Value.GetInt32();
        }
        foreach (var (id, level) in stack.AllEnchantments())
            active[id] = level;
        var runtime = new ShardSkillRuntime();
        runtime.SetFlag(armor ? "armor" : "non_armor");
        foreach (var (id, level) in active)
        {
            if (runtime.IsCancelled) break;
            if (level <= 0 || !enchantments.TryGet(id, out _)) continue;
            var evt = new ShardSkillEvent(id, "onItemDamage", Random: random,
                ItemId: stack.ItemId, Count: stack.Count, EnchantmentLevel: level)
                { Runtime = runtime };
            skills.DispatchImmediate(evt);
        }
        // Cancellation applies only to this single attempted wear point.
        return runtime.IsCancelled;
    }
}
