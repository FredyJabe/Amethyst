using Amethyst.Shards;

namespace Amethyst;

/// <summary>Server-authoritative held item consumption. A client use packet
/// starts a timed action; an early release cancels it. Completion validates
/// that exactly the original item stack is still held, so a swapped stack
/// cannot produce free food, effects or remainder items.</summary>
public sealed partial class MinecraftServer
{
    private readonly object _consumeGate = new();
    private readonly Dictionary<Guid, PendingConsume> _pendingConsumes = [];
    private sealed record PendingConsume(int Slot, ItemStack Stack,
        ShardConsumableDefinition Definition, long StartedAt);

    internal async Task<bool> HandleConsumableUseAsync(
        ConnectedPlayer player, UseItemAction action, CancellationToken ct)
    {
        if (!player.Ready || player.GameMode == PlayerGameMode.Spectator ||
            _consumables is null) return false;
        int slot = action.Hand == 0 ? player.Inventory.SelectedSlot : 45;
        ItemStack stack = player.Inventory.Snapshot().Slots[slot];
        if (stack.Count <= 0 ||
            !_consumables.TryGet(stack.ItemId, out var consumable))
            return false;
        if (player.Vitals.Snapshot().Food < 20 || consumable.AlwaysEdible)
        {
            lock (_consumeGate)
            {
                if (!_pendingConsumes.TryGetValue(player.PlayerUuid, out var prior) ||
                    prior.Slot != slot || prior.Stack != stack)
                    _pendingConsumes[player.PlayerUuid] = new PendingConsume(
                        slot, stack, consumable, Environment.TickCount64);
            }
        }
        await BlockInteractionProtocol.SendAckAsync(player.Connection,
            action.Sequence, ct);
        return true;
    }

    private void CancelConsumableUse(Guid uuid)
    {
        lock (_consumeGate) _pendingConsumes.Remove(uuid);
    }

    internal async Task TickConsumablesAsync(CancellationToken ct,
        long? nowMilliseconds = null)
    {
        long now = nowMilliseconds ?? Environment.TickCount64;
        PendingConsume[] pending;
        Guid[] owners;
        lock (_consumeGate)
        {
            owners = _pendingConsumes.Keys.ToArray();
            pending = _pendingConsumes.Values.ToArray();
        }
        for (int i = 0; i < owners.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            Guid owner = owners[i];
            PendingConsume use = pending[i];
            if (now - use.StartedAt < (long)use.Definition.DurationTicks * 50)
                continue;
            lock (_consumeGate)
            {
                if (!_pendingConsumes.TryGetValue(owner, out var current) ||
                    current != use) continue;
                _pendingConsumes.Remove(owner);
            }
            ConnectedPlayer? player = _players.Snapshot().FirstOrDefault(
                x => x.PlayerUuid == owner && x.Ready);
            if (player is null || player.GameMode == PlayerGameMode.Spectator)
                continue;
            ItemStack held = player.Inventory.Snapshot().Slots[use.Slot];
            if (held != use.Stack || held.Count == 0 ||
                use.Slot != 45 && player.Inventory.SelectedSlot != use.Slot ||
                use.Definition.Nutrition > 0 &&
                player.Vitals.Snapshot().Food == 20 &&
                !use.Definition.AlwaysEdible) continue;

            var completedEvent = await EnchantmentEventAsync(held,
                "onConsume", ct, actor: player.PlayerUuid);
            if (completedEvent.IsCancelled) continue;

            bool creative = player.GameMode == PlayerGameMode.Creative;
            if (!creative && !player.Inventory.TryConsumeExactSlot(
                use.Slot, held)) continue;
            bool accepted = creative || player.Vitals.TryEat(
                use.Definition.Nutrition, use.Definition.Saturation,
                use.Definition.AlwaysEdible);
            if (!accepted)
            {
                // Avoid consuming an item that no longer qualifies.
                if (!creative && player.Inventory.GiveStack(
                    held with { Count = 1 }) != 1)
                    SpawnLoot([new ShardItemDrop(held.ItemId, 1)
                        { ComponentData = held.ComponentData }],
                        player.Pose.X, player.Pose.Y + 0.5, player.Pose.Z);
                continue;
            }
            if (!creative && use.Definition.RemainderItemId > 0)
            {
                var remainder = new ItemStack(
                    use.Definition.RemainderItemId, 1);
                if (player.Inventory.GiveStack(remainder) != 1)
                    SpawnLoot([new ShardItemDrop(remainder.ItemId, 1)],
                        player.Pose.X, player.Pose.Y + 0.5, player.Pose.Z);
            }

            // Effect recipients are server-owned. The next stage connects
            // these validated effect definitions to a complete status engine.
            await ApplyConsumedEffectsAsync(player,
                ConsumableEffects.ForStack(held, use.Definition.Effects), ct);
            await InventoryProtocol.SendContentAsync(player.Connection,
                player.Inventory, ct);
            await CombatProtocol.SendHealthAsync(player, ct);
        }
    }
}
