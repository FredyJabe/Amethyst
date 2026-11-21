using Amethyst.Shards;

namespace Amethyst;

/// <summary>Authoritative equipment transitions and protocol-777 offhand swaps.
/// No predicted client stack or changed-slot hash supplies an item identity.</summary>
public sealed partial class MinecraftServer
{
    private static readonly int[] EquipmentSlots = [5, 6, 7, 8, 45];

    private static bool SameEquipment(ItemStack before, ItemStack after) =>
        before.ItemId == after.ItemId &&
        before.Damage == after.Damage &&
        before.Unbreakable == after.Unbreakable &&
        before.Unbreaking == after.Unbreaking &&
        before.Mending == after.Mending &&
        before.RepairCost == after.RepairCost &&
        string.Equals(before.EnchantmentData, after.EnchantmentData,
            StringComparison.Ordinal) &&
        string.Equals(before.ComponentData, after.ComponentData,
            StringComparison.Ordinal);

    /// <summary>Runs on server-accepted inventory transactions only.
    /// These are post-commit notifications; the server never trusts the
    /// packet's proposed changes as evidence of a successful equip.</summary>
    internal async Task NotifyEquipmentChangesAsync(ConnectedPlayer player,
        ItemStack[] previous, CancellationToken ct)
    {
        ItemStack[] current = player.Inventory.Snapshot().Slots;
        foreach (int slot in EquipmentSlots)
        {
            ItemStack removed = previous[slot], added = current[slot];
            if (SameEquipment(removed, added)) continue;
            if (removed.Count > 0)
                await EnchantmentEventAsync(removed, "onUnequip", ct,
                    actor: player.PlayerUuid);
            if (added.Count > 0)
                await EnchantmentEventAsync(added, "onEquip", ct,
                    actor: player.PlayerUuid);
        }
    }

    /// <summary>The F key is a cancellable pre-commit onSwap event. All
    /// participating item/enchantment skills finish before the decision.
    /// A rejected swap leaves inventory and persisted state unchanged.</summary>
    internal async Task<bool> HandleOffhandSwapAsync(ConnectedPlayer player,
        CancellationToken ct)
    {
        if (!player.Ready || player.GameMode == PlayerGameMode.Spectator)
            return false;
        ItemStack[] before = player.Inventory.Snapshot().Slots;
        ItemStack main = before[player.Inventory.SelectedSlot], offhand = before[45];
        if (main == offhand) return false;
        var runtime = new ShardSkillRuntime();
        await EnchantmentEventAsync(main, "onSwap", ct, runtime,
            actor: player.PlayerUuid);
        await EnchantmentEventAsync(offhand, "onSwap", ct, runtime,
            actor: player.PlayerUuid);
        if (runtime.IsCancelled || !player.Inventory.TrySwapOffhand())
            return false;
        await NotifyEquipmentChangesAsync(player, before, ct);
        await InventoryProtocol.SendContentAsync(player.Connection,
            player.Inventory, ct);
        return true;
    }
}
