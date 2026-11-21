using Amethyst.Shards;

namespace Amethyst;

/// <summary>Authoritative equipment transitions and protocol-777 offhand swaps.
/// No predicted client stack or changed-slot hash supplies an item identity.</summary>
public sealed partial class MinecraftServer
{
    private static readonly int[] EquipmentSlots = [5, 6, 7, 8, 45];

    private async Task BroadcastEquipmentAsync(ConnectedPlayer subject,
        IReadOnlyList<EntityEquipment> changed, CancellationToken ct)
    {
        if (changed.Count == 0) return;
        foreach (ConnectedPlayer observer in _players.Snapshot().Where(p =>
            p.Ready && p.ConnectionId != subject.ConnectionId))
        {
            try
            {
                await EquipmentProtocol.SendAsync(observer.Connection,
                    subject.EntityId, changed, _items, ct);
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Hotbar scroll changes the visible main-hand item, but is not
    /// the dedicated F-key ~onSwap event.</summary>
    internal async Task BroadcastSelectedEquipmentAsync(
        ConnectedPlayer player, int oldSelectedSlot, CancellationToken ct)
    {
        if (!player.Ready || oldSelectedSlot == player.Inventory.SelectedSlot)
            return;
        ItemStack held = player.Inventory.Held();
        await BroadcastEquipmentAsync(player,
            [new EntityEquipment(0, held)], ct);
    }

    private static bool SameEquipment(ItemStack before, ItemStack after) =>
        before.ItemId == after.ItemId &&
        before.Count == after.Count &&
        before.Damage == after.Damage &&
        before.Unbreakable == after.Unbreakable &&
        before.Unbreaking == after.Unbreaking &&
        before.Mending == after.Mending &&
        before.RepairCost == after.RepairCost &&
        string.Equals(before.EnchantmentData, after.EnchantmentData,
            StringComparison.Ordinal) &&
        string.Equals(before.ComponentData, after.ComponentData,
            StringComparison.Ordinal);

    /// <summary>Durability and stack-count changes need packet updates but
    /// are not equip transitions; metadata/identity changes are.</summary>
    private static bool SameEquippedIdentity(ItemStack before, ItemStack after) =>
        before.ItemId == after.ItemId &&
        before.Unbreaking == after.Unbreaking &&
        before.Mending == after.Mending &&
        before.Unbreakable == after.Unbreakable &&
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
        var changed = new List<EntityEquipment>();
        int mainSlot = player.Inventory.SelectedSlot;
        if (!SameEquipment(previous[mainSlot], current[mainSlot]))
            changed.Add(new EntityEquipment(0, current[mainSlot]));
        foreach (int slot in EquipmentSlots)
        {
            ItemStack removed = previous[slot], added = current[slot];
            if (SameEquipment(removed, added)) continue;
            byte equipmentId = slot switch
            {
                45 => 1, 8 => 2, 7 => 3, 6 => 4, 5 => 5,
                _ => throw new InvalidOperationException("Bad equipment slot.")
            };
            changed.Add(new EntityEquipment(equipmentId, added));
            if (!SameEquippedIdentity(removed, added))
            {
                if (removed.Count > 0)
                    await EnchantmentEventAsync(removed, "onUnequip", ct,
                        actor: player.PlayerUuid);
                if (added.Count > 0)
                    await EnchantmentEventAsync(added, "onEquip", ct,
                        actor: player.PlayerUuid);
            }
        }
        await BroadcastEquipmentAsync(player, changed, ct);
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
