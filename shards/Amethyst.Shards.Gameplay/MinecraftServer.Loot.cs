using Amethyst.Shards;

namespace Amethyst;

/// <summary>All loot, whether emitted by a YAML skill or evaluated from the
/// Mojang 26.3 data pack, becomes authoritative persistent ground items here.
/// No client-reported drop is trusted.</summary>
public sealed partial class MinecraftServer
{
    private bool SpawnLoot(IEnumerable<ShardItemDrop> drops,
        double x, double y, double z)
    {
        if (_groundItems is null) return false;
        bool success = true;
        foreach (ShardItemDrop drop in drops)
        {
            if (drop.ItemId <= 0 || drop.Count <= 0 ||
                !ItemStackComponents.IsValid(drop.ComponentData))
            {
                Console.WriteLine($"[loot] Rejected invalid loot stack {drop.ItemId}.");
                success = false;
                continue;
            }
            var stack = new ItemStack(drop.ItemId, drop.Count)
            {
                ComponentData = drop.ComponentData
            };
            if (!_groundItems.TrySpawn(stack, x, y, z, out _))
            {
                Console.WriteLine($"[loot] Ground item capacity rejected {drop.ItemId} x{drop.Count}.");
                success = false;
            }
        }
        return success;
    }

    private IReadOnlyList<ShardItemDrop>? TryVanillaLoot(
        string tableId, ShardVanillaLootContext context)
    {
        if (_vanillaLoot?.HasTable(tableId) != true) return null;
        try { return _vanillaLoot.Roll(tableId, context); }
        catch (NotSupportedException ex)
        {
            // Use a clearly logged fallback until every 26.3 loot predicate,
            // function and parameter provider has a matching implementation.
            Console.WriteLine($"[loot] Incomplete {tableId}: {ex.Message}; " +
                "using the configured fallback.");
            return null;
        }
    }
}
